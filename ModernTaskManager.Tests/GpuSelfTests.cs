using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Diagnostics; // PerformanceCounterCategory
using ModernTaskManager.Core.Native;
using ModernTaskManager.Core.Services;
using ModernTaskManager.Core.Helpers; // <-- Necesario para WindowsVersion

namespace ModernTaskManager.Tests
{
    [SupportedOSPlatform("windows")]
    internal static class GpuSelfTests
    {
        public static void RunAll()
        {
            if (!OperatingSystem.IsWindows())
                return; // Salvaguarda adicional (proyecto apunta a windows pero evita CA1416 en análisis cruzado)

            TestGpuInitialization();
            TestDxgiMemory();
            TestGpuEngineCategory();
        }

        private static void TestGpuInitialization()
        {
            using var ps = new PerformanceService();
            var gpu = ps.GetGpuUsage();

            if (WindowsVersion.IsWindows7 && gpu.VramSupported)
                throw new Exception("VRAM no debe marcarse soportada en Windows 7.");

            if (!WindowsVersion.IsWindows7 && gpu.DedicatedMemoryTotal == 0)
                Debug.WriteLine("Aviso: VRAM total no disponible (driver / permisos).");
        }

        private static void TestDxgiMemory()
        {
            var dxgi = DXGIWrapper.QueryVideoMemoryInfo();
            if (dxgi.Success && dxgi.DedicatedVideoMemory == 0)
                Debug.WriteLine("Aviso: DXGI Success pero DedicatedVideoMemory == 0 (posible driver legacy).");
        }

        private static void TestGpuEngineCategory()
        {
            // Solo validar si existe la categoría
            bool exists = PerformanceCounterCategory.Exists("GPU Engine");
            using var ps = new PerformanceService();
            var gpu = ps.GetGpuUsage();

            if (exists && (gpu.GpuUsagePercent < 0 || gpu.GpuUsagePercent > 100))
                throw new Exception("Uso GPU fuera de rango 0-100.");
        }
    }
}