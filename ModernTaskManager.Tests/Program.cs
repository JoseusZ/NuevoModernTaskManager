using System;
using System.Linq;
using System.Threading;
using ModernTaskManager.Core.Models;
using ModernTaskManager.Core.Services;

namespace ModernTaskManager.Tests
{
    internal class Program
    {
        static void Main(string[] args)
        {
            // Intentar hacer la consola un poco más ancha para ver los títulos
            try { Console.WindowWidth = 120; } catch { }

            Console.CursorVisible = false;
            Console.WriteLine("Iniciando Motor de Monitorización... (Calentando 1 seg)");

            try
            {
                using (var perfService = new PerformanceService())
                using (var monitor = new SystemMonitor())
                {
                    // Calentamiento inicial
                    monitor.Update();
                    Thread.Sleep(1000);
                    Console.Clear();

                    while (!Console.KeyAvailable)
                    {
                        // --- 1. ACTUALIZAR DATOS ---
                        double globalCpu = perfService.GetGlobalCpuUsage();
                        MemoryUsageInfo memInfo = perfService.GetMemoryUsage();
                        DiskUsageInfo diskInfo = perfService.GetDiskUsage();
                        NetworkUsageInfo netInfo = perfService.GetNetworkUsage();

                        // ¡NUEVO! Obtener datos de GPU
                        GpuUsageInfo gpuInfo = perfService.GetGpuUsage();

                        monitor.Update(); // Recalcula CPU, Disco y Agrupación

                        // --- 2. DIBUJAR CABECERA (Rendimiento Global) ---
                        Console.SetCursorPosition(0, 0);
                        Console.WriteLine("--- MODERN TASK MANAGER (CORE BACKEND TEST) --- [Presiona tecla para salir]");
                        Console.WriteLine(new string('=', 118));

                        // CPU
                        DrawBar("CPU Total", globalCpu, 100.0, "");

                        // RAM
                        string ramText = $"{FormatBytes(memInfo.UsedPhysicalBytes)} / {FormatBytes(memInfo.TotalPhysicalBytes)} (Carga: {memInfo.UsedPercentage:F0}%)";
                        DrawBar("RAM Física", memInfo.UsedPercentage, 100.0, ramText);
                        Console.WriteLine($"      -> Confirmada: {FormatBytes(memInfo.CommittedBytes)} / {FormatBytes(memInfo.CommitLimit)}");

                        // Disco
                        DrawBar("Disco Act.", diskInfo.ActiveTimePercent, 100.0,
                            $"L: {FormatBytes(diskInfo.ReadBytesPerSec)}/s | E: {FormatBytes(diskInfo.WriteBytesPerSec)}/s");

                        // Red
                        double maxNet = netInfo.BandwidthBytesPerSec > 0 ? netInfo.BandwidthBytesPerSec : (100 * 1024 * 1024 / 8);
                        double netPercent = (netInfo.TotalBytesPerSec / maxNet) * 100.0;
                        DrawBar("Red Total", Math.Min(100, netPercent), 100.0,
                            $"Sub: {FormatBytes(netInfo.BytesSentPerSec)}/s | Baj: {FormatBytes(netInfo.BytesReceivedPerSec)}/s | Cap: {FormatBytes(maxNet)}/s");

                        // ¡NUEVO! GPU
                        string vramText = $"{FormatBytes(gpuInfo.DedicatedMemoryUsed)} / {FormatBytes(gpuInfo.DedicatedMemoryTotal)}";
                        // Calculamos % de VRAM solo si tenemos el total
                        double vramPercent = (gpuInfo.DedicatedMemoryTotal > 0) ? (double)gpuInfo.DedicatedMemoryUsed / gpuInfo.DedicatedMemoryTotal * 100.0 : 0;

                        DrawBar("GPU 3D", gpuInfo.GpuUsagePercent, 100.0, $"VRAM: {vramText} ({vramPercent:F0}%)");

                        Console.WriteLine(new string('-', 118));

                        // --- 3. DIBUJAR LISTA ---
                        Console.WriteLine("{0,-6} {1,-25} {2,-6} {3,-12} {4,-4} {5}",
                            "PID", "Nombre", "CPU%", "Disk/s", "Tipo", "Detalle (Título / Usuario)");
                        Console.WriteLine(new string('-', 118));

                        int count = 0;
                        var topProcesses = monitor.Processes
                            .OrderByDescending(p => p.CpuUsage)
                            .Take(20)
                            .ToList();

                        foreach (var p in topProcesses)
                        {
                            string diskSpeed = FormatBytes(p.DiskReadSpeed + p.DiskWriteSpeed) + "/s";
                            string category = p.Category == ProcessCategory.Application ? "APP" : "BG";

                            string detail = p.Category == ProcessCategory.Application
                                ? (string.IsNullOrEmpty(p.MainWindowTitle) ? p.Name : p.MainWindowTitle)
                                : p.Username;

                            if (p.Category == ProcessCategory.Application) Console.ForegroundColor = ConsoleColor.Cyan;
                            else Console.ForegroundColor = ConsoleColor.Gray;

                            string line = string.Format("{0,-6} {1,-25} {2,-6:F1} {3,-12} {4,-4} {5}",
                                p.Pid,
                                Truncate(p.Name, 24),
                                p.CpuUsage,
                                diskSpeed,
                                category,
                                Truncate(detail, 55));

                            Console.WriteLine(line.PadRight(118));
                            count++;
                        }

                        Console.ResetColor();
                        for (int i = count; i < 20; i++) Console.WriteLine(new string(' ', 118));

                        Thread.Sleep(1000);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR FATAL: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.ResetColor();
                Console.ReadKey();
            }
        }

        // --- HELPERS VISUALES ---

        private static string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= maxChars ? value : value.Substring(0, maxChars - 2) + "..";
        }

        private static void DrawBar(string title, double value, double maxValue, string label = "")
        {
            Console.Write($"{title,-12}: [");
            int barWidth = 25;
            double cappedValue = Math.Max(0, Math.Min(value, maxValue));
            double percentage = (maxValue == 0) ? 0 : (cappedValue / maxValue);
            int progress = (int)(percentage * barWidth);

            Console.ForegroundColor = ConsoleColor.Green;
            if (percentage > 0.8) Console.ForegroundColor = ConsoleColor.Red;
            else if (percentage > 0.5) Console.ForegroundColor = ConsoleColor.Yellow;

            Console.Write(new string('|', progress));
            Console.ResetColor();
            Console.Write(new string('.', barWidth - progress));

            Console.Write($"] {value,5:F1}%  {label}");
            Console.WriteLine();
        }

        private static string FormatBytes(double bytes)
        {
            if (bytes >= 1024 * 1024 * 1024) return $"{bytes / (1024 * 1024 * 1024):F1} GB";
            if (bytes >= 1024 * 1024) return $"{bytes / (1024 * 1024):F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024:F0} KB";
            return $"{bytes:F0} B";
        }
    }
}