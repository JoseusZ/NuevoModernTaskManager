// ModernTaskManager.Core/Services/PerformanceService.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using ModernTaskManager.Core.Helpers;
using ModernTaskManager.Core.Native;

namespace ModernTaskManager.Core.Services
{
    // Estructuras (MemoryUsageInfo, DiskUsageInfo, NetworkUsageInfo, GpuUsageInfo)
    public struct MemoryUsageInfo
    {
        public ulong TotalPhysicalBytes { get; set; }
        public ulong UsedPhysicalBytes { get; set; }
        public ulong AvailableBytes { get; set; }
        public ulong CommittedBytes { get; set; }
        public ulong CommitLimit { get; set; }
        public double UsedPercentage => (TotalPhysicalBytes == 0) ? 0 : (UsedPhysicalBytes / (double)TotalPhysicalBytes) * 100.0;
    }

    public struct DiskUsageInfo
    {
        public double ActiveTimePercent { get; set; }
        public double ReadBytesPerSec { get; set; }
        public double WriteBytesPerSec { get; set; }
    }

    public struct NetworkUsageInfo
    {
        public double BytesSentPerSec { get; set; }
        public double BytesReceivedPerSec { get; set; }
        public double BandwidthBytesPerSec { get; set; }
        public double TotalBytesPerSec => BytesSentPerSec + BytesReceivedPerSec;
    }

    public struct GpuUsageInfo
    {
        public double GpuUsagePercent { get; set; }
        public ulong DedicatedMemoryUsed { get; set; }
        public ulong DedicatedMemoryTotal { get; set; }
        public ulong SharedMemory { get; set; }
        public ulong Budget { get; set; }
        public bool VramSupported { get; set; }
    }

    [SupportedOSPlatform("windows")]
    public class PerformanceService : IDisposable
    {
        private PerformanceCounter? _cpuCounter;
        private PerformanceCounter? _memCounter;
        private PerformanceCounter? _diskIdleTimeCounter;
        private PerformanceCounter? _diskReadCounter;
        private PerformanceCounter? _diskWriteCounter;

        private readonly List<PerformanceCounter> _networkSentCounters = new();
        private readonly List<PerformanceCounter> _networkReceivedCounters = new();
        private readonly List<PerformanceCounter> _networkBandwidthCounters = new();

        // GPU - Arquitectura DXGI-first
        private bool _gpuInitialized = false;
        private bool _gpuPerfCounterAvailable = false;
        private readonly List<PerformanceCounter> _gpuInstanceCounters = new();
        private ulong _gpuVramTotal = 0;
        private ulong _gpuVramUsed = 0;
        private ulong _gpuVramShared = 0;
        private ulong _gpuVramBudget = 0;
        private double _gpuUsagePercent = 0.0;
        private int _gpuRefreshTick = 0;
        private bool _dxgiAvailable = false;
        private bool _dxgiAttempted = false;

        private readonly Dictionary<string, PerformanceCounter> _gpuAllEngineCounters = new();
        private readonly Dictionary<string, double> _lastEngineValues = new();
        private int _gpuEngineWarmSamples = 0;
        private double _lastNonZeroGpuPercent = 0.0;
        private int _consecutiveZeroGpuSamples = 0;

        private bool _isDisposed;
        private ulong _totalMemory;

        private static readonly Regex AdapterKeyRegex = new Regex(@"luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_(\d+)", RegexOptions.Compiled);

        public PerformanceService()
        {
            try
            {
                _totalMemory = SystemInfoHelper.GetTotalPhysicalMemory();
                InitializeCpu();
                InitializeMemory();
                InitializeDisk();
                InitializeNetwork();
                InitializeGpu();
                WarmUpCounters();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fatal en PerformanceService: {ex.Message}");
            }
        }

        private void InitializeCpu()
        {
            try { _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
            catch
            {
                try { _cpuCounter = new PerformanceCounter("Procesador", "% de tiempo de procesador", "_Total"); }
                catch { _cpuCounter = null; }
            }
        }

        private void InitializeMemory()
        {
            try { _memCounter = new PerformanceCounter("Memory", "Available Bytes"); }
            catch
            {
                try { _memCounter = new PerformanceCounter("Memoria", "Bytes disponibles"); }
                catch { _memCounter = null; }
            }
        }

        private void InitializeDisk()
        {
            string catEn = "PhysicalDisk";
            string catEs = "Disco físico";
            try
            {
                if (PerformanceCounterCategory.Exists(catEn))
                {
                    _diskIdleTimeCounter = new PerformanceCounter(catEn, "% Idle Time", "_Total");
                    _diskReadCounter = new PerformanceCounter(catEn, "Disk Read Bytes/sec", "_Total");
                    _diskWriteCounter = new PerformanceCounter(catEn, "Disk Write Bytes/sec", "_Total");
                    return;
                }
            }
            catch { }
            try
            {
                if (PerformanceCounterCategory.Exists(catEs))
                {
                    _diskIdleTimeCounter = new PerformanceCounter(catEs, "% de tiempo inactivo", "_Total");
                    _diskReadCounter = new PerformanceCounter(catEs, "Bytes de lectura de disco/s", "_Total");
                    _diskWriteCounter = new PerformanceCounter(catEs, "Bytes de escritura en disco/s", "_Total");
                    return;
                }
            }
            catch { }
        }

        private void InitializeNetwork()
        {
            string catEn = "Network Interface";
            string catEs = "Interfaz de red";
            string? currentCat = null;

            if (PerformanceCounterCategory.Exists(catEn)) currentCat = catEn;
            else if (PerformanceCounterCategory.Exists(catEs)) currentCat = catEs;
            else return;

            string sentName = currentCat == catEn ? "Bytes Sent/sec" : "Bytes enviados/s";
            string recvName = currentCat == catEn ? "Bytes Received/sec" : "Bytes recibidos/s";
            string bandName = currentCat == catEn ? "Current Bandwidth" : "Ancho de banda actual";

            try
            {
                var category = new PerformanceCounterCategory(currentCat);
                var instances = category.GetInstanceNames();

                foreach (var instance in instances)
                {
                    string lowerName = instance.ToLowerInvariant();
                    if (lowerName.Contains("loopback") || lowerName.Contains("isatap") || lowerName.Contains("teredo"))
                        continue;

                    try
                    {
                        _networkSentCounters.Add(new PerformanceCounter(currentCat, sentName, instance, true));
                        _networkReceivedCounters.Add(new PerformanceCounter(currentCat, recvName, instance, true));
                        _networkBandwidthCounters.Add(new PerformanceCounter(currentCat, bandName, instance, true));
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void InitializeGpu()
        {
            if (_gpuInitialized) return;
            _gpuInitialized = true;

            try
            {
                if (WindowsVersion.IsWindows7)
                {
                    _gpuVramTotal = SystemInfoHelper.GetGpuTotalMemory();
                    return;
                }

                if (!_dxgiAttempted)
                {
                    _dxgiAttempted = true;
                    var dxgiResult = DXGIWrapper.QueryVideoMemoryInfo();
                    _dxgiAvailable = dxgiResult.Success;

                    if (_dxgiAvailable)
                    {
                        _gpuVramTotal = dxgiResult.DedicatedVideoMemory;
                        _gpuVramShared = dxgiResult.SharedSystemMemory;
                        _gpuVramUsed = dxgiResult.CurrentUsage;
                        _gpuVramBudget = dxgiResult.Budget;
                        if (_gpuVramTotal > 0) return;
                    }
                }

                InitializeGpuPerformanceCounters();

                if (_gpuVramTotal == 0)
                {
                    _gpuVramTotal = SystemInfoHelper.GetGpuTotalMemory();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GPU initialization failed: {ex.Message}");
                try { _gpuVramTotal = SystemInfoHelper.GetGpuTotalMemory(); } catch { }
            }
        }

        private void InitializeGpuPerformanceCounters()
        {
            if (_gpuPerfCounterAvailable) return;

            try
            {
                if (PerformanceCounterCategory.Exists("GPU Engine"))
                {
                    _gpuPerfCounterAvailable = true;
                    var cat = new PerformanceCounterCategory("GPU Engine");
                    var instances = cat.GetInstanceNames();

                    string[] candidateCounters = { "Utilization Percentage", "% GPU Usage", "GPU Usage", "Utilization" };
                    Debug.WriteLine($"GPU Engine category found. Instances={instances.Length}");

                    foreach (var inst in instances)
                    {
                        foreach (var candidate in candidateCounters)
                        {
                            try
                            {
                                var pc = new PerformanceCounter("GPU Engine", candidate, inst, true);
                                try { _ = pc.NextValue(); } catch { pc.Dispose(); continue; }

                                if (!_gpuAllEngineCounters.ContainsKey(inst))
                                {
                                    _gpuAllEngineCounters[inst] = pc;
                                    if (inst.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        inst.IndexOf("engtype_Compute", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        _gpuInstanceCounters.Add(pc);
                                    }
                                }
                                break;
                            }
                            catch { }
                        }
                    }

                    Debug.WriteLine($"Total GPU counters (all engines): {_gpuAllEngineCounters.Count}. 3D/Compute tracked: {_gpuInstanceCounters.Count}");
                }
                else
                {
                    Debug.WriteLine("GPU Engine performance counter category not found");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InitializeGpuPerformanceCounters error: {ex.Message}");
                _gpuPerfCounterAvailable = false;
            }
        }

        private string FindGpuUsageCounterName(PerformanceCounterCategory cat, string[] instances)
        {
            try
            {
                var exampleInstance = instances.FirstOrDefault();
                if (!string.IsNullOrEmpty(exampleInstance))
                {
                    var counters = cat.GetCounters(exampleInstance);
                    bool hasUtilization = counters.Any(c =>
                        string.Equals(c.CounterName, "Utilization Percentage", StringComparison.OrdinalIgnoreCase));

                    return hasUtilization ? "Utilization Percentage" : "% GPU Usage";
                }
            }
            catch { }

            return "% GPU Usage";
        }

        private void RefreshGpuMetrics()
        {
            try
            {
                _gpuRefreshTick++;
                if (!_gpuInitialized)
                    InitializeGpu();

                if (_dxgiAvailable && WindowsVersion.IsWindows8OrGreater)
                    RefreshGpuViaDXGI();

                RefreshGpuEngineUsage();

                if (!WindowsVersion.IsWindows7)
                    RefreshGpuVramUsagePerfCounter();
                else
                {
                    _gpuVramUsed = 0;
                    _gpuVramBudget = 0;
                }

                if (_gpuVramTotal == 0)
                    _gpuVramTotal = SystemInfoHelper.GetGpuTotalMemory();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RefreshGpuMetrics error: {ex.Message}");
                _gpuUsagePercent = 0.0;
            }
        }

        private void RefreshGpuViaDXGI()
        {
            try
            {
                var dxgiResult = DXGIWrapper.QueryVideoMemoryInfo();
                if (dxgiResult.Success)
                {
                    if (dxgiResult.DedicatedVideoMemory > 0)
                        _gpuVramTotal = dxgiResult.DedicatedVideoMemory;
                    if (dxgiResult.SharedSystemMemory > 0)
                        _gpuVramShared = dxgiResult.SharedSystemMemory;
                    if (dxgiResult.CurrentUsage > 0)
                        _gpuVramUsed = dxgiResult.CurrentUsage;
                    if (dxgiResult.Budget > 0)
                        _gpuVramBudget = dxgiResult.Budget;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DXGI refresh failed: {ex.Message}");
                _dxgiAvailable = false;
            }
        }

        private void RefreshGpuEngineUsage()
        {
            try
            {
                if (!_gpuPerfCounterAvailable || _gpuAllEngineCounters.Count == 0)
                {
                    _gpuUsagePercent = TryAlternativeGpuUsageMethods();
                    if (_gpuVramTotal == 0)
                        _gpuVramTotal = SystemInfoHelper.GetGpuTotalMemory();
                    return;
                }

                // Primera fase: warm-up inicial (primera llamada) para estabilizar counters.
                if (_gpuEngineWarmSamples == 0)
                {
                    foreach (var pc in _gpuAllEngineCounters.Values)
                    {
                        try { _ = pc.NextValue(); } catch { }
                    }
                    _gpuEngineWarmSamples++;
                    _gpuUsagePercent = 0.0;
                    return;
                }

                // Lectura única por refresco (evitar sesgar con múltiples lecturas seguidas)
                var perAdapterSums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                double maxEngine = 0.0;

                foreach (var kv in _gpuAllEngineCounters.ToList())
                {
                    var instanceName = kv.Key;
                    var pc = kv.Value;
                    double val;
                    try
                    {
                        val = pc.NextValue();
                    }
                    catch
                    {
                        try { pc.Dispose(); } catch { }
                        _gpuAllEngineCounters.Remove(instanceName);
                        continue;
                    }

                    if (double.IsNaN(val) || double.IsInfinity(val)) continue;

                    // Normalizar
                    double norm = Math.Min(Math.Max(val, 0.0), 100.0);
                    if (norm > maxEngine) maxEngine = norm;

                    // Agrupar por adaptador: extraer "luid_*_0x..._phys_N"
                    string adapterKey = ExtractAdapterKey(instanceName);

                    if (!perAdapterSums.TryGetValue(adapterKey, out var current))
                        current = 0.0;
                    current += norm;
                    perAdapterSums[adapterKey] = current;
                }

                // Tomar el máximo por adaptador y clampeado a 100 (Task Manager approach)
                double aggregate = perAdapterSums.Values.Select(v => Math.Min(v, 100.0)).DefaultIfEmpty(0.0).Max();

                // Heurística: si la suma es muy baja pero hay motores >0, usar el máximo motor
                if (aggregate < 0.5 && maxEngine > aggregate)
                    aggregate = maxEngine;

                // Fallback basado en VRAM si sigue siendo cero repetidamente y hay memoria usada
                if (aggregate < 0.1)
                {
                    _consecutiveZeroGpuSamples++;
                    if (_consecutiveZeroGpuSamples >= 10 && _gpuVramUsed > 0 && _gpuVramTotal > 0)
                    {
                        double vramRatio = (_gpuVramUsed / (double)_gpuVramTotal) * 100.0;
                        aggregate = Math.Min(100.0, vramRatio * 0.4);
                    }
                }
                else
                {
                    _consecutiveZeroGpuSamples = 0;
                    _lastNonZeroGpuPercent = aggregate;
                }

                _gpuUsagePercent = Math.Min(100.0, Math.Max(0.0, aggregate));

                if (_gpuVramTotal == 0)
                    _gpuVramTotal = SystemInfoHelper.GetGpuTotalMemory();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in RefreshGpuEngineUsage: {ex.Message}");
                _gpuUsagePercent = 0.0;
            }
        }

        private string ExtractAdapterKey(string instanceName)
        {
            // Ejemplo de instancia: pid_XXXX_luid_0xAAA_0xBBB_phys_0_eng_XX_engtype_3D
            var m = AdapterKeyRegex.Match(instanceName);
            if (m.Success)
            {
                return $"{m.Groups[1].Value}_phys_{m.Groups[2].Value}";
            }

            // Fallback rudimentario: buscar 'phys_'
            int iPhys = instanceName.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
            if (iPhys >= 0)
            {
                int end = instanceName.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
                if (end > iPhys)
                    return instanceName.Substring(iPhys + 1, end - (iPhys + 1)); // quitar '_' inicial
            }
            return "adapter_default";
        }

        private double TryAlternativeGpuUsageMethods()
        {
            try
            {
                double wmiUsage = GetGpuUsageViaWMI();
                if (wmiUsage > 0) return wmiUsage;

                if (WindowsVersion.IsWindows10OrGreater)
                {
                    double vendorUsage = TryGetVendorSpecificGpuUsage();
                    if (vendorUsage > 0) return vendorUsage;
                }

                if (_gpuRefreshTick % 30 == 0)
                {
                    Debug.WriteLine("Reintentando inicialización de GPU Performance Counters...");
                    _gpuPerfCounterAvailable = false;
                    _gpuInstanceCounters.Clear();
                    _gpuAllEngineCounters.Clear();
                    InitializeGpuPerformanceCounters();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Alternative GPU methods failed: {ex.Message}");
            }

            return 0.0;
        }

        private double GetGpuUsageViaWMI()
        {
            try
            {
                return 0.0;
            }
            catch
            {
                return 0.0;
            }
        }

        private double TryGetVendorSpecificGpuUsage()
        {
            return 0.0;
        }

        private void RefreshGpuVramUsagePerfCounter()
        {
            try
            {
                if (!PerformanceCounterCategory.Exists("GPU Adapter Memory"))
                {
                    _gpuVramTotal = SystemInfoHelper.GetGpuTotalMemory();
                    _gpuVramUsed = 0;
                    return;
                }

                var cat = new PerformanceCounterCategory("GPU Adapter Memory");
                var instances = cat.GetInstanceNames();
                if (instances == null || instances.Length == 0)
                {
                    _gpuVramTotal = SystemInfoHelper.GetGpuTotalMemory();
                    _gpuVramUsed = 0;
                    return;
                }

                string? target = instances.FirstOrDefault(i => i.IndexOf("_phys_0", StringComparison.OrdinalIgnoreCase) >= 0)
                                ?? instances.FirstOrDefault();

                if (string.IsNullOrEmpty(target))
                {
                    _gpuVramTotal = SystemInfoHelper.GetGpuTotalMemory();
                    _gpuVramUsed = 0;
                    return;
                }

                string? counterName = null;
                try
                {
                    var counters = cat.GetCounters(target).Select(c => c.CounterName).ToArray();
                    counterName = counters.FirstOrDefault(cn => cn.IndexOf("Dedicated", StringComparison.OrdinalIgnoreCase) >= 0)
                                  ?? counters.FirstOrDefault(cn => cn.IndexOf("Usage", StringComparison.OrdinalIgnoreCase) >= 0);
                }
                catch { }

                if (string.IsNullOrEmpty(counterName))
                {
                    _gpuVramTotal = SystemInfoHelper.GetGpuTotalMemory();
                    _gpuVramUsed = 0;
                    return;
                }

                using (var pc = new PerformanceCounter("GPU Adapter Memory", counterName, target, true))
                {
                    try { pc.NextValue(); } catch { }
                    double val = 0;
                    try { val = pc.NextValue(); } catch { val = 0; }

                    if (!double.IsNaN(val) && val > 0 && val < (double)ulong.MaxValue)
                    {
                        _gpuVramUsed = Convert.ToUInt64(Math.Max(0, val));
                        Debug.WriteLine($"GPU VRAM Usage: {FormatBytes(_gpuVramUsed)}");
                    }
                    else
                    {
                        _gpuVramUsed = 0;
                    }
                }

                if (_gpuVramTotal == 0)
                    _gpuVramTotal = SystemInfoHelper.GetGpuTotalMemory();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GPU VRAM counter error: {ex.Message}");
                _gpuVramTotal = SystemInfoHelper.GetGpuTotalMemory();
                _gpuVramUsed = 0;
            }
        }

        private string FormatBytes(ulong bytes)
        {
            if (bytes == 0) return "0 B";
            string[] suffixes = { "B", "KB", "MB", "GB" };
            int counter = 0;
            decimal number = (decimal)bytes;

            while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
            {
                number /= 1024;
                counter++;
            }

            return $"{number:n1} {suffixes[counter]}";
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

                foreach (var c in _networkSentCounters) try { c.NextValue(); } catch { }
                foreach (var c in _networkReceivedCounters) try { c.NextValue(); } catch { }
                foreach (var c in _networkBandwidthCounters) try { c.NextValue(); } catch { }

                foreach (var c in _gpuInstanceCounters) try { c.NextValue(); } catch { }
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

            try
            {
                if (_memCounter != null)
                {
                    ulong availableBytes = Convert.ToUInt64(_memCounter.NextValue());
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

        public GpuUsageInfo GetGpuUsage()
        {
            if (_isDisposed) return new GpuUsageInfo();

            try
            {
                RefreshGpuMetrics();

                ulong vramUsed = 0;
                try { vramUsed = _gpuVramUsed; } catch { vramUsed = 0; }

                bool vramSupported = !WindowsVersion.IsWindows7 &&
                                   (_gpuVramTotal > 0 || _gpuVramUsed > 0 || _dxgiAvailable);

                if (WindowsVersion.IsWindows7)
                {
                    vramSupported = false;
                    _gpuVramUsed = 0;
                    _gpuVramBudget = 0;
                }

                return new GpuUsageInfo
                {
                    GpuUsagePercent = _gpuUsagePercent,
                    DedicatedMemoryUsed = vramUsed,
                    DedicatedMemoryTotal = _gpuVramTotal,
                    SharedMemory = _gpuVramShared,
                    Budget = _gpuVramBudget,
                    VramSupported = vramSupported
                };
            }
            catch { return new GpuUsageInfo(); }
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _cpuCounter?.Dispose();
            _memCounter?.Dispose();
            _diskIdleTimeCounter?.Dispose();
            _diskReadCounter?.Dispose();
            _diskWriteCounter?.Dispose();

            foreach (var c in _networkSentCounters) c.Dispose();
            foreach (var c in _networkReceivedCounters) c.Dispose();
            foreach (var c in _networkBandwidthCounters) c.Dispose();

            foreach (var c in _gpuInstanceCounters) c.Dispose();
            foreach (var c in _gpuAllEngineCounters.Values) c.Dispose();

            _isDisposed = true;
        }
    }
}