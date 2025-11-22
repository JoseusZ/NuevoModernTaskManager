// ModernTaskManager.Tests/GPUTests.cs
using System;
using ModernTaskManager.Core.Helpers;
using ModernTaskManager.Core.Native;
using ModernTaskManager.Core.Services;

namespace ModernTaskManager.Tests
{
    public class GPUTests
    {
        public static void RunAllTests()
        {
            Console.WriteLine("=== INICIANDO PRUEBAS GPU ===\n");

            TestWindowsVersionDetection();
            TestDXGIAvailability();
            TestD3DKMTFallback();
            TestPerformanceServiceIntegration();
            TestCrossPlatformCompatibility();

            Console.WriteLine("\n=== PRUEBAS COMPLETADAS ===");
        }

        public static void TestWindowsVersionDetection()
        {
            Console.WriteLine("1. Probando detección de versión de Windows...");

            Console.WriteLine($"   Windows 7: {WindowsVersion.IsWindows7}");
            Console.WriteLine($"   Windows 8: {WindowsVersion.IsWindows8}");
            Console.WriteLine($"   Windows 8.1: {WindowsVersion.IsWindows81}");
            Console.WriteLine($"   Windows 10+: {WindowsVersion.IsWindows10OrGreater}");
            Console.WriteLine($"   Windows 8+: {WindowsVersion.IsWindows8OrGreater}");

            Console.WriteLine("   ✓ Detección de versión completada\n");
        }

        public static void TestDXGIAvailability()
        {
            Console.WriteLine("2. Probando disponibilidad de DXGI...");

            try
            {
                bool dxgiAvailable = DXGIWrapper.IsDXGIAvailable();
                Console.WriteLine($"   DXGI disponible: {dxgiAvailable}");

                if (dxgiAvailable)
                {
                    var result = DXGIWrapper.QueryVideoMemoryInfo();
                    Console.WriteLine($"   DXGI éxito: {result.Success}");
                    Console.WriteLine($"   VRAM dedicada: {FormatBytes(result.DedicatedVideoMemory)}");
                    Console.WriteLine($"   VRAM compartida: {FormatBytes(result.SharedSystemMemory)}");
                    Console.WriteLine($"   Uso actual: {FormatBytes(result.CurrentUsage)}");
                    Console.WriteLine($"   Budget: {FormatBytes(result.Budget)}");
                }
                else
                {
                    Console.WriteLine("   DXGI no disponible - usando fallback");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ Error en DXGI: {ex.Message}");
            }

            Console.WriteLine("   ✓ Prueba DXGI completada\n");
        }

        public static void TestD3DKMTFallback()
        {
            Console.WriteLine("3. Probando fallback D3DKMT...");

            try
            {
                var stats = D3DKMT.QueryVideoMemoryStatistics();
                if (stats != null && stats.IsValid)
                {
                    Console.WriteLine($"   D3DKMT válido: Sí");
                    Console.WriteLine($"   VRAM dedicada: {FormatBytes(stats.TotalDedicatedBytes)}");
                    Console.WriteLine($"   VRAM compartida: {FormatBytes(stats.TotalSharedBytes)}");
                    Console.WriteLine($"   Uso: {FormatBytes(stats.TotalCurrentUsageBytes)}");
                }
                else
                {
                    Console.WriteLine("   D3DKMT no disponible");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ Error en D3DKMT: {ex.Message}");
            }

            Console.WriteLine("   ✓ Prueba D3DKMT completada\n");
        }

        public static void TestPerformanceServiceIntegration()
        {
            Console.WriteLine("4. Probando integración con PerformanceService...");

            try
            {
                using (var performanceService = new PerformanceService())
                {
                    var gpuInfo = performanceService.GetGpuUsage();

                    Console.WriteLine($"   Uso GPU: {gpuInfo.GpuUsagePercent:F1}%");
                    Console.WriteLine($"   VRAM total: {FormatBytes(gpuInfo.DedicatedMemoryTotal)}");
                    Console.WriteLine($"   VRAM usada: {FormatBytes(gpuInfo.DedicatedMemoryUsed)}");
                    Console.WriteLine($"   VRAM compartida: {FormatBytes(gpuInfo.SharedMemory)}");
                    Console.WriteLine($"   Budget: {FormatBytes(gpuInfo.Budget)}");
                    Console.WriteLine($"   VRAM soportada: {gpuInfo.VramSupported}");

                    // Probar múltiples lecturas para verificar estabilidad
                    for (int i = 0; i < 3; i++)
                    {
                        System.Threading.Thread.Sleep(1000);
                        gpuInfo = performanceService.GetGpuUsage();
                        Console.WriteLine($"   Lectura {i + 1}: {gpuInfo.GpuUsagePercent:F1}% uso, {FormatBytes(gpuInfo.DedicatedMemoryUsed)} usadas");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ✗ Error en PerformanceService: {ex.Message}");
            }

            Console.WriteLine("   ✓ Prueba PerformanceService completada\n");
        }

        public static void TestCrossPlatformCompatibility()
        {
            Console.WriteLine("5. Verificando compatibilidad multi-plataforma...");

            var expectedBehavior = WindowsVersion.IsWindows7 ?
                "Windows 7: VRAM usada debe ser 0, no soportada" :
                "Windows 8+: VRAM debe estar disponible";

            Console.WriteLine($"   Comportamiento esperado: {expectedBehavior}");

            using (var performanceService = new PerformanceService())
            {
                var gpuInfo = performanceService.GetGpuUsage();

                if (WindowsVersion.IsWindows7)
                {
                    bool correctBehavior = !gpuInfo.VramSupported && gpuInfo.DedicatedMemoryUsed == 0;
                    Console.WriteLine($"   Comportamiento Windows 7 correcto: {correctBehavior}");
                }
                else
                {
                    bool correctBehavior = gpuInfo.VramSupported && gpuInfo.DedicatedMemoryTotal > 0;
                    Console.WriteLine($"   Comportamiento Windows 8+ correcto: {correctBehavior}");
                }
            }

            Console.WriteLine("   ✓ Verificación de compatibilidad completada\n");
        }

        private static string FormatBytes(ulong bytes)
        {
            if (bytes == 0) return "0 B";

            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = (decimal)bytes;

            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }

            return $"{number:n1} {suffixes[counter]}";
        }
    }
}