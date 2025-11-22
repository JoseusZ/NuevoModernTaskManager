// ModernTaskManager.Tests/ArchitectureValidator.cs
using ModernTaskManager.Core.Helpers;
using ModernTaskManager.Core.Native;
using ModernTaskManager.Core.Services;
using System;
using System.Linq;
using System.Reflection;

namespace ModernTaskManager.Tests
{
    public class ArchitectureValidator
    {
        public static void ValidateDXGIFirstArchitecture()
        {
            Console.WriteLine("=== VALIDACIÓN DE ARQUITECTURA DXGI-FIRST ===\n");

            ValidatePerformanceServiceStructure();
            ValidateFallbackMechanisms();
            ValidateWindows7Compatibility();

            Console.WriteLine("✓ Validación de arquitectura completada\n");
        }

        private static void ValidatePerformanceServiceStructure()
        {
            Console.WriteLine("1. Validando estructura de PerformanceService...");

            var serviceType = typeof(PerformanceService);
            var fields = serviceType.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            bool hasDxgiFields = fields.Any(f => f.Name.Contains("_dxgi"));
            bool hasGpuFields = fields.Any(f => f.Name.Contains("_gpu"));

            Console.WriteLine($"   Campos DXGI encontrados: {hasDxgiFields}");
            Console.WriteLine($"   Campos GPU encontrados: {hasGpuFields}");

            var gpuInfoType = typeof(GpuUsageInfo);
            var gpuProperties = gpuInfoType.GetProperties();

            bool hasBudgetProperty = gpuProperties.Any(p => p.Name == "Budget");
            bool hasSharedProperty = gpuProperties.Any(p => p.Name == "SharedMemory");

            Console.WriteLine($"   Propiedad Budget en GpuUsageInfo: {hasBudgetProperty}");
            Console.WriteLine($"   Propiedad SharedMemory en GpuUsageInfo: {hasSharedProperty}");
        }

        private static void ValidateFallbackMechanisms()
        {
            Console.WriteLine("2. Validando mecanismos de fallback...");

            try
            {
                bool dxgiAvailable = DXGIWrapper.IsDXGIAvailable();
                Console.WriteLine($"   DXGI disponible: {dxgiAvailable}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   DXGI error: {ex.GetType().Name} - {ex.Message}");
            }

            // Verificar que al menos un método de fallback esté disponible
            try
            {
                bool performanceCountersAvailable = System.Diagnostics.PerformanceCounterCategory.Exists("GPU Engine");
                Console.WriteLine($"   Performance Counters GPU disponibles: {performanceCountersAvailable}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   Performance Counters error: {ex.GetType().Name}");
            }

            // Verificar WMI como último recurso
            try
            {
                ulong wmiMemory = SystemInfoHelper.GetGpuTotalMemory();
                Console.WriteLine($"   WMI VRAM total: {FormatBytes(wmiMemory)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   WMI error: {ex.GetType().Name}");
            }
        }

        private static void ValidateWindows7Compatibility()
        {
            Console.WriteLine("3. Validando compatibilidad Windows 7...");

            if (WindowsVersion.IsWindows7)
            {
                try
                {
                    using (var service = new PerformanceService())
                    {
                        var gpuInfo = service.GetGpuUsage();

                        bool correctWindows7Behavior =
                            !gpuInfo.VramSupported &&
                            gpuInfo.DedicatedMemoryUsed == 0;

                        Console.WriteLine($"   Comportamiento Windows 7 correcto: {correctWindows7Behavior}");
                        Console.WriteLine($"     - VRAM no soportada: {!gpuInfo.VramSupported}");
                        Console.WriteLine($"     - VRAM usada = 0: {gpuInfo.DedicatedMemoryUsed == 0}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   Error en Windows 7 test: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"   No es Windows 7 - skip");
            }
        }

        private static string FormatBytes(ulong bytes)
        {
            if (bytes == 0) return "0 B";
            string[] suffixes = { "B", "KB", "MB", "GB" };
            int counter = 0;
            decimal number = (decimal)bytes;

            while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
            {
                number /= 1024;
                counter++;
            }

            return $"{number:n1} {suffixes[counter]}";
        }
    }
}