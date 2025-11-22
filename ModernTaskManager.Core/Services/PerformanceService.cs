// PerformanceService.cs (versión consolidada - FIX duplicados y referencias GPU)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using ModernTaskManager.Core.Helpers;
using ModernTaskManager.Core.Native;

namespace ModernTaskManager.Core.Services
{
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
        public ulong SharedMemoryUsed { get; set; }
        public ulong SharedMemoryTotal { get; set; }
        public ulong Budget { get; set; }
        public bool VramSupported { get; set; }
        public string DriverVersion { get; set; }
        public DateTime? DriverDate { get; set; }
        public string DirectXVersion { get; set; }
        public string FeatureLevel { get; set; }
        public string PciLocation { get; set; }
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

        private bool _gpuInitialized;
        private bool _dxgiAvailable;
        private bool _dxgiAttempted;
        private ulong _gpuVramTotal;
        private ulong _gpuVramUsed;
        private ulong _gpuVramShared;
        private ulong _gpuVramBudget;
        private double _gpuUsagePercent;
        private int _gpuRefreshTick;

        private GpuEngineAggregator? _gpuAggregator;
        private GpuGlobalSnapshot? _lastGpuSnapshot;

        private ulong _totalMemory;
        private bool _isDisposed;

        private ulong _lastNonZeroVramUsed;
        private int _consecutiveZeroVramSamples;

        private bool _gpuStaticDetailsLoaded;
        private string _gpuDriverVersion = "";
        private DateTime? _gpuDriverDate;
        private string _gpuDirectXVersion = "";
        private string _gpuFeatureLevel = "";
        private string _gpuPciLocation = "";
        private ulong _sharedMemoryTotal;
        private ulong _sharedMemoryUsed;

        private ulong _lastDxgiUsage;
        private ulong _lastPerfUsage;
        private ulong _lastD3dUsage;
        private int _stabilizationTicks;
        private ulong _lastD3dKmtCommittedBytes;

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
                Debug.WriteLine($"Error fatal PerformanceService: {ex.Message}");
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
            try
            {
                if (PerformanceCounterCategory.Exists("PhysicalDisk"))
                {
                    _diskIdleTimeCounter = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total");
                    _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                    _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                    return;
                }
            }
            catch { }
            try
            {
                if (PerformanceCounterCategory.Exists("Disco físico"))
                {
                    _diskIdleTimeCounter = new PerformanceCounter("Disco físico", "% de tiempo inactivo", "_Total");
                    _diskReadCounter = new PerformanceCounter("Disco físico", "Bytes de lectura de disco/s", "_Total");
                    _diskWriteCounter = new PerformanceCounter("Disco físico", "Bytes de escritura en disco/s", "_Total");
                }
            }
            catch { }
        }

        private void InitializeNetwork()
        {
            string? cat = PerformanceCounterCategory.Exists("Network Interface") ? "Network Interface"
                        : PerformanceCounterCategory.Exists("Interfaz de red") ? "Interfaz de red" : null;
            if (cat == null) return;

            string sentName = cat == "Network Interface" ? "Bytes Sent/sec" : "Bytes enviados/s";
            string recvName = cat == "Network Interface" ? "Bytes Received/sec" : "Bytes recibidos/s";
            string bwName = cat == "Network Interface" ? "Current Bandwidth" : "Ancho de banda actual";

            try
            {
                var category = new PerformanceCounterCategory(cat);
                foreach (var inst in category.GetInstanceNames())
                {
                    var l = inst.ToLowerInvariant();
                    if (l.Contains("loopback") || l.Contains("isatap") || l.Contains("teredo"))
                        continue;
                    try
                    {
                        _networkSentCounters.Add(new PerformanceCounter(cat, sentName, inst, true));
                        _networkReceivedCounters.Add(new PerformanceCounter(cat, recvName, inst, true));
                        _networkBandwidthCounters.Add(new PerformanceCounter(cat, bwName, inst, true));
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
                    var dxgi = DXGIWrapper.QueryVideoMemoryInfo();
                    _dxgiAvailable = dxgi.Success;
                    if (_dxgiAvailable)
                    {
                        if (dxgi.DedicatedVideoMemory > 0) _gpuVramTotal = dxgi.DedicatedVideoMemory;
                        if (dxgi.SharedSystemMemory > 0) _gpuVramShared = dxgi.SharedSystemMemory;
                        if (dxgi.CurrentUsage > 0) _gpuVramUsed = dxgi.CurrentUsage;
                        if (dxgi.Budget > 0) _gpuVramBudget = dxgi.Budget;
                    }
                }

                _gpuAggregator = new GpuEngineAggregator();
                _gpuAggregator.Initialize();

                if (_gpuVramTotal == 0)
                    _gpuVramTotal = SystemInfoHelper.GetGpuTotalMemory();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InitializeGpu error: {ex.Message}");
                try { _gpuVramTotal = SystemInfoHelper.GetGpuTotalMemory(); } catch { }
            }
        }

        private void RefreshGpuMetrics()
        {
            try
            {
                _gpuRefreshTick++;
                if (!_gpuInitialized) InitializeGpu();

                if (!WindowsVersion.IsWindows7)
                    UpdateVramUsageRobust();

                if (!WindowsVersion.IsWindows7 && _gpuAggregator?.Available == true)
                {
                    _lastGpuSnapshot = _gpuAggregator.Refresh();
                    _gpuUsagePercent = _lastGpuSnapshot.GlobalHighestAdapterUsage;
                }
                else
                {
                    _gpuUsagePercent = 0.0;
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
            }
            catch { }
        }

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
                var mem = new Kernel32.MEMORYSTATUSEX();
                if (Kernel32.GlobalMemoryStatusEx(mem))
                {
                    return new MemoryUsageInfo
                    {
                        TotalPhysicalBytes = mem.ullTotalPhys,
                        AvailableBytes = mem.ullAvailPhys,
                        UsedPhysicalBytes = mem.ullTotalPhys - mem.ullAvailPhys,
                        CommittedBytes = mem.ullTotalPageFile - mem.ullAvailPageFile,
                        CommitLimit = mem.ullTotalPageFile
                    };
                }
            }
            catch { }

            try
            {
                if (_memCounter != null)
                {
                    ulong available = Convert.ToUInt64(_memCounter.NextValue());
                    return new MemoryUsageInfo
                    {
                        TotalPhysicalBytes = _totalMemory,
                        AvailableBytes = available,
                        UsedPhysicalBytes = _totalMemory > available ? _totalMemory - available : 0
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
                float idle = _diskIdleTimeCounter?.NextValue() ?? 100;
                double active = Math.Max(0, Math.Min(100, 100 - idle));
                return new DiskUsageInfo
                {
                    ActiveTimePercent = active,
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
                double sent = 0, recv = 0, bwBits = 0;
                foreach (var c in _networkSentCounters) sent += SafeCounter(c);
                foreach (var c in _networkReceivedCounters) recv += SafeCounter(c);
                foreach (var c in _networkBandwidthCounters) bwBits += SafeCounter(c);
                return new NetworkUsageInfo
                {
                    BytesSentPerSec = sent,
                    BytesReceivedPerSec = recv,
                    BandwidthBytesPerSec = bwBits / 8.0
                };
            }
            catch { return new NetworkUsageInfo(); }
        }

        private static double SafeCounter(PerformanceCounter c)
        {
            try { return c.NextValue(); } catch { return 0.0; }
        }

        public GpuUsageInfo GetGpuUsage()
        {
            if (_isDisposed) return new GpuUsageInfo();
            try
            {
                RefreshGpuMetrics();
                LoadGpuStaticDetails();

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
                    DedicatedMemoryUsed = _gpuVramUsed,
                    DedicatedMemoryTotal = _gpuVramTotal,
                    SharedMemoryUsed = _sharedMemoryUsed,
                    SharedMemoryTotal = _sharedMemoryTotal,
                    SharedMemory = _sharedMemoryTotal > 0 ? _sharedMemoryTotal : _gpuVramShared,
                    Budget = _gpuVramBudget,
                    VramSupported = vramSupported,
                    DriverVersion = _gpuDriverVersion,
                    DriverDate = _gpuDriverDate,
                    DirectXVersion = _gpuDirectXVersion,
                    FeatureLevel = _gpuFeatureLevel,
                    PciLocation = _gpuPciLocation
                };
            }
            catch
            {
                return new GpuUsageInfo();
            }
        }

        public IReadOnlyList<(string AdapterKey, double Global, double ThreeD, double Compute)> GetGpuAdapterSnapshots()
        {
            if (_isDisposed || WindowsVersion.IsWindows7) return Array.Empty<(string, double, double, double)>();
            if (_lastGpuSnapshot == null) RefreshGpuMetrics();
            if (_lastGpuSnapshot?.Adapters == null || _lastGpuSnapshot.Adapters.Count == 0)
                return Array.Empty<(string, double, double, double)>();

            return _lastGpuSnapshot.Adapters
                .Select(a => (a.AdapterKey, a.GlobalUsagePercent, a.ThreeDPercent, a.ComputePercent))
                .ToArray();
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
            _gpuAggregator?.Dispose();
            _isDisposed = true;
        }

        ~PerformanceService() { }

        private void TryReadD3DKMTVideoMemory()
        {
            try
            {
                if (D3DKMT.TryQueryPrimaryAdapterVideoMemory(out var stats) && stats != null && stats.IsValid)
                {
                    if (_gpuVramTotal == 0 && stats.TotalDedicatedBytes > 0)
                        _gpuVramTotal = stats.TotalDedicatedBytes;
                    if (stats.TotalCurrentUsageBytes > 0)
                        _lastD3dUsage = stats.TotalCurrentUsageBytes;
                    if (stats.TotalCommittedBytes > 0)
                        _lastD3dKmtCommittedBytes = stats.TotalCommittedBytes;
                    if (_sharedMemoryTotal == 0 && stats.TotalSharedBytes > 0)
                        _sharedMemoryTotal = stats.TotalSharedBytes;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryReadD3DKMTVideoMemory error: {ex.Message}");
            }
        }

        private void RefreshGpuViaDXGI()
        {
            try
            {
                var dx = DXGIWrapper.QueryVideoMemoryInfo();
                if (dx.Success)
                {
                    if (dx.DedicatedVideoMemory > 0) _gpuVramTotal = dx.DedicatedVideoMemory;
                    if (dx.SharedSystemMemory > 0) { _gpuVramShared = dx.SharedSystemMemory; _sharedMemoryTotal = dx.SharedSystemMemory; }
                    if (dx.Budget > 0) _gpuVramBudget = dx.Budget;
                    if (dx.CurrentUsage > 0) _lastDxgiUsage = dx.CurrentUsage;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RefreshGpuViaDXGI error: {ex.Message}");
                _dxgiAvailable = false;
            }
        }

        private void TryReadPerfCounterVramAllInstances()
        {
            try
            {
                if (!PerformanceCounterCategory.Exists("GPU Adapter Memory"))
                    return;

                var cat = new PerformanceCounterCategory("GPU Adapter Memory");
                var instances = cat.GetInstanceNames();
                if (instances == null || instances.Length == 0) return;

                ulong bestDedicated = 0;
                ulong bestShared = 0;

                foreach (var inst in instances)
                {
                    PerformanceCounter[]? counters = null;
                    try
                    {
                        counters = cat.GetCounters(inst);
                        foreach (var c in counters)
                        {
                            try { c.NextValue(); } catch { }
                            double v = 0;
                            try { v = c.NextValue(); } catch { }
                            if (double.IsNaN(v) || v <= 0 || v >= ulong.MaxValue) continue;

                            ulong val = (ulong)v;
                            string name = (c.CounterName ?? "").ToLowerInvariant();
                            bool usage = name.Contains("usage");
                            bool dedicated = name.Contains("dedicated") || name.Contains("local");
                            bool shared = name.Contains("shared");
                            if (!usage) continue;

                            if (dedicated) bestDedicated = Math.Max(bestDedicated, val);
                            else if (shared) bestShared = Math.Max(bestShared, val);
                            else if (name.Contains("total") && bestDedicated == 0) bestDedicated = val;
                        }
                    }
                    catch { }
                    finally
                    {
                        if (counters != null)
                        {
                            foreach (var c in counters) try { c.Dispose(); } catch { }
                        }
                    }
                }

                if (bestDedicated > 0) _lastPerfUsage = bestDedicated;
                if (bestShared > 0) _sharedMemoryUsed = bestShared;

                if (_gpuVramTotal == 0 && bestDedicated > 0)
                {
                    var total = SystemInfoHelper.GetGpuTotalMemory();
                    if (total > 0) _gpuVramTotal = total;
                }
                if (_sharedMemoryTotal == 0 && _gpuVramShared > 0)
                    _sharedMemoryTotal = _gpuVramShared;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TryReadPerfCounterVramAllInstances error: {ex.Message}");
            }
        }

        private void UpdateVramUsageRobust()
        {
            if (WindowsVersion.IsWindows7) return;

            TryReadD3DKMTVideoMemory();
            RefreshGpuViaDXGI();
            TryReadPerfCounterVramAllInstances();

            ulong candidate = 0;
            if (_lastPerfUsage > 0) candidate = _lastPerfUsage;
            else if (_lastDxgiUsage > 0) candidate = _lastDxgiUsage;
            else if (_lastD3dUsage > 0) candidate = _lastD3dUsage;

            if (candidate == 0 && _lastD3dKmtCommittedBytes > 0)
                candidate = Math.Min(_lastD3dKmtCommittedBytes, _gpuVramTotal > 0 ? _gpuVramTotal : _lastD3dKmtCommittedBytes);

            if (candidate == 0)
            {
                _stabilizationTicks++;
                if (_stabilizationTicks <= 6 && _gpuVramUsed > 0) return;
            }
            else
            {
                _stabilizationTicks = 0;
            }

            _gpuVramUsed = candidate;

            if (_gpuVramTotal > 0 && _gpuVramUsed > _gpuVramTotal)
                _gpuVramUsed = _gpuVramTotal;

            if (_sharedMemoryTotal == 0 && _gpuVramShared > 0)
                _sharedMemoryTotal = _gpuVramShared;
            if (_sharedMemoryUsed > _sharedMemoryTotal && _sharedMemoryTotal > 0)
                _sharedMemoryUsed = _sharedMemoryTotal;
        }

        private void LoadGpuStaticDetails()
        {
            if (_gpuStaticDetailsLoaded) return;
            try
            {
                using var search = new System.Management.ManagementObjectSearcher(
                    "SELECT Name,DriverVersion,DriverDate,AdapterRAM,PNPDeviceID FROM Win32_VideoController");
                foreach (var mo in search.Get())
                {
                    _gpuDriverVersion = mo["DriverVersion"]?.ToString() ?? "";
                    var rawDate = mo["DriverDate"]?.ToString();
                    if (!string.IsNullOrEmpty(rawDate) && rawDate.Length >= 8 &&
                        DateTime.TryParseExact(rawDate.Substring(0, 8), "yyyyMMdd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                        _gpuDriverDate = dt;

                    _gpuDirectXVersion = "Desconocido";
                    _gpuFeatureLevel = "Desconocido";

                    var pnp = mo["PNPDeviceID"]?.ToString() ?? "";
                    _gpuPciLocation = string.IsNullOrEmpty(pnp) ? "N/A" : pnp;
                    break;
                }
            }
            catch { }
            _gpuStaticDetailsLoaded = true;
        }
    }
}