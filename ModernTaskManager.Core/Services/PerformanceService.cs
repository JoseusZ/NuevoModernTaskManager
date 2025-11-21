// En: ModernTaskManager.Core/Services/PerformanceService.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ModernTaskManager.Core.Helpers;
using ModernTaskManager.Core.Native; // Necesario para Kernel32 y sus estructuras

namespace ModernTaskManager.Core.Services
{
    // Estructura para datos de Memoria (RAM)
    public struct MemoryUsageInfo
    {
        public ulong TotalPhysicalBytes { get; set; }
        public ulong UsedPhysicalBytes { get; set; }

        // Campos detallados para mayor precisión (como el Task Manager real)
        public ulong AvailableBytes { get; set; }
        public ulong CommittedBytes { get; set; } // Memoria "Confirmada"
        public ulong CommitLimit { get; set; }    // Límite de carga total

        public double UsedPercentage => (TotalPhysicalBytes == 0) ? 0 : (UsedPhysicalBytes / (double)TotalPhysicalBytes) * 100.0;
    }

    // Estructura para datos de Disco
    public struct DiskUsageInfo
    {
        public double ActiveTimePercent { get; set; }
        public double ReadBytesPerSec { get; set; }
        public double WriteBytesPerSec { get; set; }
    }

    // Estructura para datos de Red
    public struct NetworkUsageInfo
    {
        public double BytesSentPerSec { get; set; }
        public double BytesReceivedPerSec { get; set; }
        public double BandwidthBytesPerSec { get; set; } // Capacidad total de la red
        public double TotalBytesPerSec => BytesSentPerSec + BytesReceivedPerSec;
    }

    // Estructura para datos de GPU
    public struct GpuUsageInfo
    {
        public double GpuUsagePercent { get; set; } // Carga del núcleo 3D
        public ulong DedicatedMemoryUsed { get; set; } // VRAM usada
        public ulong DedicatedMemoryTotal { get; set; } // VRAM total (usando D3DKMT)
    }

    public class PerformanceService : IDisposable
    {
        // Contadores de rendimiento del sistema
        private PerformanceCounter _cpuCounter;
        private PerformanceCounter _memCounter; // Se usa como fallback
        private PerformanceCounter _diskIdleTimeCounter;
        private PerformanceCounter _diskReadCounter;
        private PerformanceCounter _diskWriteCounter;

        // Red
        private readonly List<PerformanceCounter> _networkSentCounters = new();
        private readonly List<PerformanceCounter> _networkReceivedCounters = new();
        private readonly List<PerformanceCounter> _networkBandwidthCounters = new();

        // GPU (Listas dinámicas)
        private readonly List<PerformanceCounter> _gpu3dCounters = new();
        private PerformanceCounter _gpuVramCounter;
        private string _gpuCategoryName;
        private string _gpuMemCategoryName;
        private int _gpuCounterRefreshTick = 0;

        private bool _isDisposed;
        private ulong _totalMemory;

        public PerformanceService()
        {
            try
            {
                _totalMemory = SystemInfoHelper.GetTotalPhysicalMemory();

                InitializeCpu();
                InitializeMemory();
                InitializeDisk();
                InitializeNetwork();
                InitializeGpu(); // ¡NUEVO!

                // Lectura inicial para "calentar" los contadores
                WarmUpCounters();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fatal en PerformanceService: {ex.Message}");
            }
        }

        private void InitializeCpu()
        {
            try { _cpuCounter = new PerformanceCounter("Procesador", "% de tiempo de procesador", "_Total"); }
            catch { _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
        }

        private void InitializeMemory()
        {
            try { _memCounter = new PerformanceCounter("Memoria", "Bytes disponibles"); }
            catch { _memCounter = new PerformanceCounter("Memory", "Available Bytes"); }
        }

        private void InitializeDisk()
        {
            string catEs = "Disco físico";
            string catEn = "PhysicalDisk";

            try
            {
                _diskIdleTimeCounter = new PerformanceCounter(catEs, "% de tiempo inactivo", "_Total");
                _diskReadCounter = new PerformanceCounter(catEs, "Bytes de lectura de disco/s", "_Total");
                _diskWriteCounter = new PerformanceCounter(catEs, "Bytes de escritura en disco/s", "_Total");
            }
            catch
            {
                try
                {
                    _diskIdleTimeCounter = new PerformanceCounter(catEn, "% Idle Time", "_Total");
                    _diskReadCounter = new PerformanceCounter(catEn, "Disk Read Bytes/sec", "_Total");
                    _diskWriteCounter = new PerformanceCounter(catEn, "Disk Write Bytes/sec", "_Total");
                }
                catch { }
            }
        }

        private void InitializeNetwork()
        {
            string catEs = "Interfaz de red";
            string catEn = "Network Interface";
            string currentCat = catEs;

            if (!PerformanceCounterCategory.Exists(catEs))
            {
                if (PerformanceCounterCategory.Exists(catEn)) currentCat = catEn;
                else return;
            }

            string sentName = (currentCat == catEs) ? "Bytes enviados/s" : "Bytes Sent/sec";
            string recvName = (currentCat == catEs) ? "Bytes recibidos/s" : "Bytes Received/sec";
            string bandName = (currentCat == catEs) ? "Ancho de banda actual" : "Current Bandwidth";

            try
            {
                var category = new PerformanceCounterCategory(currentCat);
                var instances = category.GetInstanceNames();

                foreach (var instance in instances)
                {
                    string lowerName = instance.ToLower();
                    if (lowerName.Contains("loopback") || lowerName.Contains("isatap") || lowerName.Contains("teredo"))
                        continue;

                    try
                    {
                        _networkSentCounters.Add(new PerformanceCounter(currentCat, sentName, instance));
                        _networkReceivedCounters.Add(new PerformanceCounter(currentCat, recvName, instance));
                        _networkBandwidthCounters.Add(new PerformanceCounter(currentCat, bandName, instance));
                    }
                    catch { }
                }
            }
            catch { }
        }

        // *** Inicialización de GPU ***
        private void InitializeGpu()
        {
            try
            {
                try { _gpuCategoryName = PerfCounterHelper.ResolveCategory("GPU Engine", "Motor de GPU"); } catch { return; }
                try { _gpuMemCategoryName = PerfCounterHelper.ResolveCategory("GPU Adapter Memory", "Memoria de adaptador de GPU"); } catch { }

                RefreshGpuCounters();

                if (!string.IsNullOrEmpty(_gpuMemCategoryName))
                {
                    try
                    {
                        string counterName = PerfCounterHelper.ResolveCounter(_gpuMemCategoryName, "Dedicated Usage", "Bytes dedicados");
                        _gpuVramCounter = new PerformanceCounter(_gpuMemCategoryName, counterName, "_Total");
                    }
                    catch
                    {
                        try
                        {
                            var cat = new PerformanceCounterCategory(_gpuMemCategoryName);
                            var instances = cat.GetInstanceNames();
                            if (instances.Length > 0)
                            {
                                string counterName = PerfCounterHelper.ResolveCounter(_gpuMemCategoryName, "Dedicated Usage", "Bytes dedicados");
                                _gpuVramCounter = new PerformanceCounter(_gpuMemCategoryName, counterName, instances[0]);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void RefreshGpuCounters()
        {
            if (string.IsNullOrEmpty(_gpuCategoryName)) return;

            foreach (var c in _gpu3dCounters) c.Dispose();
            _gpu3dCounters.Clear();

            try
            {
                var category = new PerformanceCounterCategory(_gpuCategoryName);
                var instances = category.GetInstanceNames();
                string counterName = PerfCounterHelper.ResolveCounter(_gpuCategoryName, "Utilization Percentage", "Porcentaje de utilización");

                foreach (var instance in instances)
                {
                    if (instance.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase) ||
                        instance.Contains("3D", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var pc = new PerformanceCounter(_gpuCategoryName, counterName, instance);
                            pc.NextValue();
                            _gpu3dCounters.Add(pc);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void WarmUpCounters()
        {
            try
            {
                _cpuCounter?.NextValue();
                _memCounter?.NextValue();
                _diskIdleTimeCounter?.NextValue();
                _diskReadCounter?.NextValue();
                _diskWriteCounter?.NextValue();
                _gpuVramCounter?.NextValue();

                foreach (var c in _networkSentCounters) c.NextValue();
                foreach (var c in _networkReceivedCounters) c.NextValue();
                foreach (var c in _networkBandwidthCounters) c.NextValue();
                foreach (var c in _gpu3dCounters) c.NextValue();
            }
            catch { }
        }

        // --- MÉTODOS DE LECTURA ---

        public double GetGlobalCpuUsage()
        {
            if (_isDisposed || _cpuCounter == null) return 0.0;
            try { return _cpuCounter.NextValue(); } catch { return 0.0; }
        }

        public MemoryUsageInfo GetMemoryUsage()
        {
            if (_isDisposed) return new MemoryUsageInfo();

            try
            {
                // Usamos GlobalMemoryStatusEx para datos precisos
                var memStatus = new Kernel32.MEMORYSTATUSEX();
                if (Kernel32.GlobalMemoryStatusEx(memStatus))
                {
                    return new MemoryUsageInfo
                    {
                        TotalPhysicalBytes = memStatus.ullTotalPhys,
                        AvailableBytes = memStatus.ullAvailPhys,
                        UsedPhysicalBytes = memStatus.ullTotalPhys - memStatus.ullAvailPhys,
                        CommittedBytes = memStatus.ullTotalPageFile - memStatus.ullAvailPageFile,
                        CommitLimit = memStatus.ullTotalPageFile
                    };
                }
            }
            catch { }

            // Fallback
            try
            {
                if (_memCounter != null)
                {
                    ulong availableBytes = (ulong)_memCounter.NextValue();
                    return new MemoryUsageInfo
                    {
                        TotalPhysicalBytes = _totalMemory,
                        AvailableBytes = availableBytes,
                        UsedPhysicalBytes = (_totalMemory > availableBytes) ? _totalMemory - availableBytes : 0
                    };
                }
            }
            catch { }

            return new MemoryUsageInfo { TotalPhysicalBytes = _totalMemory };
        }

        public DiskUsageInfo GetDiskUsage()
        {
            if (_isDisposed) return new DiskUsageInfo();
            try
            {
                float idlePercent = _diskIdleTimeCounter?.NextValue() ?? 100;
                double activePercent = Math.Max(0, Math.Min(100, 100.0 - idlePercent));
                return new DiskUsageInfo
                {
                    ActiveTimePercent = activePercent,
                    ReadBytesPerSec = _diskReadCounter?.NextValue() ?? 0,
                    WriteBytesPerSec = _diskWriteCounter?.NextValue() ?? 0
                };
            }
            catch { return new DiskUsageInfo(); }
        }

        public NetworkUsageInfo GetNetworkUsage()
        {
            if (_isDisposed) return new NetworkUsageInfo();
            try
            {
                double sent = 0;
                double recv = 0;
                double bandwidthBits = 0;
                for (int i = 0; i < _networkSentCounters.Count; i++) sent += _networkSentCounters[i].NextValue();
                for (int i = 0; i < _networkReceivedCounters.Count; i++) recv += _networkReceivedCounters[i].NextValue();
                for (int i = 0; i < _networkBandwidthCounters.Count; i++) bandwidthBits += _networkBandwidthCounters[i].NextValue();
                return new NetworkUsageInfo { BytesSentPerSec = sent, BytesReceivedPerSec = recv, BandwidthBytesPerSec = bandwidthBits / 8.0 };
            }
            catch { return new NetworkUsageInfo(); }
        }

        // *** LECTURA DE GPU ***
        public GpuUsageInfo GetGpuUsage()
        {
            if (_isDisposed) return new GpuUsageInfo();

            if (++_gpuCounterRefreshTick > 20)
            {
                RefreshGpuCounters();
                _gpuCounterRefreshTick = 0;
            }

            double totalUsage = 0;
            ulong vramUsed = 0;

            try
            {
                foreach (var c in _gpu3dCounters)
                {
                    try { totalUsage += c.NextValue(); } catch { }
                }

                if (_gpuVramCounter != null)
                {
                    try { vramUsed = (ulong)_gpuVramCounter.NextValue(); } catch { }
                }
            }
            catch { }

            return new GpuUsageInfo
            {
                GpuUsagePercent = Math.Min(100.0, totalUsage),
                DedicatedMemoryUsed = vramUsed,

                // Usamos D3DKMT (Kernel Graphics) para obtener el total real de VRAM
                DedicatedMemoryTotal = SystemInfoHelper.GetGpuTotalMemory()
            };
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _cpuCounter?.Dispose();
            _memCounter?.Dispose();
            _diskIdleTimeCounter?.Dispose();
            _diskReadCounter?.Dispose();
            _diskWriteCounter?.Dispose();
            _gpuVramCounter?.Dispose();

            foreach (var c in _networkSentCounters) c.Dispose();
            foreach (var c in _networkReceivedCounters) c.Dispose();
            foreach (var c in _networkBandwidthCounters) c.Dispose();
            foreach (var c in _gpu3dCounters) c.Dispose();

            _isDisposed = true;
        }
    }
}