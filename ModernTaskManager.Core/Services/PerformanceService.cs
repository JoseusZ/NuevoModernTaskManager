// ModernTaskManager.Core/Services/PerformanceService.cs

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

        private ulong _totalMemory;
        private bool _isDisposed;

        public PerformanceService()
        {
            try
            {
                _totalMemory = SystemInfoHelper.GetTotalPhysicalMemory();
                InitializeCpu();
                InitializeMemory();
                InitializeDisk();
                InitializeNetwork();
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

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _cpuCounter?.Dispose();
            _memCounter?.Dispose();
            _diskIdleTimeCounter?.Dispose();
            _diskReadCounter?.Dispose();
            _diskWriteCounter?.Dispose();
            foreach (var c in _networkSentCounters) c.Dispose();
            foreach (var c in _networkReceivedCounters) c.Dispose();
            foreach (var c in _networkBandwidthCounters) c.Dispose();
        }
    }
}