using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using ModernTaskManager.Core.Helpers;
using ModernTaskManager.Core.Models; // ¡IMPORTANTE! Usamos los modelos compartidos

namespace ModernTaskManager.Core.Services
{
    [SupportedOSPlatform("windows")]
    public sealed class HardwareInfoService : IDisposable
    {
        // Datos estáticos agregados
        public CpuDetailInfo Cpu { get; private set; } = new();
        public GpuDetailInfo Gpu { get; private set; } = new();
        public MemoryDetailInfo Memory { get; private set; } = new();

        // Instancias
        public List<CpuDetailInfo> CpuList { get; } = new();
        public List<GpuDetailInfo> GpuList { get; } = new();
        public List<DiskStaticInfo> Disks { get; } = new();
        public List<NetworkAdapterStaticInfo> NetworkAdapters { get; } = new();

        // Métricas dinámicas
        public List<CpuCoreUsageInfo> CpuCoreUsage { get; } = new();
        public List<DiskDynamicInfo> DiskUsage { get; } = new();
        public List<NetworkAdapterDynamicInfo> NetworkUsage { get; } = new();
        public List<GpuAdapterDynamicInfo> GpuAdapterUsage { get; } = new();

        private bool _disposed;
        private readonly object _lock = new();
        private bool _staticLoaded;

        // PerformanceCounters dinámicos
        private readonly List<PerformanceCounter> _cpuCoreCounters = new();
        private readonly Dictionary<string, (PerformanceCounter active, PerformanceCounter read, PerformanceCounter write)> _diskCounters = new();
        private readonly Dictionary<string, (PerformanceCounter sent, PerformanceCounter recv, PerformanceCounter bw)> _netCounters = new();

        // Reusar scope WMI para evitar overhead
        private readonly ManagementScope _scope = new ManagementScope(@"\\.\root\cimv2");

        public HardwareInfoService()
        {
            try { _scope.Connect(); } catch { }
            LoadStaticOnce();
            InitializeDynamicCounters();
            RefreshDynamic();
        }

        public void RefreshDynamic()
        {
            if (_disposed) return;
            lock (_lock)
            {
                LoadMemoryDynamic();
                RefreshCpuCoreUsage();
                RefreshDiskUsage();
                RefreshNetworkUsage();
                // Evitar cargar GPU dinámica aquí para ahorrar memoria
            }
        }

        private void LoadStaticOnce()
        {
            if (_staticLoaded) return;
            lock (_lock)
            {
                if (_staticLoaded) return;
                LoadCpu();
                LoadGpu();
                LoadDisks();
                LoadNetwork();
                _staticLoaded = true;
            }
        }

        // ----------- CPU ESTÁTICO -----------
        private void LoadCpu()
        {
            try
            {
                Cpu = new CpuDetailInfo();
                CpuList.Clear();
                using var search = CreateSearcher(_scope, "SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,L2CacheSize,L3CacheSize FROM Win32_Processor");
                int sockets = 0;
                foreach (var mo in SafeEnumerate(search))
                {
                    var info = new CpuDetailInfo
                    {
                        Name = SafeString(mo, "Name"),
                        PhysicalCores = SafeInt(mo, "NumberOfCores"),
                        LogicalProcessors = SafeInt(mo, "NumberOfLogicalProcessors"),
                        BaseClockMHz = SafeInt(mo, "MaxClockSpeed"),
                        L2CacheKB = SafeInt(mo, "L2CacheSize"),
                        L3CacheKB = SafeInt(mo, "L3CacheSize"),
                        VirtualizationEnabled = false
                    };
                    CpuList.Add(info);

                    Cpu.Name = info.Name;
                    Cpu.PhysicalCores += info.PhysicalCores;
                    Cpu.LogicalProcessors += info.LogicalProcessors;
                    Cpu.BaseClockMHz = MaxPositive(Cpu.BaseClockMHz, info.BaseClockMHz);
                    Cpu.L2CacheKB += info.L2CacheKB;
                    Cpu.L3CacheKB += info.L3CacheKB;
                    sockets++;
                }
                Cpu.Sockets = sockets > 0 ? sockets : 1;
                bool virt = DetectVirtualization();
                Cpu.VirtualizationEnabled = virt;
                foreach (var c in CpuList) c.VirtualizationEnabled = virt;
            }
            catch { }
        }

        private bool DetectVirtualization()
        {
            try
            {
                using var search = CreateSearcher(_scope, "SELECT VirtualizationFirmwareEnabled FROM Win32_ComputerSystem");
                foreach (var mo in SafeEnumerate(search))
                {
                    var raw = mo["VirtualizationFirmwareEnabled"];
                    if (raw is bool b) return b;
                }
            }
            catch { }
            return false;
        }

        // ----------- MEMORIA DINÁMICO -----------
        private void LoadMemoryDynamic()
        {
            Memory.TotalBytes = SystemInfoHelper.GetTotalPhysicalMemory();
            try
            {
                using var osSearch = CreateSearcher(_scope, "SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem");
                foreach (var mo in SafeEnumerate(osSearch))
                {
                    ulong totalKb = SafeULong(mo, "TotalVisibleMemorySize");
                    ulong freeKb = SafeULong(mo, "FreePhysicalMemory");
                    if (totalKb > 0) Memory.TotalBytes = totalKb * 1024;
                    Memory.FreeBytes = freeKb * 1024;
                    Memory.InUseBytes = (Memory.TotalBytes > Memory.FreeBytes) ? Memory.TotalBytes - Memory.FreeBytes : 0;
                    break;
                }
                using var perfMem = CreateSearcher(_scope, "SELECT PoolPagedBytes,PoolNonpagedBytes,CacheBytes FROM Win32_PerfFormattedData_PerfOS_Memory");
                foreach (var mo in SafeEnumerate(perfMem))
                {
                    Memory.PagedPoolBytes = SafeULong(mo, "PoolPagedBytes");
                    Memory.NonPagedPoolBytes = SafeULong(mo, "PoolNonpagedBytes");
                    Memory.CacheBytes = SafeULong(mo, "CacheBytes");
                    break;
                }
                Memory.HardwareReservedBytes = 0;
            }
            catch { }
        }

        // ----------- GPU ESTÁTICO -----------
        private void LoadGpu()
        {
            try
            {
                Gpu = new GpuDetailInfo();
                GpuList.Clear();

                using var search = CreateSearcher(_scope, "SELECT Name, DriverVersion, DriverDate, AdapterRAM, PNPDeviceID FROM Win32_VideoController");

                foreach (var mo in SafeEnumerate(search).Take(2)) // limitar para ahorrar memoria
                {
                    var info = new GpuDetailInfo
                    {
                        Name = SafeString(mo, "Name"),
                        DriverVersion = SafeString(mo, "DriverVersion"),
                        TotalDedicatedBytes = SafeULong(mo, "AdapterRAM"),
                        PnpDeviceId = SafeString(mo, "PNPDeviceID")
                    };

                    string dateStr = SafeString(mo, "DriverDate");
                    if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 8)
                    {
                        if (DateTime.TryParseExact(dateStr.Substring(0, 8), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime dt))
                        {
                            info.DriverDate = dt;
                        }
                    }

                    ulong sysRam = SystemInfoHelper.GetTotalPhysicalMemory();
                    info.TotalSharedBytes = sysRam / 2;
                    info.Location = GetPnpLocation(info.PnpDeviceId);

                    GpuList.Add(info);
                    if (string.IsNullOrEmpty(Gpu.Name)) Gpu = info;
                }

                if (GpuList.Count > 0 && string.IsNullOrEmpty(Gpu.Name)) Gpu = GpuList[0];
            }
            catch { }
        }

        private string GetPnpLocation(string pnpDeviceId)
        {
            try
            {
                string cleanId = pnpDeviceId.Replace("\\", "\\\\");
                using var search = CreateSearcher(_scope, $"SELECT LocationInformation FROM Win32_PnPEntity WHERE DeviceID = '{cleanId}'");
                foreach (var item in SafeEnumerate(search))
                {
                    return SafeString(item, "LocationInformation");
                }
            }
            catch { }
            return "Bus PCI desconocido";
        }

        private void RefreshGpuAdapterUsage()
        {
            GpuAdapterUsage.Clear();
            // Aquí iría la lógica dinámica de GPU si se usa GpuEngineAggregator.
            // Por ahora lo dejamos limpio para evitar errores de dependencias faltantes.
        }

        // ----------- DISCO ESTÁTICO -----------
        private void LoadDisks()
        {
            try
            {
                Disks.Clear();
                var physical = new Dictionary<string, (string model, ulong size)>(StringComparer.OrdinalIgnoreCase);
                using (var diskSearch = CreateSearcher(_scope, "SELECT DeviceID,Model,Size FROM Win32_DiskDrive"))
                {
                    foreach (var mo in SafeEnumerate(diskSearch))
                    {
                        string id = SafeString(mo, "DeviceID");
                        if (!string.IsNullOrEmpty(id))
                            physical[id] = (SafeString(mo, "Model"), SafeULong(mo, "Size"));
                    }
                }

                using var volSearch = CreateSearcher(_scope, "SELECT DriveLetter,Capacity,FreeSpace FROM Win32_Volume WHERE DriveLetter IS NOT NULL");
                foreach (var mo in SafeEnumerate(volSearch))
                {
                    var disk = new DiskStaticInfo
                    {
                        DriveLetter = SafeString(mo, "DriveLetter"),
                        CapacityBytes = SafeULong(mo, "Capacity"),
                        FreeBytes = SafeULong(mo, "FreeSpace"),
                        Model = physical.Values.FirstOrDefault().model ?? "Disco Genérico"
                    };
                    if (!string.IsNullOrEmpty(disk.DriveLetter))
                        Disks.Add(disk);
                }
            }
            catch { }
        }

        // ----------- RED ESTÁTICO -----------
        private void LoadNetwork()
        {
            try
            {
                NetworkAdapters.Clear();
                using var search = CreateSearcher(_scope, "SELECT Name,Speed,MACAddress FROM Win32_NetworkAdapter WHERE NetEnabled = TRUE");
                foreach (var mo in SafeEnumerate(search).Take(10))
                {
                    var info = new NetworkAdapterStaticInfo
                    {
                        Name = SafeString(mo, "Name"),
                        SpeedBitsPerSec = SafeULong(mo, "Speed"),
                        MacAddress = SafeString(mo, "MACAddress")
                    };
                    if (!string.IsNullOrEmpty(info.Name))
                        NetworkAdapters.Add(info);
                }
            }
            catch { }
        }

        // ----------- CONTADORES DINÁMICOS -----------
        private void InitializeDynamicCounters()
        {
            InitializeCpuCoreCounters();
            InitializeDiskInstanceCounters();
            InitializeNetworkInstanceCounters();
        }

        private void InitializeCpuCoreCounters()
        {
            try
            {
                if (!PerformanceCounterCategory.Exists("Processor")) return;
                var cat = new PerformanceCounterCategory("Processor");
                foreach (var inst in cat.GetInstanceNames())
                {
                    if (inst.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!int.TryParse(inst, out _)) continue;
                    try
                    {
                        var pc = new PerformanceCounter("Processor", "% Processor Time", inst, true);
                        pc.NextValue();
                        _cpuCoreCounters.Add(pc);
                        if (_cpuCoreCounters.Count >= 16) break; // limitar para ahorrar memoria
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void InitializeDiskInstanceCounters()
        {
            try
            {
                if (!PerformanceCounterCategory.Exists("PhysicalDisk")) return;
                var cat = new PerformanceCounterCategory("PhysicalDisk");
                foreach (var inst in cat.GetInstanceNames())
                {
                    if (inst.Equals("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                    if (_diskCounters.ContainsKey(inst)) continue;
                    try
                    {
                        var active = new PerformanceCounter("PhysicalDisk", "% Disk Time", inst, true);
                        var read = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", inst, true);
                        var write = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", inst, true);
                        active.NextValue(); read.NextValue(); write.NextValue();
                        _diskCounters[inst] = (active, read, write);
                        if (_diskCounters.Count >= 8) break; // limitar
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void InitializeNetworkInstanceCounters()
        {
            try
            {
                if (!PerformanceCounterCategory.Exists("Network Interface")) return;
                var cat = new PerformanceCounterCategory("Network Interface");
                foreach (var inst in cat.GetInstanceNames())
                {
                    string lower = inst.ToLowerInvariant();
                    if (lower.Contains("loopback") || lower.Contains("isatap") || lower.Contains("teredo")) continue;
                    if (_netCounters.ContainsKey(inst)) continue;
                    try
                    {
                        var sent = new PerformanceCounter("Network Interface", "Bytes Sent/sec", inst, true);
                        var recv = new PerformanceCounter("Network Interface", "Bytes Received/sec", inst, true);
                        var bw = new PerformanceCounter("Network Interface", "Current Bandwidth", inst, true);
                        sent.NextValue(); recv.NextValue(); bw.NextValue();
                        _netCounters[inst] = (sent, recv, bw);
                        if (_netCounters.Count >= 8) break; // limitar
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ----------- REFRESCOS DINÁMICOS -----------
        private void RefreshCpuCoreUsage()
        {
            CpuCoreUsage.Clear();
            foreach (var pc in _cpuCoreCounters)
            {
                try
                {
                    double val = pc.NextValue();
                    CpuCoreUsage.Add(new CpuCoreUsageInfo
                    {
                        CoreIndex = int.TryParse(pc.InstanceName, out var idx) ? idx : -1,
                        UsagePercent = double.IsNaN(val) ? 0 : val
                    });
                }
                catch
                {
                    CpuCoreUsage.Add(new CpuCoreUsageInfo { CoreIndex = -1, UsagePercent = 0 });
                }
            }
        }

        private void RefreshDiskUsage()
        {
            DiskUsage.Clear();
            foreach (var kv in _diskCounters)
            {
                try
                {
                    var a = kv.Value.active.NextValue();
                    var r = kv.Value.read.NextValue();
                    var w = kv.Value.write.NextValue();
                    DiskUsage.Add(new DiskDynamicInfo
                    {
                        Instance = kv.Key,
                        ActiveTimePercent = Math.Max(0, Math.Min(100, a)),
                        ReadBytesPerSec = r,
                        WriteBytesPerSec = w
                    });
                }
                catch
                {
                    DiskUsage.Add(new DiskDynamicInfo { Instance = kv.Key });
                }
            }
        }

        private void RefreshNetworkUsage()
        {
            NetworkUsage.Clear();
            foreach (var kv in _netCounters)
            {
                try
                {
                    double sent = kv.Value.sent.NextValue();
                    double recv = kv.Value.recv.NextValue();
                    double bwBits = kv.Value.bw.NextValue();
                    NetworkUsage.Add(new NetworkAdapterDynamicInfo
                    {
                        Name = kv.Key,
                        SentBytesPerSec = sent,
                        ReceivedBytesPerSec = recv,
                        BandwidthBytesPerSec = bwBits / 8.0
                    });
                }
                catch
                {
                    NetworkUsage.Add(new NetworkAdapterDynamicInfo { Name = kv.Key });
                }
            }
        }

        // ----------- HELPERS WMI -----------
        private static ManagementObjectSearcher CreateSearcher(ManagementScope scope, string wql)
        {
            var query = new ObjectQuery(wql);
            var options = new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, DirectRead = true };
            return new ManagementObjectSearcher(scope, query, options);
        }

        private static IEnumerable<ManagementObject> SafeEnumerate(ManagementObjectSearcher searcher)
        {
            var list = new List<ManagementObject>();
            ManagementObjectCollection? col = null;
            try
            {
                col = searcher.Get();
                foreach (var obj in col) { if (obj is ManagementObject mo) list.Add(mo); }
            }
            catch { }
            finally { col?.Dispose(); }
            return list;
        }

        private static string SafeString(ManagementBaseObject mo, string prop)
        {
            try { return mo[prop]?.ToString()?.Trim() ?? ""; } catch { return ""; }
        }

        private static int SafeInt(ManagementBaseObject mo, string prop)
        {
            try { return Convert.ToInt32(mo[prop]); } catch { return 0; }
        }

        private static ulong SafeULong(ManagementBaseObject mo, string prop)
        {
            try { return Convert.ToUInt64(mo[prop]); } catch { return 0; }
        }

        private static int MaxPositive(int current, int candidate) => candidate > current && candidate > 0 ? candidate : current;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var c in _cpuCoreCounters) try { c.Dispose(); } catch { }
            foreach (var d in _diskCounters.Values) { try { d.active.Dispose(); } catch { } try { d.read.Dispose(); } catch { } try { d.write.Dispose(); } catch { } }
            foreach (var n in _netCounters.Values) { try { n.sent.Dispose(); } catch { } try { n.recv.Dispose(); } catch { } try { n.bw.Dispose(); } catch { } }
        }
    }
}