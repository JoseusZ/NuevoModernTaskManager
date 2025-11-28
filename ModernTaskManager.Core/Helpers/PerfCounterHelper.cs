using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;

namespace ModernTaskManager.Core.Helpers
{
    public static class PerfCounterHelper
    {
        public static string ResolveCategory(params string[] names)
        {
            List<string> categories;
            try
            {
                categories = PerformanceCounterCategory.GetCategories()
                    .Select(c => c.CategoryName)
                    .ToList();
            }
            catch
            {
                // Si falla al listar todas, intentamos verificar si existe la primera opción directamente
                if (names.Length > 0 && PerformanceCounterCategory.Exists(names[0]))
                    return names[0];
                throw new Exception("Error al leer categorías de rendimiento del sistema.");
            }

            // 1. Búsqueda exacta
            foreach (var name in names)
            {
                var match = categories.FirstOrDefault(c =>
                    string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                    return match;
            }

            // 2. Búsqueda parcial (Contiene)
            foreach (var name in names)
            {
                var lower = name.ToLower();
                var match = categories.FirstOrDefault(c =>
                    c.ToLower().Contains(lower));

                if (match != null)
                    return match;
            }

            // Fallback: Si buscábamos "GPU Engine" y no está, devolvemos el nombre en inglés por si acaso
            // (A veces la lista de categorías falla pero el constructor directo funciona)
            if (names.Contains("GPU Engine") || names.Contains("Motor de GPU"))
                return "GPU Engine";

            throw new Exception(
                $"No se pudo resolver ninguna categoría: {string.Join(", ", names)}");
        }

        public static string ResolveCounter(string category, params string[] names)
        {
            try
            {
                var cat = new PerformanceCounterCategory(category);
                // Usamos la primera instancia disponible para inspeccionar los nombres de contadores
                string instance = cat.GetInstanceNames().FirstOrDefault() ?? "";

                var counters = cat.GetCounters(instance)
                    .Select(c => c.CounterName)
                    .ToList();

                // 1. Exacta
                foreach (var name in names)
                {
                    var match = counters.FirstOrDefault(c =>
                        string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

                    if (match != null) return match;
                }

                // 2. Parcial
                foreach (var name in names)
                {
                    var lower = name.ToLower();
                    var match = counters.FirstOrDefault(c =>
                        c.ToLower().Contains(lower));

                    if (match != null) return match;
                }
            }
            catch
            {
                // Si falla la inspección (permisos, etc.), devolvemos el primer nombre esperado
                // y rezamos para que funcione.
            }

            return names[0];
        }

        /// <summary>
        /// Crea un contador de CPU específicamente para "% Processor Time".
        /// Este contador SIEMPRE necesita instancia "_Total" para CPU global.
        /// </summary>
        public static PerformanceCounter CreateCpuCounter()
        {
            try
            {
                string cpuCat = ResolveCategory("Processor", "Procesador");
                string cpuCounter = ResolveCounter(cpuCat, "% Processor Time", "% de tiempo de procesador");

                // El contador de CPU SIEMPRE necesita instancia "_Total" para el total del sistema
                return new PerformanceCounter(cpuCat, cpuCounter, "_Total");
            }
            catch (Exception ex)
            {
                throw new Exception($"No se pudo crear el contador de CPU: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Crea un contador de memoria para "Available Bytes".
        /// Este contador NO necesita instancia.
        /// </summary>
        public static PerformanceCounter CreateMemoryCounter()
        {
            try
            {
                string memCat = ResolveCategory("Memory", "Memoria");
                string memCounter = ResolveCounter(memCat, "Available Bytes", "Bytes disponibles");

                return new PerformanceCounter(memCat, memCounter);
            }
            catch (Exception ex)
            {
                throw new Exception($"No se pudo crear el contador de memoria: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Crea contadores de disco.
        /// Estos contadores necesitan instancia "_Total".
        /// </summary>
        public static (PerformanceCounter activeTime, PerformanceCounter read, PerformanceCounter write) CreateDiskCounters()
        {
            try
            {
                string diskCat = ResolveCategory("PhysicalDisk", "Disco físico");

                string activeCounter = ResolveCounter(diskCat, "% Disk Time", "% de tiempo de disco");
                string readCounter = ResolveCounter(diskCat, "Disk Read Bytes/sec", "Bytes de lectura de disco/s");
                string writeCounter = ResolveCounter(diskCat, "Disk Write Bytes/sec", "Bytes de escritura en disco/s");

                var activeTime = new PerformanceCounter(diskCat, activeCounter, "_Total");
                var read = new PerformanceCounter(diskCat, readCounter, "_Total");
                var write = new PerformanceCounter(diskCat, writeCounter, "_Total");

                return (activeTime, read, write);
            }
            catch (Exception ex)
            {
                throw new Exception($"No se pudieron crear los contadores de disco: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Crea contadores de red para todas las interfaces activas.
        /// </summary>
        public static (List<PerformanceCounter> sent, List<PerformanceCounter> received) CreateNetworkCounters()
        {
            var sentCounters = new List<PerformanceCounter>();
            var receivedCounters = new List<PerformanceCounter>();

            try
            {
                string netCat = ResolveCategory("Network Interface", "Interfaz de red");
                string sentCounter = ResolveCounter(netCat, "Bytes Sent/sec", "Bytes enviados/s");
                string receivedCounter = ResolveCounter(netCat, "Bytes Received/sec", "Bytes recibidos/s");

                var instances = new PerformanceCounterCategory(netCat).GetInstanceNames();

                foreach (var instance in instances)
                {
                    // Filtrar interfaces virtuales y problemáticas
                    if (IsValidNetworkInstance(instance))
                    {
                        try
                        {
                            var sent = new PerformanceCounter(netCat, sentCounter, instance);
                            var received = new PerformanceCounter(netCat, receivedCounter, instance);

                            // Hacer una lectura inicial
                            sent.NextValue();
                            received.NextValue();

                            sentCounters.Add(sent);
                            receivedCounters.Add(received);
                        }
                        catch
                        {
                            // Ignorar interfaces problemáticas
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error creando contadores de red: {ex.Message}");
            }

            return (sentCounters, receivedCounters);
        }

        private static bool IsValidNetworkInstance(string instanceName)
        {
            var invalidPatterns = new[]
            {
                "isatap", "teredo", "loopback", "ms_ndiswan", "ppp", "vpn",
                "virtual", "pseudo", "bluetooth", "hamachi", "tap-"
            };

            return !invalidPatterns.Any(pattern =>
                instanceName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Método de compatibilidad para el código existente.
        /// </summary>
        public static PerformanceCounter CreateCounter(string category, string counterName)
        {
            // Para CPU, usar el método específico
            if (counterName.Contains("Processor Time") || counterName.Contains("tiempo de procesador"))
            {
                return CreateCpuCounter();
            }

            var cat = new PerformanceCounterCategory(category);
            var instances = cat.GetInstanceNames();

            if (instances.Length == 0)
            {
                return new PerformanceCounter(category, counterName);
            }

            var totalInstance = instances.FirstOrDefault(i =>
                i.Equals("_Total", StringComparison.OrdinalIgnoreCase));

            if (totalInstance != null)
            {
                return new PerformanceCounter(category, counterName, totalInstance);
            }

            return new PerformanceCounter(category, counterName, instances[0]);
        }

        /// <summary>
        /// Crea un contador para una instancia específica.
        /// </summary>
        public static PerformanceCounter? CreateInstanceCounter(string category, string counter, string instance)
        {
            try
            {
                var cat = new PerformanceCounterCategory(category);

                if (!cat.GetInstanceNames().Contains(instance))
                    return null;

                var counters = cat.GetCounters(instance);
                // Corrección: Búsqueda flexible también aquí
                if (!counters.Any(c => string.Equals(c.CounterName, counter, StringComparison.OrdinalIgnoreCase)))
                    return null;

                return new PerformanceCounter(category, counter, instance);
            }
            catch
            {
                return null;
            }
        }
    }
}