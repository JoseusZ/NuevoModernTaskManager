using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using ModernTaskManager.Core.Helpers;

namespace ModernTaskManager.Core.Services
{
    [SupportedOSPlatform("windows")]
    public sealed class HardwareInfoService : IDisposable
    {
        // Datos estáticos agregados (compatibilidad)
        public CpuDetailInfo Cpu { get; private set; } = new();
        public GpuDetailInfo Gpu { get; private set; } = new();
        public MemoryDetailInfo Memory { get; private set; } = new();

        // Instancias (multi‑dispositivo estático)
        public List<CpuDetailInfo> CpuList { get; } = new();
        public List<GpuDetailInfo> GpuList { get; } = new();
        public List<DiskStaticInfo> Disks { get; } = new();
        public List<NetworkAdapterStaticInfo> NetworkAdapters { get; } = new();

        // Métricas dinámicas (refrescables)
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
        private GpuEngineAggregator? _gpuAggregator; // Reutiliza lógica del servicio existente

        public HardwareInfoService()
        {
            LoadStaticOnce();
            InitializeDynamicCounters();
            RefreshDynamic();
        }

        // PUBLIC REFRESH
        public void RefreshDynamic()
        {
            if (_disposed) return;
            lock (_lock)
            {
                LoadMemoryDynamic();
                RefreshCpuCoreUsage();
                RefreshDiskUsage();
                RefreshNetworkUsage();
                RefreshGpuAdapterUsage();
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
                using var search = CreateSearcher("SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,L2CacheSize,L3CacheSize FROM Win32_Processor");
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
                using var search = CreateSearcher("SELECT VirtualizationFirmwareEnabled FROM Win32_ComputerSystem");
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
                using var osSearch = CreateSearcher("SELECT TotalVisibleMemorySize,FreePhysicalMemory FROM Win32_OperatingSystem");
                foreach (var mo in SafeEnumerate(osSearch))
                {
                    ulong totalKb = SafeULong(mo, "TotalVisibleMemorySize");
                    ulong freeKb = SafeULong(mo, "FreePhysicalMemory");
                    if (totalKb > 0) Memory.TotalBytes = totalKb * 1024;
                    Memory.FreeBytes = freeKb * 1024;
                    Memory.InUseBytes = (Memory.TotalBytes > Memory.FreeBytes) ? Memory.TotalBytes - Memory.FreeBytes : 0;
                    break;
                }
                using var perfMem = CreateSearcher("SELECT PoolPagedBytes,PoolNonpagedBytes,CacheBytes FROM Win32_PerfFormattedData_PerfOS_Memory");
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

                // Agregamos 'DriverDate' y 'InstalledDisplayDrivers' (a veces ayuda con ubicación)
                // Nota: La ubicación PCI exacta "Bus 3, Dev 0" requiere parsear PNPDeviceID o usar Win32_Bus,
                // pero 'PNPDeviceID' suele ser suficiente para identificarla internamente.
                // Para la UI, WMI 'Win32_PnPEntity' tiene la ubicación amigable.

                using var search = CreateSearcher("SELECT Name, DriverVersion, DriverDate, AdapterRAM, PNPDeviceID FROM Win32_VideoController");

                foreach (var mo in SafeEnumerate(search))
                {
                    var info = new GpuDetailInfo
                    {
                        Name = SafeString(mo, "Name"),
                        DriverVersion = SafeString(mo, "DriverVersion"),
                        TotalDedicatedBytes = SafeULong(mo, "AdapterRAM"),
                        PnpDeviceId = SafeString(mo, "PNPDeviceID")
                    };

                    // 1. Parsear Fecha (Viene como string raro "20251029000000...")
                    string dateStr = SafeString(mo, "DriverDate");
                    if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 8)
                    {
                        // Formato WMI: yyyyMMdd
                        if (DateTime.TryParseExact(dateStr.Substring(0, 8), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime dt))
                        {
                            info.DriverDate = dt;
                        }
                    }

                    // 2. Obtener Memoria Compartida (Estimación del Sistema)
                    // Windows suele reservar el 50% de la RAM del sistema como compartida para GPU.
                    // Podemos usar SystemInfoHelper.GetGpuTotalMemory() para la dedicada real si WMI falla,
                    // pero para la compartida, una buena aproximación es: (RAM Total / 2).
                    // Si queremos el dato EXACTO de D3DKMT, necesitamos ampliar SystemInfoHelper.
                    // Por ahora, usaremos la regla estándar de Windows:
                    ulong sysRam = SystemInfoHelper.GetTotalPhysicalMemory();
                    info.TotalSharedBytes = sysRam / 2;

                    // 3. Ubicación Física (Bus PCI)
                    // Esto requiere una segunda consulta cruzada con PnPEntity usando el ID del dispositivo
                    info.Location = GetPnpLocation(info.PnpDeviceId);

                    GpuList.Add(info);

                    if (string.IsNullOrEmpty(Gpu.Name)) Gpu = info;
                }

                if (GpuList.Count > 0 && string.IsNullOrEmpty(Gpu.Name)) Gpu = GpuList[0];
            }
            catch { }
        }

        // Helper para buscar la ubicación bonita ("Bus PCI 3...")
        private string GetPnpLocation(string pnpDeviceId)
        {
            try
            {
                // Escapar las barras invertidas para WMI
                string cleanId = pnpDeviceId.Replace("\\", "\\\\");
                using var search = CreateSearcher($"SELECT LocationInformation FROM Win32_PnPEntity WHERE DeviceID = '{cleanId}'");
                foreach (var item in SafeEnumerate(search))
                {
                    return SafeString(item, "LocationInformation"); // Ej: "PCI bus 0, device 2, function 0"
                }
            }
            catch { }
            return "Bus PCI desconocido";
        }

        // ----------- DISCO ESTÁTICO -----------
        private void LoadDisks()
        {
            try
            {
                Disks.Clear();
                var physical = new Dictionary<string, (string model, ulong size)>(StringComparer.OrdinalIgnoreCase);
                using (var diskSearch = CreateSearcher("SELECT DeviceID,Model,Size FROM Win32_DiskDrive"))
                {
                    foreach (var mo in SafeEnumerate(diskSearch))
                    {
                        string id = SafeString(mo, "DeviceID");
                        if (!string.IsNullOrEmpty(id))
                            physical[id] = (SafeString(mo, "Model"), SafeULong(mo, "Size"));
                    }
                }

                // Volúmenes
                using var volSearch = CreateSearcher("SELECT DriveLetter,Capacity,FreeSpace FROM Win32_Volume WHERE DriveLetter IS NOT NULL");
                foreach (var mo in SafeEnumerate(volSearch))
                {
                    var disk = new DiskStaticInfo
                    {
                        DriveLetter = SafeString(mo, "DriveLetter"),
                        CapacityBytes = SafeULong(mo, "Capacity"),
                        FreeBytes = SafeULong(mo, "FreeSpace"),
                        Model = physical.Values.FirstOrDefault().model ?? ""
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
                using var search = CreateSearcher("SELECT Name,Speed,MACAddress FROM Win32_NetworkAdapter WHERE NetEnabled = TRUE");
                foreach (var mo in SafeEnumerate(search))
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

            try
            {
                // Requiere agregar referencia a Microsoft.Management.Infrastructure o usar dynamic con WMI clásico en root\Microsoft\Windows\Storage
                using var searcher = new ManagementObjectSearcher(@"\\.\root\Microsoft\Windows\Storage", "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk");
                foreach (var disk in searcher.Get())
                {
                    // Mapear MediaType: 3 -> HDD, 4 -> SSD
                }
            }
            catch { /* Fallback a rotación 0 para SSD */ }
        }

        // ----------- CONTADORES DINÁMICOS -----------
        private void InitializeDynamicCounters()
        {
            InitializeCpuCoreCounters();
            InitializeDiskInstanceCounters();
            InitializeNetworkInstanceCounters();
            InitializeGpuAggregator();
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
                    if (!int.TryParse(inst, out _)) continue; // Sólo núcleos numéricos
                    try
                    {
                        var pc = new PerformanceCounter("Processor", "% Processor Time", inst, true);
                        pc.NextValue();
                        _cpuCoreCounters.Add(pc);
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
                    if (lower.Contains("loopback") || lower.Contains("isatap") || lower.Contains("teredo"))
                        continue;
                    if (_netCounters.ContainsKey(inst)) continue;
                    try
                    {
                        var sent = new PerformanceCounter("Network Interface", "Bytes Sent/sec", inst, true);
                        var recv = new PerformanceCounter("Network Interface", "Bytes Received/sec", inst, true);
                        var bw = new PerformanceCounter("Network Interface", "Current Bandwidth", inst, true);
                        sent.NextValue(); recv.NextValue(); bw.NextValue();
                        _netCounters[inst] = (sent, recv, bw);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void InitializeGpuAggregator()
        {
            try
            {
                if (WindowsVersion.IsWindows7) return;
                _gpuAggregator = new GpuEngineAggregator();
                _gpuAggregator.Initialize();
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
                    var a = kv.Value.active.NextValue(); // % Disk Time
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

        private void RefreshGpuAdapterUsage()
        {
            GpuAdapterUsage.Clear();
            if (WindowsVersion.IsWindows7 || _gpuAggregator?.LastSnapshot == null)
                return;

            var snap = _gpuAggregator.LastSnapshot;
            foreach (var a in snap.Adapters)
            {
                GpuAdapterUsage.Add(new GpuAdapterDynamicInfo
                {
                    AdapterKey = a.AdapterKey,
                    GlobalUsagePercent = a.GlobalUsagePercent,
                    ThreeDPercent = a.ThreeDPercent,
                    ComputePercent = a.ComputePercent
                });
            }
        }

        // ----------- HELPERS WMI -----------
        private static ManagementObjectSearcher CreateSearcher(string wql)
        {
            var scope = new ManagementScope(@"\\.\root\cimv2");
            var query = new ObjectQuery(wql);
            var options = new System.Management.EnumerationOptions
            {
                ReturnImmediately = true,
                Rewindable = false,
                DirectRead = true
            };
            return new ManagementObjectSearcher(scope, query, options);
        }

        private static IEnumerable<ManagementObject> SafeEnumerate(ManagementObjectSearcher searcher)
        {
            var list = new List<ManagementObject>();
            ManagementObjectCollection? col = null;
            try
            {
                col = searcher.Get();
                foreach (var obj in col)
                {
                    if (obj is ManagementObject mo)
                        list.Add(mo);
                }
            }
            catch { }
            finally
            {
                col?.Dispose();
            }
            return list;
        }

        private static string SafeString(ManagementBaseObject mo, string prop)
        {
            try
            {
                var v = mo[prop];
                if (v == null) return "";
                var s = v.ToString()?.Trim() ?? "";
                if (s.Length > 512) s = s.Substring(0, 512);
                return s;
            }
            catch { return ""; }
        }

        private static int SafeInt(ManagementBaseObject mo, string prop)
        {
            try
            {
                var v = mo[prop];
                if (v == null) return 0;
                int val = Convert.ToInt32(v);
                return val < 0 ? 0 : val;
            }
            catch { return 0; }
        }

        private static ulong SafeULong(ManagementBaseObject mo, string prop)
        {
            try
            {
                var v = mo[prop];
                if (v == null) return 0;
                return Convert.ToUInt64(v);
            }
            catch { return 0; }
        }

        private static int MaxPositive(int current, int candidate) =>
            candidate > current && candidate > 0 ? candidate : current;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var c in _cpuCoreCounters) try { c.Dispose(); } catch { }
            foreach (var d in _diskCounters.Values)
            {
                try { d.active.Dispose(); } catch { }
                try { d.read.Dispose(); } catch { }
                try { d.write.Dispose(); } catch { }
            }
            foreach (var n in _netCounters.Values)
            {
                try { n.sent.Dispose(); } catch { }
                try { n.recv.Dispose(); } catch { }
                try { n.bw.Dispose(); } catch { }
            }
            _gpuAggregator?.Dispose();
        }
    }

    // -------- MODELOS ESTÁTICOS --------
    public sealed class CpuDetailInfo
    {
        public string Name { get; set; } = "";
        public int Sockets { get; set; }
        public int PhysicalCores { get; set; }
        public int LogicalProcessors { get; set; }
        public int BaseClockMHz { get; set; }
        public int L2CacheKB { get; set; }
        public int L3CacheKB { get; set; }
        public bool VirtualizationEnabled { get; set; }
    }

    public sealed class MemoryDetailInfo
    {
        public ulong TotalBytes { get; set; }
        public ulong FreeBytes { get; set; }
        public ulong InUseBytes { get; set; }
        public ulong HardwareReservedBytes { get; set; }
        public ulong CacheBytes { get; set; }
        public ulong PagedPoolBytes { get; set; }
        public ulong NonPagedPoolBytes { get; set; }
    }

    public sealed class DiskStaticInfo
    {
        public string DriveLetter { get; set; } = "";
        public string Model { get; set; } = "";
        public ulong CapacityBytes { get; set; }
        public ulong FreeBytes { get; set; }
    }

    public sealed class NetworkAdapterStaticInfo
    {
        public string Name { get; set; } = "";
        public ulong SpeedBitsPerSec { get; set; }
        public string MacAddress { get; set; } = "";
    }

    public sealed class GpuDetailInfo
    {
        public string Name { get; set; } = "";
        public string DriverVersion { get; set; } = "";
        public DateTime DriverDate { get; set; } // ¡NUEVO!
        public string Location { get; set; } = ""; // ¡NUEVO! (Bus PCI...)

        public ulong TotalDedicatedBytes { get; set; }
        public ulong TotalSharedBytes { get; set; } // ¡NUEVO!

        public string PnpDeviceId { get; set; } = "";
    }

    // -------- MODELOS DINÁMICOS --------
    public sealed class CpuCoreUsageInfo
    {
        public int CoreIndex { get; set; }
        public double UsagePercent { get; set; }
    }

    public sealed class DiskDynamicInfo
    {
        public string Instance { get; set; } = "";
        public double ActiveTimePercent { get; set; }
        public double ReadBytesPerSec { get; set; }
        public double WriteBytesPerSec { get; set; }
    }

    public sealed class NetworkAdapterDynamicInfo
    {
        public string Name { get; set; } = "";
        public double SentBytesPerSec { get; set; }
        public double ReceivedBytesPerSec { get; set; }
        public double BandwidthBytesPerSec { get; set; }
    }

    public sealed class GpuAdapterDynamicInfo
    {
        public string AdapterKey { get; set; } = "";
        public double GlobalUsagePercent { get; set; }
        public double ThreeDPercent { get; set; }
        public double ComputePercent { get; set; }
    }
}