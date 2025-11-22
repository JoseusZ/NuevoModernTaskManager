// ModernTaskManager.Core/Services/SystemMonitor.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using ModernTaskManager.Core.Helpers;
using ModernTaskManager.Core.Models;
using ModernTaskManager.Core.Native;

namespace ModernTaskManager.Core.Services
{
    /// <summary>
    /// SystemMonitor: recoge información periódica de procesos (vía ProcessService)
    /// y aplica los cambios en la colección <see cref="Processes"/> en el
    /// SynchronizationContext proporcionado (habitualmente el hilo UI).
    /// 
    /// Si no se provee un SynchronizationContext en el constructor, se intentará
    /// capturar SynchronizationContext.Current. Si no existe ninguno, las actualizaciones
    /// se ejecutarán en el hilo que llame a Update() (fallback).
    /// </summary>
    public class SystemMonitor : IDisposable
    {
        private readonly ProcessService _processService;

        // Mapa para recordar el estado anterior de cada proceso
        private readonly Dictionary<int, ProcessInfo> _processMap;
        private readonly object _lock = new object();

        private ulong _previousSystemKernelTime;
        private ulong _previousSystemUserTime;

        /// <summary>
        /// Colección pública enlazable (UI). NUNCA debe modificarse desde un hilo background
        /// — this class garantiza que las modificaciones se realicen en el sync context.
        /// </summary>
        public ObservableCollection<ProcessInfo> Processes { get; }

        private readonly SynchronizationContext? _syncContext;
        private bool _disposed;

        // Añadir cache simple y liberar iconos al remover procesos.
        private readonly Dictionary<string, (IntPtr hIcon, int refCount)> _iconCache = new();

        /// <summary>
        /// Crea SystemMonitor intentando capturar SynchronizationContext.Current (p. ej. hilo UI).
        /// Si creas la instancia desde la UI, el contexto será el correcto y las actualizaciones se
        /// harán en el hilo UI. Si la instancia se crea desde un hilo background, pásale explícitamente
        /// el syncContext (por ejemplo: SynchronizationContext.Current o un wrapper).
        /// </summary>
        public SystemMonitor() : this(SynchronizationContext.Current)
        {
        }

        /// <summary>
        /// Constructor que acepta un SynchronizationContext opcional.
        /// </summary>
        /// <param name="syncContext">SynchronizationContext donde aplicar los cambios (UI thread).</param>
        public SystemMonitor(SynchronizationContext? syncContext)
        {
            _processService = new ProcessService();
            Processes = new ObservableCollection<ProcessInfo>();
            _processMap = new Dictionary<int, ProcessInfo>();
            _syncContext = syncContext;

            // Inicializar tiempos de sistema (si es posible)
            if (Kernel32.GetSystemTimes(out _, out var kernelTime, out var userTime))
            {
                _previousSystemKernelTime = kernelTime.ToULong();
                _previousSystemUserTime = userTime.ToULong();
            }
        }

        /// <summary>
        /// Ejecuta una acción en el contexto sincronizado (usualmente UI). Si no hay syncContext,
        /// ejecuta la acción inmediatamente en el hilo actual (fallback).
        /// </summary>
        private void RunOnSyncContext(Action action)
        {
            if (action == null) return;
            if (_syncContext != null)
            {
                try
                {
                    _syncContext.Post(_ => {
                        try { action(); }
                        catch { /* no propagar excepciones a caller */ }
                    }, null);
                }
                catch
                {
                    // Fallback: intentar ejecutar directamente
                    try { action(); } catch { }
                }
            }
            else
            {
                // No se puede publicar al UI thread: ejecutar en el hilo actual (caller).
                // Este fallback se usa cuando la instancia fue creada sin sync context.
                try { action(); } catch { }
            }
        }

        private IntPtr AcquireIconForProcess(ProcessInfo p)
        {
            // Intentar obtener ruta para clave. Si no hay ruta, usar nombre.
            string key = p.CommandLine;
            if (string.IsNullOrEmpty(key))
                key = p.Name;

            if (_iconCache.TryGetValue(key, out var entry))
            {
                _iconCache[key] = (entry.hIcon, entry.refCount + 1);
                return entry.hIcon;
            }

            IntPtr hIcon = IconHelper.TryGetProcessIcon(p.Pid);
            if (hIcon != IntPtr.Zero)
            {
                _iconCache[key] = (hIcon, 1);
                return hIcon;
            }
            return IntPtr.Zero;
        }

        private void ReleaseIconForProcess(ProcessInfo p)
        {
            string key = p.CommandLine;
            if (string.IsNullOrEmpty(key))
                key = p.Name;

            if (_iconCache.TryGetValue(key, out var entry))
            {
                int newCount = entry.refCount - 1;
                if (newCount <= 0)
                {
                    IconHelper.DestroyIconSafe(entry.hIcon);
                    _iconCache.Remove(key);
                }
                else
                {
                    _iconCache[key] = (entry.hIcon, newCount);
                }
            }
        }

        /// <summary>
        /// Actualiza el estado de procesos: calcula deltas y prepara acciones para aplicar en la UI.
        /// </summary>
        public void Update()
        {
            if (_disposed) return;

            // 1. Tiempo Sistema (CPU)
            if (!Kernel32.GetSystemTimes(out _, out var ftKernel, out var ftUser)) return;
            ulong currentKernelTime = ftKernel.ToULong();
            ulong currentUserTime = ftUser.ToULong();

            // deltaSystemTime en unidades coherentes con FILETIME->ticks (100-ns)
            ulong deltaSystemTime = (currentKernelTime - _previousSystemKernelTime) + (currentUserTime - _previousSystemUserTime);
            _previousSystemKernelTime = currentKernelTime;
            _previousSystemUserTime = currentUserTime;

            // 2. Obtener lista cruda de procesos (puede lanzar; capturamos)
            List<ProcessInfo> currentProcessList;
            try
            {
                currentProcessList = _processService.GetProcesses();
            }
            catch
            {
                // Si falla la obtención de procesos, no hacemos nada en este ciclo.
                return;
            }

            // 3. Obtener lista de "Aplicaciones" (Ventanas visibles) - puede ser costoso pero lo hacemos.
            var appWindows = WindowHelper.GetAppProcesses();
            var pidsThisCycle = new HashSet<int>();

            // Lista de acciones que se aplicarán en batch en el sync context (UI).
            var uiActions = new List<Action>();

            lock (_lock)
            {
                // Procesamos cada proceso crudo y preparamos una acción para aplicar en la UI.
                foreach (var procData in currentProcessList)
                {
                    pidsThisCycle.Add(procData.Pid);

                    // Determinar Categoría y Título
                    if (appWindows.ContainsKey(procData.Pid))
                    {
                        procData.Category = ProcessCategory.Application;
                        procData.MainWindowTitle = appWindows[procData.Pid];
                    }
                    else
                    {
                        procData.Category = ProcessCategory.Background;
                        procData.MainWindowTitle = string.Empty;
                    }

                    if (_processMap.TryGetValue(procData.Pid, out var existingProcess))
                    {
                        // --- PROCESO EXISTENTE ---
                        // Preparamos una acción que actualice las propiedades del objeto existente
                        uiActions.Add(() =>
                        {
                            try
                            {
                                // A) CPU
                                // Convertimos KernelTime/UserTime (long) a ulong para cálculo de delta
                                ulong prevProcTime = (ulong)existingProcess.KernelTime + (ulong)existingProcess.UserTime;
                                ulong currentProcTime = (ulong)procData.KernelTime + (ulong)procData.UserTime;
                                ulong deltaProc = (currentProcTime > prevProcTime) ? currentProcTime - prevProcTime : 0UL;
                                if (deltaSystemTime > 0)
                                {
                                    // cpuUsage como porcentaje del tiempo de sistema
                                    existingProcess.CpuUsage = (deltaProc / (double)deltaSystemTime) * 100.0;
                                }
                                else
                                {
                                    existingProcess.CpuUsage = 0.0;
                                }

                                // B) Disco (velocidad en bytes desde último muestreo)
                                long deltaRead = procData.DiskReadBytes - existingProcess.DiskReadBytes;
                                long deltaWrite = procData.DiskWriteBytes - existingProcess.DiskWriteBytes;
                                existingProcess.DiskReadSpeed = deltaRead < 0 ? 0 : deltaRead;
                                existingProcess.DiskWriteSpeed = deltaWrite < 0 ? 0 : deltaWrite;
                                existingProcess.DiskReadBytes = procData.DiskReadBytes;
                                existingProcess.DiskWriteBytes = procData.DiskWriteBytes;

                                // C) Datos básicos
                                existingProcess.WorkingSetSize = procData.WorkingSetSize;
                                existingProcess.PrivatePageCount = procData.PrivatePageCount;
                                existingProcess.ThreadCount = procData.ThreadCount;
                                existingProcess.HandleCount = procData.HandleCount;

                                // Actualizar agrupación (puede cambiar)
                                existingProcess.Category = procData.Category;
                                existingProcess.MainWindowTitle = procData.MainWindowTitle;

                                // Copiar arquitectura/commandline si cambió (raro pero seguro)
                                existingProcess.Architecture = procData.Architecture;
                                if (!string.IsNullOrEmpty(procData.CommandLine) && existingProcess.CommandLine != procData.CommandLine)
                                    existingProcess.CommandLine = procData.CommandLine;

                                // Actualizar tiempos para siguiente ciclo
                                existingProcess.KernelTime = procData.KernelTime;
                                existingProcess.UserTime = procData.UserTime;
                            }
                            catch
                            {
                                // No propagamos excepciones hacia Update()
                            }
                        });
                    }
                    else
                    {
                        // --- NUEVO PROCESO ---
                        // Normalizamos valores iniciales y preparamos agregarlo a la colección y al mapa
                        procData.CpuUsage = 0.0;
                        procData.DiskReadSpeed = 0;
                        procData.DiskWriteSpeed = 0;

                        // Carga diferida de CommandLine (optimización) -- puede devolver empty si falla
                        try
                        {
                            procData.CommandLine = SystemInfoHelper.GetProcessCommandLine(procData.Pid);
                        }
                        catch { procData.CommandLine = string.Empty; }

                        // Asignar icono
                        try { procData.IconHandle = AcquireIconForProcess(procData); } catch { procData.IconHandle = IntPtr.Zero; }

                        uiActions.Add(() =>
                        {
                            try
                            {
                                // Añadir a mapa y colección en UI thread
                                _processMap[procData.Pid] = procData;
                                Processes.Add(procData);
                            }
                            catch
                            {
                                // ignore
                            }
                        });
                    }
                }

                // 4. Limpieza: detectar procesos que desaparecieron y prepararlos para remover en UI
                var pidsToRemove = _processMap.Keys.Where(pid => !pidsThisCycle.Contains(pid)).ToList();
                foreach (var pid in pidsToRemove)
                {
                    if (_processMap.TryGetValue(pid, out var procToRemove))
                    {
                        uiActions.Add(() =>
                        {
                            try
                            {
                                Processes.Remove(procToRemove);
                                _processMap.Remove(pid);
                            }
                            catch
                            {
                                // ignore
                            }
                        });
                    }
                }
            } // lock

            // 5. Aplicar todas las actualizaciones en el sync context (UI) de una sola vez
            if (uiActions.Count > 0)
            {
                RunOnSyncContext(() =>
                {
                    foreach (var a in uiActions)
                    {
                        try { a(); } catch { /* proteger loop */ }
                    }
                });
            }
        }

        /// <summary>
        /// Dispose: limpia colecciones y recursos nativos.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Limpieza: vaciar la colección en el sync context para no causar cross-thread issues
            RunOnSyncContext(() =>
            {
                try
                {
                    Processes.Clear();
                }
                catch { }
            });

            // Liberar mapa interno
            lock (_lock)
            {
                foreach (var kv in _iconCache.Values)
                    IconHelper.DestroyIconSafe(kv.hIcon);
                _iconCache.Clear();

                _processMap.Clear();
            }

            // Si ProcessService implementa IDisposable, disposearlo (defensivo)
            try
            {
                (_processService as IDisposable)?.Dispose();
            }
            catch { }
        }
    }
}
