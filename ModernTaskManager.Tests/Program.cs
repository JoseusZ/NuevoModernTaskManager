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
            try { Console.WindowWidth = 150; } catch { }
            Console.CursorVisible = false;
            Console.WriteLine("Iniciando Motor... (Calentando)");

            try
            {
                // Usamos GpuService para la GPU y PerformanceService para lo demás
                using (var perfService = new PerformanceService())
                using (var gpuService = new GpuService())
                using (var monitor = new SystemMonitor())
                {
                    monitor.Update();
                    Thread.Sleep(1000);
                    Console.Clear();

                    while (!Console.KeyAvailable)
                    {
                        // 1. Obtener Datos
                        double globalCpu = perfService.GetGlobalCpuUsage();
                        MemoryUsageInfo memInfo = perfService.GetMemoryUsage();
                        DiskUsageInfo diskInfo = perfService.GetDiskUsage();
                        NetworkUsageInfo netInfo = perfService.GetNetworkUsage();

                        // 2. Obtener GPU (Adaptado al nuevo modelo)
                        GpuAdapterDynamicInfo gpuInfo = gpuService.GetGpuUsage();

                        monitor.Update();

                        // --- DIBUJAR ---
                        Console.SetCursorPosition(0, 0);
                        Console.WriteLine($"--- TASK MANAGER CORE (GPU Provider: {gpuService.GetProviderName()}) ---");
                        Console.WriteLine(new string('=', 148));

                        DrawBar("CPU Total", globalCpu, 100.0, "");

                        string ramText = $"{FormatBytes(memInfo.UsedPhysicalBytes)} / {FormatBytes(memInfo.TotalPhysicalBytes)}";
                        DrawBar("RAM Física", memInfo.UsedPercentage, 100.0, ramText);

                        DrawBar("Disco Act.", diskInfo.ActiveTimePercent, 100.0,
                            $"L: {FormatBytes(diskInfo.ReadBytesPerSec)}/s | E: {FormatBytes(diskInfo.WriteBytesPerSec)}/s");

                        double maxNet = netInfo.BandwidthBytesPerSec > 0 ? netInfo.BandwidthBytesPerSec : (100 * 1024 * 1024 / 8);
                        double netPercent = (netInfo.TotalBytesPerSec / maxNet) * 100.0;
                        DrawBar("Red Total", Math.Min(100, netPercent), 100.0,
                            $"Tot: {FormatBytes(netInfo.TotalBytesPerSec)}/s");

                        // GPU (Mostrar uso global)
                        ulong totalVram = gpuService.StaticInfo.TotalDedicatedBytes;
                        string vramText = $"{FormatBytes(gpuInfo.DedicatedMemoryUsed)} / {FormatBytes(totalVram)}";
                        double vramPercent = (totalVram > 0) ? (double)gpuInfo.DedicatedMemoryUsed / totalVram * 100.0 : 0;

                        DrawBar("GPU Uso", gpuInfo.GlobalUsagePercent, 100.0,
                            $"VRAM: {vramText} ({vramPercent:F0}%) [{gpuService.StaticInfo.Name}]");

                        Console.WriteLine(new string('-', 148));
                        Console.WriteLine("{0,-6} {1,-25} {2,-5} {3,-6} {4,-12} {5,-4} {6}", "PID", "Nombre", "Arq", "CPU%", "Disk/s", "Tipo", "Detalle");
                        Console.WriteLine(new string('-', 148));

                        var topProcesses = monitor.Processes.OrderByDescending(p => p.CpuUsage).Take(15);
                        int count = 0;
                        foreach (var p in topProcesses)
                        {
                            string diskSpeed = FormatBytes(p.DiskReadSpeed + p.DiskWriteSpeed) + "/s";
                            string category = p.Category == ProcessCategory.Application ? "APP" : "BG";

                            // Check visual: ¿Tiene un handle de icono válido?
                            string iconCheck = (p.IconHandle != IntPtr.Zero) ? "[ICON]" : "[    ]";

                            string detail = p.Category == ProcessCategory.Application
                                ? (string.IsNullOrEmpty(p.MainWindowTitle) ? p.Name : p.MainWindowTitle)
                                : p.Username;

                            if (p.Category == ProcessCategory.Application) Console.ForegroundColor = ConsoleColor.Cyan;
                            else Console.ForegroundColor = ConsoleColor.Gray;

                            // Agregamos la columna de icono en la salida
                            string line = string.Format("{0,-6} {1,-20} {2,-6} {3,-5} {4,-5:F1} {5,-10} {6,-4} {7}",
                                p.Pid,
                                Truncate(p.Name, 18),
                                iconCheck, // Columna Icono
                                p.Architecture,
                                p.CpuUsage,
                                diskSpeed,
                                category,
                                Truncate(detail, 50));

                            Console.WriteLine(line.PadRight(148));
                            count++;
                        }
                        for (int i = count; i < 15; i++) Console.WriteLine(new string(' ', 148));

                        Thread.Sleep(1000);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        private static string Truncate(string value, int maxChars) =>
            string.IsNullOrEmpty(value) ? "" : (value.Length <= maxChars ? value : value.Substring(0, maxChars - 2) + "..");

        private static void DrawBar(string title, double value, double maxValue, string label = "")
        {
            Console.Write($"{title,-12}: [");
            int barWidth = 25;
            double cappedValue = Math.Max(0, Math.Min(value, maxValue));
            double percentage = (maxValue == 0) ? 0 : (cappedValue / maxValue);
            int progress = (int)(percentage * barWidth);

            Console.ForegroundColor = ConsoleColor.Green;
            if (percentage > 0.8) Console.ForegroundColor = ConsoleColor.Red;
            Console.Write(new string('|', progress));
            Console.ResetColor();
            Console.Write(new string('.', barWidth - progress));
            Console.Write($"] {value,5:F1}%  {label}\n");
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