using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Linq;
using ModernTaskManager.Core.Helpers;
using ModernTaskManager.Core.Models;
using ModernTaskManager.Core.Gpu;

namespace ModernTaskManager.Core.Services.GPU
{
    public class ModernGpuProvider : IGpuUsageProvider
    {
        public string ProviderName => "Modern (WDDM + D3DKMT)";
        public bool IsSupported { get; private set; } = false;

        // Contadores (Listas para sumar todas las instancias)
        private readonly List<PerformanceCounter> _gpu3dCounters = new();
        private readonly List<PerformanceCounter> _gpuVramCounters = new();
        private readonly List<PerformanceCounter> _gpuSharedCounters = new();

        private string? _gpuCategoryName;
        private string? _gpuMemCategoryName;
        private int _refreshTick = 0;
        private ulong _totalVramCached = 0;

        // Nombres de contadores cacheados que sabemos que funcionan
        private string _valid3dCounterName = "";
        private string _validVramCounterName = "";
        private string _validSharedCounterName = "";

        public void Initialize()
        {
            // 1. Resolver categoría MOTOR 3D
            // Probamos nombres comunes directamente
            string[] engineNames = { "GPU Engine", "Motor de GPU" };
            foreach (var name in engineNames)
            {
                if (PerformanceCounterCategory.Exists(name)) { _gpuCategoryName = name; break; }
            }

            if (string.IsNullOrEmpty(_gpuCategoryName))
            {
                IsSupported = false;
                return;
            }

            IsSupported = true; // Al menos tenemos motor

            // 2. Resolver categoría MEMORIA
            string[] memNames = { "GPU Adapter Memory", "Memoria de adaptador de GPU" };
            foreach (var name in memNames)
            {
                if (PerformanceCounterCategory.Exists(name)) { _gpuMemCategoryName = name; break; }
            }

            // 3. Inicializar
            RefreshCounters();
            SetupMemoryCounters();

            // 4. VRAM Total
            try { _totalVramCached = SystemInfoHelper.GetGpuTotalMemory(); } catch { }
        }

        public GpuDetailInfo GetStaticInfo()
        {
            var info = new GpuDetailInfo();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController");
                foreach (var mo in searcher.Get())
                {
                    info.Name = mo["Name"]?.ToString() ?? "GPU Genérica";
                    info.DriverVersion = mo["DriverVersion"]?.ToString() ?? "";
                    info.PnpDeviceId = mo["PNPDeviceID"]?.ToString() ?? "";

                    string dateStr = mo["DriverDate"]?.ToString() ?? "";
                    if (dateStr.Length >= 8 && DateTime.TryParseExact(dateStr.Substring(0, 8), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt))
                        info.DriverDate = dt;

                    if (info.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                        info.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase))
                        break;
                }

                info.TotalDedicatedBytes = _totalVramCached > 0 ? _totalVramCached : SystemInfoHelper.GetGpuTotalMemory();
                info.TotalSharedBytes = SystemInfoHelper.GetTotalPhysicalMemory() / 2;
            }
            catch { }
            return info;
        }

        public GpuAdapterDynamicInfo GetUsage()
        {
            if (!IsSupported) return new GpuAdapterDynamicInfo();

            if (++_refreshTick > 20)
            {
                RefreshCounters();
                _refreshTick = 0;
            }

            double total3d = 0;
            ulong vram = 0;
            ulong shared = 0;

            // 1. Sumar 3D
            foreach (var c in _gpu3dCounters)
            {
                try { total3d += c.NextValue(); } catch { }
            }

            // 2. Sumar VRAM (Todas las instancias)
            foreach (var c in _gpuVramCounters)
            {
                try { vram += (ulong)c.NextValue(); } catch { }
            }

            foreach (var c in _gpuSharedCounters)
            {
                try { shared += (ulong)c.NextValue(); } catch { }
            }

            return new GpuAdapterDynamicInfo
            {
                GlobalUsagePercent = Math.Min(100.0, total3d),
                DedicatedMemoryUsed = vram,
                SharedMemoryUsed = shared
            };
        }

        private void RefreshCounters()
        {
            if (string.IsNullOrEmpty(_gpuCategoryName)) return;

            foreach (var c in _gpu3dCounters) c.Dispose();
            _gpu3dCounters.Clear();

            try
            {
                var cat = new PerformanceCounterCategory(_gpuCategoryName);
                var instances = cat.GetInstanceNames();

                // Si no tenemos el nombre validado aún, lo buscamos
                if (string.IsNullOrEmpty(_valid3dCounterName))
                {
                    string[] candidates = { "Utilization Percentage", "Porcentaje de utilización" };
                    _valid3dCounterName = FindWorkingCounterName(_gpuCategoryName, instances.FirstOrDefault(), candidates);
                }

                if (string.IsNullOrEmpty(_valid3dCounterName)) return;

                foreach (var inst in instances)
                {
                    if (inst.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase) ||
                        inst.Contains("3D", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var pc = new PerformanceCounter(_gpuCategoryName, _valid3dCounterName, inst);
                            pc.NextValue();
                            _gpu3dCounters.Add(pc);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private void SetupMemoryCounters()
        {
            if (string.IsNullOrEmpty(_gpuMemCategoryName)) return;

            foreach (var c in _gpuVramCounters) c.Dispose();
            foreach (var c in _gpuSharedCounters) c.Dispose();
            _gpuVramCounters.Clear();
            _gpuSharedCounters.Clear();

            try
            {
                var cat = new PerformanceCounterCategory(_gpuMemCategoryName);
                var instances = cat.GetInstanceNames();
                if (instances.Length == 0) return;

                // Buscar nombres correctos si no los tenemos
                if (string.IsNullOrEmpty(_validVramCounterName))
                {
                    string[] dedCandidates = { "Dedicated Usage", "Bytes dedicados", "Dedicated Memory" };
                    _validVramCounterName = FindWorkingCounterName(_gpuMemCategoryName, instances[0], dedCandidates);
                }

                if (string.IsNullOrEmpty(_validSharedCounterName))
                {
                    string[] sharedCandidates = { "Shared Usage", "Bytes compartidos", "Shared Memory" };
                    _validSharedCounterName = FindWorkingCounterName(_gpuMemCategoryName, instances[0], sharedCandidates);
                }

                // Si falló la búsqueda de nombres, abortamos para no llenar de excepciones
                if (string.IsNullOrEmpty(_validVramCounterName) || string.IsNullOrEmpty(_validSharedCounterName)) return;

                foreach (var inst in instances)
                {
                    try
                    {
                        var vramCounter = new PerformanceCounter(_gpuMemCategoryName, _validVramCounterName, inst);
                        var sharedCounter = new PerformanceCounter(_gpuMemCategoryName, _validSharedCounterName, inst);

                        vramCounter.NextValue();
                        sharedCounter.NextValue();

                        _gpuVramCounters.Add(vramCounter);
                        _gpuSharedCounters.Add(sharedCounter);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Método auxiliar para probar nombres hasta que uno no explote
        private string FindWorkingCounterName(string category, string? instance, string[] candidates)
        {
            if (string.IsNullOrEmpty(instance)) return candidates[0]; // Fallback

            foreach (var counterName in candidates)
            {
                try
                {
                    using var pc = new PerformanceCounter(category, counterName, instance);
                    pc.NextValue(); // Si esto no lanza excepción, el nombre es válido
                    return counterName;
                }
                catch { /* Siguiente candidato */ }
            }
            return ""; // Ninguno funcionó
        }

        public void Dispose()
        {
            foreach (var c in _gpu3dCounters) c.Dispose();
            foreach (var c in _gpuVramCounters) c.Dispose();
            foreach (var c in _gpuSharedCounters) c.Dispose();
        }
    }
}