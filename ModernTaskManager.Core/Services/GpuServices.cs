using System;
using ModernTaskManager.Core.Gpu;
using ModernTaskManager.Core.Models;
// Asegúrate de que estos usings apunten a donde guardaste los archivos
using ModernTaskManager.Core.Services.GPU;

namespace ModernTaskManager.Core.Services
{
    public class GpuService : IDisposable
    {
        private IGpuUsageProvider _provider;
        public GpuDetailInfo StaticInfo { get; private set; }

        public GpuService()
        {
            Console.WriteLine("--- INICIANDO SERVICIO DE GPU ---");

            // 1. Intentar iniciar el proveedor Moderno (WDDM/D3DKMT)
            var modern = new ModernGpuProvider();

            // CAPTURA DE ERRORES DE INICIALIZACIÓN
            try
            {
                modern.Initialize();
            }
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
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[ADVERTENCIA] ModernGpuProvider no es compatible o falló. Cambiando a Legacy.");
                Console.WriteLine("Posibles causas:");
                Console.WriteLine("1. No estás ejecutando como ADMINISTRADOR (Vital para contadores de GPU).");
                Console.WriteLine("2. Los contadores de rendimiento están dañados (ejecutar 'lodctr /r' en CMD).");
                Console.WriteLine("3. El nombre de la categoría 'GPU Engine' no se encontró en tu idioma.");
                Console.ResetColor();

                modern.Dispose();
                _provider = new LegacyGpuProvider();
                _provider.Initialize();
            }

            // Cargar datos estáticos al inicio
            StaticInfo = _provider.GetStaticInfo();
        }

        public GpuAdapterDynamicInfo GetGpuUsage()
        {
            try
            {
                return _provider.GetUsage();
            }
            catch
            {
                return new GpuAdapterDynamicInfo();
            }
        }

        public string GetProviderName() => _provider.ProviderName;

        public void Dispose()
        {
            _provider?.Dispose();
        }
    }
}