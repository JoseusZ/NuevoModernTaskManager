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
    public class SystemMonitor : IDisposable
    {
        private readonly ProcessService _processService;
        private readonly Dictionary<int, ProcessInfo> _processMap;
        private readonly object _lock = new object();

        private ulong _previousSystemKernelTime;
        private ulong _previousSystemUserTime;

        public ObservableCollection<ProcessInfo> Processes { get; }

        private readonly SynchronizationContext? _syncContext;
        private bool _disposed;

        // Cache de iconos: Clave = Ruta/Nombre, Valor = (Handle, Contador de Referencias)
        private readonly Dictionary<string, (IntPtr hIcon, int refCount)> _iconCache = new();

        public SystemMonitor() : this(SynchronizationContext.Current)
        {
        }

        public SystemMonitor(SynchronizationContext? syncContext)
        {
            _processService = new ProcessService();
            Processes = new ObservableCollection<ProcessInfo>();
            _processMap = new Dictionary<int, ProcessInfo>();
            _syncContext = syncContext;

            if (Kernel32.GetSystemTimes(out _, out var kernelTime, out var userTime))
            {
                _previousSystemKernelTime = kernelTime.ToULong();
                _previousSystemUserTime = userTime.ToULong();
            }
        }

        private void RunOnSyncContext(Action action)
        {
            if (action == null) return;
            if (_syncContext != null)
            {
                _syncContext.Post(_ => {
                    try { action(); } catch { }
                }, null);
            }
            else
            {
                try { action(); } catch { }
            }
        }

        // --- GESTIÓN DE ICONOS (Reference Counting) ---

        private IntPtr AcquireIconForProcess(ProcessInfo p)
        {
            // Usamos la ruta completa o el nombre como clave
            string key = !string.IsNullOrEmpty(p.CommandLine) ? p.CommandLine : p.Name;

            lock (_iconCache) // Proteger acceso concurrente al diccionario de iconos
            {
                if (_iconCache.TryGetValue(key, out var entry))
                {
                    // Icono ya existe: incrementamos referencia y devolvemos el mismo handle
                    _iconCache[key] = (entry.hIcon, entry.refCount + 1);
                    return entry.hIcon;
                }

                // Icono nuevo: intentamos extraerlo
                IntPtr hIcon = IconHelper.TryGetProcessIcon(p.Pid);
                if (hIcon != IntPtr.Zero)
                {
                    _iconCache[key] = (hIcon, 1);
                    return hIcon;
                }
            }
            return IntPtr.Zero;
        }

        private void ReleaseIconForProcess(ProcessInfo p)
        {
            if (p.IconHandle == IntPtr.Zero) return;

            string key = !string.IsNullOrEmpty(p.CommandLine) ? p.CommandLine : p.Name;

            lock (_iconCache)
            {
                if (_iconCache.TryGetValue(key, out var entry))
                {
                    int newCount = entry.refCount - 1;
                    if (newCount <= 0)
                    {
                        // Nadie más usa este icono: destruirlo para liberar RAM GDI
                        IconHelper.DestroyIconSafe(entry.hIcon);
                        _iconCache.Remove(key);
                    }
                    else
                    {
                        // Todavía en uso: solo bajar contador
                        _iconCache[key] = (entry.hIcon, newCount);
                    }
                }
            }
        }

        public void Update()
        {
            if (_disposed) return;

            if (!Kernel32.GetSystemTimes(out _, out var ftKernel, out var ftUser)) return;
            ulong currentKernelTime = ftKernel.ToULong();
            ulong currentUserTime = ftUser.ToULong();
            ulong deltaSystemTime = (currentKernelTime - _previousSystemKernelTime) + (currentUserTime - _previousSystemUserTime);

            _previousSystemKernelTime = currentKernelTime;
            _previousSystemUserTime = currentUserTime;

            List<ProcessInfo> currentProcessList;
            try { currentProcessList = _processService.GetProcesses(); } catch { return; }

            var appWindows = WindowHelper.GetAppProcesses();
            var pidsThisCycle = new HashSet<int>();
            var uiActions = new List<Action>();

            lock (_lock)
            {
                foreach (var procData in currentProcessList)
                {
                    pidsThisCycle.Add(procData.Pid);

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
                        // --- ACTUALIZAR EXISTENTE ---
                        uiActions.Add(() =>
                        {
                            try
                            {
                                ulong prevProcTime = (ulong)existingProcess.KernelTime + (ulong)existingProcess.UserTime;
                                ulong currentProcTime = (ulong)procData.KernelTime + (ulong)procData.UserTime;
                                ulong deltaProc = (currentProcTime > prevProcTime) ? currentProcTime - prevProcTime : 0UL;

                                existingProcess.CpuUsage = (deltaSystemTime > 0) ? (deltaProc / (double)deltaSystemTime) * 100.0 : 0.0;

                                long deltaRead = procData.DiskReadBytes - existingProcess.DiskReadBytes;
                                long deltaWrite = procData.DiskWriteBytes - existingProcess.DiskWriteBytes;
                                existingProcess.DiskReadSpeed = deltaRead < 0 ? 0 : deltaRead;
                                existingProcess.DiskWriteSpeed = deltaWrite < 0 ? 0 : deltaWrite;
                                existingProcess.DiskReadBytes = procData.DiskReadBytes;
                                existingProcess.DiskWriteBytes = procData.DiskWriteBytes;

                                existingProcess.WorkingSetSize = procData.WorkingSetSize;
                                existingProcess.PrivatePageCount = procData.PrivatePageCount;
                                existingProcess.ThreadCount = procData.ThreadCount;
                                existingProcess.HandleCount = procData.HandleCount;
                                existingProcess.Category = procData.Category;
                                existingProcess.MainWindowTitle = procData.MainWindowTitle;
                                existingProcess.Architecture = procData.Architecture;

                                existingProcess.KernelTime = procData.KernelTime;
                                existingProcess.UserTime = procData.UserTime;
                            }
                            catch { }
                        });
                    }
                    else
                    {
                        // --- NUEVO PROCESO ---
                        procData.CpuUsage = 0.0;
                        procData.DiskReadSpeed = 0;
                        procData.DiskWriteSpeed = 0;

                        // 1. Obtener Línea de Comandos (WMI)
                        try
                        {
                            procData.CommandLine = SystemInfoHelper.GetProcessCommandLine(procData.Pid);
                        }
                        catch { procData.CommandLine = ""; }

                        // 2. Obtener Icono (Usando Caché)
                        procData.IconHandle = AcquireIconForProcess(procData);

                        uiActions.Add(() =>
                        {
                            try
                            {
                                _processMap[procData.Pid] = procData;
                                Processes.Add(procData);
                            }
                            catch { }
                        });
                    }
                }

                // --- LIMPIEZA DE PROCESOS MUERTOS ---
                var pidsToRemove = _processMap.Keys.Where(pid => !pidsThisCycle.Contains(pid)).ToList();
                foreach (var pid in pidsToRemove)
                {
                    if (_processMap.TryGetValue(pid, out var procToRemove))
                    {
                        // Liberar icono antes de quitar de la lista
                        ReleaseIconForProcess(procToRemove);

                        uiActions.Add(() =>
                        {
                            try
                            {
                                Processes.Remove(procToRemove);
                                _processMap.Remove(pid);
                            }
                            catch { }
                        });
                    }
                }
            }

            // Ejecutar lote de cambios en UI
            if (uiActions.Count > 0)
            {
                RunOnSyncContext(() =>
                {
                    foreach (var a in uiActions) try { a(); } catch { }
                });
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RunOnSyncContext(() =>
            {
                try { Processes.Clear(); } catch { }
            });

            lock (_lock)
            {
                // Limpiar cache de iconos nativos
                lock (_iconCache)
                {
                    foreach (var kv in _iconCache.Values)
                        IconHelper.DestroyIconSafe(kv.hIcon);
                    _iconCache.Clear();
                }
                _processMap.Clear();
            }

            try { (_processService as IDisposable)?.Dispose(); } catch { }
        }
    }
}