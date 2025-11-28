using System;
using ModernTaskManager.Core.Gpu;
using ModernTaskManager.Core.Models;
using ModernTaskManager.Core.Services.GPU;
using LegacyGpuProvider = ModernTaskManager.Core.Services.GPU.ExtremeLegacyGpuProvider;

namespace ModernTaskManager.Core.Services
{
    public class GpuService : IDisposable
    {
        private IGpuUsageProvider _provider;
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
                _provider = modern;
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
                    _provider = extreme;
                }
                else
                {
                    extreme.Dispose();

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("[ADVERTENCIA] Modern y ExtremeLegacy no disponibles. Cambiando a Legacy (WMI).");
                    Console.ResetColor();

                    _provider = new LegacyGpuProvider();
                    _provider.Initialize();
                }
            }

            StaticInfo = _provider.GetStaticInfo();
        }

        public GpuAdapterDynamicInfo GetGpuUsage()
        {
            try { return _provider.GetUsage(); }
            catch { return new GpuAdapterDynamicInfo(); }
        }

        public string GetProviderName() => _provider.ProviderName;

        public void Dispose() { _provider?.Dispose(); }
    }
}