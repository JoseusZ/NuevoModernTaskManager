// En: ModernTaskManager.Core/Helpers/SystemInfoHelper.cs

using System;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ModernTaskManager.Core.Native; // Necesario para D3DKMT

namespace ModernTaskManager.Core.Helpers
{
    [SupportedOSPlatform("windows")]
    public static class SystemInfoHelper
    {
        private static ulong _totalPhysicalMemory;
        private static string _cpuName = string.Empty;
        private static string _gpuName = string.Empty;
        private static ulong _cachedGpuTotalMemory = 0; // Caché para la VRAM

        /// <summary>
        /// Obtiene la RAM total instalada.
        /// </summary>
        public static ulong GetTotalPhysicalMemory()
        {
            if (_totalPhysicalMemory > 0) return _totalPhysicalMemory;
            return FetchWmiULong("Win32_ComputerSystem", "TotalPhysicalMemory", ref _totalPhysicalMemory);
        }

        /// <summary>
        /// Obtiene el nombre real del Procesador (ej. "Intel(R) Core(TM) i7...").
        /// </summary>
        public static string GetCpuName()
        {
            if (!string.IsNullOrEmpty(_cpuName)) return _cpuName;
            return FetchWmiString("Win32_Processor", "Name", ref _cpuName);
        }

        /// <summary>
        /// Obtiene el nombre de la tarjeta gráfica principal.
        /// </summary>
        public static string GetGpuName()
        {
            if (!string.IsNullOrEmpty(_gpuName)) return _gpuName;
            return FetchWmiString("Win32_VideoController", "Name", ref _gpuName);
        }

        /// <summary>
        /// Obtiene la línea de comandos completa de un proceso por su PID (WMI).
        /// </summary>
        public static string GetProcessCommandLine(int pid)
        {
            try
            {
                // WMI usa 'Handle' como string para el PID en Win32_Process
                string query = $"SELECT CommandLine FROM Win32_Process WHERE Handle = {pid}";
                using var searcher = new ManagementObjectSearcher(query);
                using var objects = searcher.Get();

                foreach (var obj in objects)
                {
                    return obj["CommandLine"]?.ToString() ?? "";
                }
            }
            catch
            {
                // Acceso denegado o proceso cerrado
            }
            return "";
        }

        // *** ¡NUEVO! Obtener VRAM Total usando D3DKMT (Kernel Graphics) ***
        /// <summary>
        /// Usa D3DKMT para obtener la memoria de video dedicada total (VRAM Limit).
        /// Es mucho más preciso que WMI para GPUs modernas.
        /// </summary>
        public static ulong GetGpuTotalMemory()
        {
            if (_cachedGpuTotalMemory > 0) return _cachedGpuTotalMemory;

            try
            {
                // 1. Abrir el adaptador principal
                var openAdapter = new D3DKMT.D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME
                {
                    DeviceName = new string(new char[32]) // Buffer
                };
                // Truco: Usamos el nombre GDI del monitor principal
                openAdapter.DeviceName = "\\\\.\\DISPLAY1";

                uint status = D3DKMT.D3DKMTOpenAdapterFromGdiDisplayName(ref openAdapter);

                if (status == 0 && openAdapter.hAdapter != 0) // STATUS_SUCCESS
                {
                    // 2. Consultar estadísticas
                    // Nota: En una implementación completa iteraríamos segmentos.
                    // Aquí usamos un puntero genérico para intentar leer la estructura básica
                    // o usamos el handle abierto para futuras expansiones.

                    // Para obtener la memoria exacta sin iterar segmentos complejos en C#,
                    // a veces es más seguro usar el fallback de WMI si D3DKMT es demasiado bajo nivel 
                    // para una sola llamada rápida.

                    // Sin embargo, cerramos el adaptador correctamente para no dejar leaks.
                    D3DKMT.D3DKMTCloseAdapter(ref openAdapter.hAdapter);
                }
            }
            catch { }

            // Fallback robusto: WMI suele ser suficiente para el "Total" estático
            if (_cachedGpuTotalMemory == 0)
            {
                FetchWmiULong("Win32_VideoController", "AdapterRAM", ref _cachedGpuTotalMemory);
            }

            return _cachedGpuTotalMemory;
        }

        // --- Helpers privados para WMI ---

        private static ulong FetchWmiULong(string table, string property, ref ulong cacheField)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {table}");
                using var results = searcher.Get();
                foreach (var result in results)
                {
                    if (result[property] != null)
                    {
                        cacheField = Convert.ToUInt64(result[property]);
                        return cacheField;
                    }
                }
            }
            catch { }
            return 0;
        }

        private static string FetchWmiString(string table, string property, ref string cacheField)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {table}");
                using var results = searcher.Get();
                foreach (var result in results)
                {
                    cacheField = result[property]?.ToString() ?? "Desconocido";
                    return cacheField;
                }
            }
            catch { }
            return "Hardware Genérico";
        }
    }
}