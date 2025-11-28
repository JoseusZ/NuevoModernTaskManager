// Ruta: ModernTaskManager.Core/Models/HardwareModels.cs
using System;

namespace ModernTaskManager.Core.Models
{
    // --- DATOS ESTÁTICOS ---

    public sealed class GpuDetailInfo
    {
        public string Name { get; set; } = "";
        public string DriverVersion { get; set; } = "";
        public DateTime DriverDate { get; set; }
        public string Location { get; set; } = "";
        public ulong TotalDedicatedBytes { get; set; }
        public ulong TotalSharedBytes { get; set; }
        public string PnpDeviceId { get; set; } = "";
    }

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

    // --- DATOS DINÁMICOS ---

    public sealed class GpuAdapterDynamicInfo
    {
        public string AdapterKey { get; set; } = "";
        public double GlobalUsagePercent { get; set; }
        public double ThreeDPercent { get; set; }
        public double ComputePercent { get; set; }

        // Propiedades agregadas para compatibilidad con la UI
        public ulong DedicatedMemoryUsed { get; set; }
        public ulong SharedMemoryUsed { get; set; }
    }

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
}