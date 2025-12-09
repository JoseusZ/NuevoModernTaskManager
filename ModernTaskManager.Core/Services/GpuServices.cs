using System;
using ModernTaskManager.Core.Gpu;
using ModernTaskManager.Core.Models;
using ModernTaskManager.Core.Services.GPU;
using LegacyGpuProvider = ModernTaskManager.Core.Services.GPU.ExtremeLegacyGpuProvider;

namespace ModernTaskManager.Core.Services
{
    public class GpuService : IDisposable
    {
        private IGpuUsageProvider _primary;
        private IGpuUsageProvider? _fallback; // NVAPI/ADL/D3DKMT fallback for NVIDIA/legacy drivers
        public GpuDetailInfo StaticInfo { get; private set; }

        public GpuService()
        {
            Console.WriteLine("--- INICIANDO SERVICIO DE GPU ---");

            var modern = new ModernGpuProvider();
            try { modern.Initialize(); }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR CRÍTICO] ModernGpuProvider lanzó excepción: {ex.Message}");
                Console.ResetColor();
            }

            if (modern.IsSupported)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[ÉXITO] ModernGpuProvider cargado correctamente.");
                Console.ResetColor();
                _primary = modern;
            }
            else
            {
                modern.Dispose();

                // 2. Proveedor extremo NVAPI/ADL/D3DKMT (Windows 7 y drivers antiguos)
                var extreme = new ExtremeLegacyGpuProvider();
                try { extreme.Initialize(); } catch { }
                if (extreme.IsSupported)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("[ÉXITO] ExtremeLegacyGpuProvider cargado (NVAPI/ADL/D3DKMT).");
                    Console.ResetColor();
                    _primary = extreme;
                }
                else
                {
                    extreme.Dispose();

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("[ADVERTENCIA] Modern y ExtremeLegacy no disponibles. Cambiando a Legacy (WMI).");
                    Console.ResetColor();

                    _primary = new LegacyGpuProvider();
                    _primary.Initialize();
                }
            }

            // Si el proveedor primario es Modern, crear fallback ExtremeLegacy para mejorar precisión en NVIDIA/AMD
            if (_primary is ModernGpuProvider)
            {
                var extremeFallback = new ExtremeLegacyGpuProvider();
                try { extremeFallback.Initialize(); } catch { }
                if (extremeFallback.IsSupported)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine("[INFO] Activado fallback NVAPI/ADL para ajustar % GPU cuando WDDM subestima.");
                    Console.ResetColor();
                    _fallback = extremeFallback;
                }
                else
                {
                    extremeFallback.Dispose();
                }
            }

            StaticInfo = _primary.GetStaticInfo();
        }

        public GpuAdapterDynamicInfo GetGpuUsage()
        {
            try
            {
                var primary = _primary.GetUsage();

                // Usar fallback si existe y da un valor más alto y plausible
                if (_fallback != null)
                {
                    var fb = _fallback.GetUsage();
                    // Tomar el mejor estimador (máximo) dentro de 0..100
                    if (fb.GlobalUsagePercent > primary.GlobalUsagePercent && fb.GlobalUsagePercent <= 100)
                    {
                        // Conservar memoria del primario si es más rica
                        fb.DedicatedMemoryUsed = primary.DedicatedMemoryUsed != 0 ? primary.DedicatedMemoryUsed : fb.DedicatedMemoryUsed;
                        fb.SharedMemoryUsed = primary.SharedMemoryUsed != 0 ? primary.SharedMemoryUsed : fb.SharedMemoryUsed;
                        return fb;
                    }
                }

                return primary;
            }
            catch { return new GpuAdapterDynamicInfo(); }
        }

        public string GetProviderName()
        {
            if (_fallback != null && _primary is ModernGpuProvider)
                return $"{_primary.ProviderName} + Fallback";
            return _primary.ProviderName;
        }

        public void Dispose()
        {
            try { _primary?.Dispose(); } catch { }
            try { _fallback?.Dispose(); } catch { }
        }
    }
}