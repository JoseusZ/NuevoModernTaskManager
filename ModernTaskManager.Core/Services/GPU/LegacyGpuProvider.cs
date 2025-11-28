// Ruta: ModernTaskManager.Core/Gpu/LegacyGpuProvider.cs
using System;
using System.Management;
using ModernTaskManager.Core.Models; // Necesario para GpuDetailInfo

namespace ModernTaskManager.Core.Gpu
{
    public class LegacyGpuProvider : IGpuUsageProvider
    {
        public string ProviderName => "Legacy (WMI Basic)";
        public bool IsSupported => true;

        public void Initialize() { }

        public GpuDetailInfo GetStaticInfo()
        {
            var info = new GpuDetailInfo();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController");
                foreach (var mo in searcher.Get())
                {
                    info.Name = mo["Name"]?.ToString() ?? "Pantalla estándar";
                    info.DriverVersion = mo["DriverVersion"]?.ToString() ?? "N/A";
                    info.Location = mo["PNPDeviceID"]?.ToString() ?? "";

                    string dateStr = mo["DriverDate"]?.ToString() ?? "";
                    if (dateStr.Length >= 8 && DateTime.TryParseExact(dateStr.Substring(0, 8), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt))
                        info.DriverDate = dt;

                    if (ulong.TryParse(mo["AdapterRAM"]?.ToString(), out ulong ram))
                        info.TotalDedicatedBytes = ram;

                    break;
                }
            }
            catch { }
            return info;
        }

        public GpuAdapterDynamicInfo GetUsage()
        {
            return new GpuAdapterDynamicInfo();
        }

        public void Dispose() { }
    }
}