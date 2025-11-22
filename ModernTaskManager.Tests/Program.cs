using System;
using System.Linq;
using System.Threading;
using ModernTaskManager.Core.Helpers;
using ModernTaskManager.Core.Models;
using ModernTaskManager.Core.Services;

namespace ModernTaskManager.Tests
{
    internal class Program
    {
        static void Main(string[] args)
        {
            // Intentar hacer la consola un poco más ancha para ver los títulos largos
            try { Console.WindowWidth = 150; } catch { }

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

                    // Pruebas de autodiagnóstico para GPU
                    GpuSelfTests.RunAll();

                    while (!Console.KeyAvailable)
                    {
                        // --- 1. ACTUALIZAR DATOS ---
                        double globalCpu = perfService.GetGlobalCpuUsage();
                        MemoryUsageInfo memInfo = perfService.GetMemoryUsage();
                        DiskUsageInfo diskInfo = perfService.GetDiskUsage();
                        NetworkUsageInfo netInfo = perfService.GetNetworkUsage();
                        GpuUsageInfo gpuInfo = perfService.GetGpuUsage(); // ¡NUEVO!

                        monitor.Update(); // Recalcula CPU, Disco y Agrupación

                        // --- 2. DIBUJAR CABECERA (Rendimiento Global) ---
                        Console.SetCursorPosition(0, 0);
                        Console.WriteLine("--- MODERN TASK MANAGER (CORE BACKEND TEST) --- [Presiona tecla para salir]");
                        Console.WriteLine(new string('=', 148));

                        // CPU
                        DrawBar("CPU Total", globalCpu, 100.0, "");

                        // RAM (Detallada)
                        string ramText = $"{ByteFormatHelper.Format(memInfo.UsedPhysicalBytes)} / {ByteFormatHelper.Format(memInfo.TotalPhysicalBytes)} (Uso: {memInfo.UsedPercentage:F0}%)";
                        DrawBar("RAM Física", memInfo.UsedPercentage, 100.0, ramText);
                        Console.WriteLine($"      -> Confirmada: {ByteFormatHelper.Format(memInfo.CommittedBytes)} / {ByteFormatHelper.Format(memInfo.CommitLimit)}");

                        // Disco
                        DrawBar("Disco Act.", diskInfo.ActiveTimePercent, 100.0,
                            $"L: {ByteFormatHelper.Format((ulong)diskInfo.ReadBytesPerSec)}/s | E: {ByteFormatHelper.Format((ulong)diskInfo.WriteBytesPerSec)}/s");

                        // Red
                        double maxNet = netInfo.BandwidthBytesPerSec > 0 ? netInfo.BandwidthBytesPerSec : (100 * 1024 * 1024 / 8); // ~100 Mbps fallback
                        double netPercent = (maxNet > 0) ? (netInfo.TotalBytesPerSec / maxNet) * 100.0 : 0;
                        DrawBar("Red Total", Math.Min(100, netPercent), 100.0,
                            $"Sub: {ByteFormatHelper.Format(netInfo.BytesSentPerSec)}/s | Baj: {ByteFormatHelper.Format(netInfo.BytesReceivedPerSec)}/s | Cap: {ByteFormatHelper.Format(maxNet)}/s");

                        // GPU (¡NUEVO!)
                        string vramText = $"{ByteFormatHelper.Format(gpuInfo.DedicatedMemoryUsed)} / {ByteFormatHelper.Format(gpuInfo.DedicatedMemoryTotal)}";
                        double vramPercent = (gpuInfo.DedicatedMemoryTotal > 0) ? (double)gpuInfo.DedicatedMemoryUsed / gpuInfo.DedicatedMemoryTotal * 100.0 : 0;
                        DrawBar("GPU 3D", gpuInfo.GpuUsagePercent, 100.0, $"VRAM: {vramText} ({vramPercent:F0}%)");

                        Console.WriteLine(new string('-', 148));

                        // --- 3. DIBUJAR LISTA DE PROCESOS ---
                        // Nuevas columnas: Arq (x64/x86) y CmdLine (truncada)
                        Console.WriteLine("{0,-6} {1,-25} {2,-5} {3,-6} {4,-12} {5,-4} {6}",
                            "PID", "Nombre", "Arq", "CPU%", "Disk/s", "Tipo", "Detalle (Título / CmdLine / Usuario)");
                        Console.WriteLine(new string('-', 148));

                        int count = 0;
                        var topProcesses = monitor.Processes
                            .OrderByDescending(p => p.CpuUsage)
                            .Take(20)
                            .ToList();

                        foreach (var p in topProcesses)
                        {
                            string diskSpeed = ByteFormatHelper.Format((ulong)(p.DiskReadSpeed + p.DiskWriteSpeed)) + "/s";
                            string category = p.Category == ProcessCategory.Application ? "APP" : "BG";

                            // Lógica de visualización de detalle:
                            // 1. Si es APP, muestra el Título de Ventana.
                            // 2. Si es BG, intenta mostrar la Línea de Comandos (si existe).
                            // 3. Si no, muestra el Usuario.
                            string detail = p.Username;
                            if (p.Category == ProcessCategory.Application && !string.IsNullOrEmpty(p.MainWindowTitle))
                                detail = $"[Ventana] {p.MainWindowTitle}";
                            else if (!string.IsNullOrEmpty(p.CommandLine))
                                detail = $"[Cmd] {p.CommandLine}";

                            if (p.Category == ProcessCategory.Application) Console.ForegroundColor = ConsoleColor.Cyan;
                            else Console.ForegroundColor = ConsoleColor.Gray;

                            string line = string.Format("{0,-6} {1,-25} {2,-5} {3,-6:F1} {4,-12} {5,-4} {6}",
                                p.Pid,
                                Truncate(p.Name, 24),
                                p.Architecture, // ¡Nueva columna!
                                p.CpuUsage,
                                diskSpeed,
                                category,
                                Truncate(detail, 80)); // Más espacio para ver la línea de comandos

                            Console.WriteLine(line.PadRight(148));
                            count++;
                        }

                        Console.ResetColor();

                        for (int i = count; i < 20; i++) Console.WriteLine(new string(' ', 148));

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
    }
}