// En: ModernTaskManager.Core/Services/SystemMonitor.cs

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ModernTaskManager.Core.Helpers;
using ModernTaskManager.Core.Models;
using ModernTaskManager.Core.Native;

namespace ModernTaskManager.Core.Services
{
    public class SystemMonitor : IDisposable
    {
        private readonly ProcessService _processService;

        // Mapa para recordar el estado anterior de cada proceso
        private Dictionary<int, ProcessInfo> _processMap;

        private ulong _previousSystemKernelTime;
        private ulong _previousSystemUserTime;

        public ObservableCollection<ProcessInfo> Processes { get; }

        public SystemMonitor()
        {
            _processService = new ProcessService();
            Processes = new ObservableCollection<ProcessInfo>();
            _processMap = new Dictionary<int, ProcessInfo>();

            if (Kernel32.GetSystemTimes(out _, out var kernelTime, out var userTime))
            {
                _previousSystemKernelTime = kernelTime.ToULong();
                _previousSystemUserTime = userTime.ToULong();
            }
        }

        public void Update()
        {
            // 1. Tiempo Sistema (CPU)
            if (!Kernel32.GetSystemTimes(out _, out var ftKernel, out var ftUser)) return;
            ulong currentKernelTime = ftKernel.ToULong();
            ulong currentUserTime = ftUser.ToULong();
            ulong deltaSystemTime = (currentKernelTime - _previousSystemKernelTime) + (currentUserTime - _previousSystemUserTime);

            _previousSystemKernelTime = currentKernelTime;
            _previousSystemUserTime = currentUserTime;

            // 2. Obtener lista cruda de procesos
            List<ProcessInfo> currentProcessList;
            try { currentProcessList = _processService.GetProcesses(); } catch { return; }

            // 3. Obtener lista de "Aplicaciones" (Ventanas visibles)
            var appWindows = WindowHelper.GetAppProcesses();
            var pidsThisCycle = new HashSet<int>();

            foreach (var procData in currentProcessList)
            {
                pidsThisCycle.Add(procData.Pid);
                ulong totalProcessTime = (ulong)procData.KernelTime + (ulong)procData.UserTime;

                // Determinar Categoría y Título
                if (appWindows.ContainsKey(procData.Pid))
                {
                    procData.Category = ProcessCategory.Application;
                    procData.MainWindowTitle = appWindows[procData.Pid];
                }
                else
                {
                    procData.Category = ProcessCategory.Background;
                    procData.MainWindowTitle = "";
                }

                if (_processMap.TryGetValue(procData.Pid, out var existingProcess))
                {
                    // --- PROCESO EXISTENTE ---

                    // A) CPU
                    ulong prevProcTime = (ulong)existingProcess.KernelTime + (ulong)existingProcess.UserTime;
                    ulong deltaProc = (totalProcessTime > prevProcTime) ? totalProcessTime - prevProcTime : 0;
                    if (deltaSystemTime > 0)
                        existingProcess.CpuUsage = (deltaProc / (double)deltaSystemTime) * 100.0;

                    // B) Disco
                    long deltaRead = procData.DiskReadBytes - existingProcess.DiskReadBytes;
                    long deltaWrite = procData.DiskWriteBytes - existingProcess.DiskWriteBytes;
                    existingProcess.DiskReadSpeed = deltaRead < 0 ? 0 : deltaRead;
                    existingProcess.DiskWriteSpeed = deltaWrite < 0 ? 0 : deltaWrite;
                    existingProcess.DiskReadBytes = procData.DiskReadBytes;
                    existingProcess.DiskWriteBytes = procData.DiskWriteBytes;

                    // C) Datos Básicos
                    existingProcess.WorkingSetSize = procData.WorkingSetSize;
                    existingProcess.PrivatePageCount = procData.PrivatePageCount;
                    existingProcess.ThreadCount = procData.ThreadCount;
                    existingProcess.HandleCount = procData.HandleCount;

                    // Actualizar agrupación (puede cambiar)
                    existingProcess.Category = procData.Category;
                    existingProcess.MainWindowTitle = procData.MainWindowTitle;

                    // NOTA: No actualizamos CommandLine ni Architecture aquí porque son estáticos.
                    // Solo copiamos la arquitectura si cambió el objeto procData (raro)
                    existingProcess.Architecture = procData.Architecture;

                    existingProcess.KernelTime = procData.KernelTime;
                    existingProcess.UserTime = procData.UserTime;
                }
                else
                {
                    // --- NUEVO PROCESO ---
                    procData.CpuUsage = 0.0;
                    procData.DiskReadSpeed = 0;
                    procData.DiskWriteSpeed = 0;

                    // *** Carga Diferida de WMI (Optimización) ***
                    // Solo consultamos la línea de comandos una vez al inicio.
                    procData.CommandLine = SystemInfoHelper.GetProcessCommandLine(procData.Pid);

                    _processMap[procData.Pid] = procData;
                    Processes.Add(procData);
                }
            }

            // 4. Limpieza
            var pidsToRemove = new List<int>();
            foreach (var pid in _processMap.Keys)
            {
                if (!pidsThisCycle.Contains(pid)) pidsToRemove.Add(pid);
            }

            foreach (var pid in pidsToRemove)
            {
                if (_processMap.TryGetValue(pid, out var procToRemove))
                {
                    Processes.Remove(procToRemove);
                    _processMap.Remove(pid);
                }
            }
        }

        public void Dispose() { }
    }
}