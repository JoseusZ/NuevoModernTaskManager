using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using ModernTaskManager.Core.Helpers;
using ModernTaskManager.Core.Models;
using ModernTaskManager.Core.Gpu;
using ModernTaskManager.Core.Native; // DXGIWrapper + D3DKMT

namespace ModernTaskManager.Core.Services.GPU
{
    public class ModernGpuProvider : IGpuUsageProvider
    {
        public string ProviderName => "Modern (WDDM + D3DKMT)";
        public bool IsSupported { get; private set; } = false;

        // Motores agregados y por-proceso (agregamos Copy, VideoDecode, VideoEncode)
        private readonly List<PerformanceCounter> _gpuAllEnginesCounters = new();  // engtype_* (agregado) o pid_*_engtype_* (per proceso)
        private readonly List<PerformanceCounter> _gpu3dAggregated = new();        // 3D
        private readonly List<PerformanceCounter> _gpuComputeAggregated = new();   // Compute
        private readonly List<PerformanceCounter> _gpuCopyAggregated = new();      // Copy
        private readonly List<PerformanceCounter> _gpuVdecAggregated = new();      // VideoDecode
        private readonly List<PerformanceCounter> _gpuVencAggregated = new();      // VideoEncode

        // Memoria
        private readonly List<PerformanceCounter> _gpuVramCounters = new();
        private readonly List<PerformanceCounter> _gpuSharedCounters = new();

        private string? _gpuCategoryName;
        private string? _gpuMemCategoryName;
        private int _refreshTick = 0;
        private ulong _totalVramCached = 0;

        private string _validEngineCounterName = "";
        private string _validVramCounterName = "";
        private string _validSharedCounterName = "";

        private readonly Stopwatch _samplingWatch = new();
        private readonly int _minSamplingMs = 250;

        // Suavizado sobre total (queremos estabilidad similar a Task Manager)
        private double _emaTotal = 0;
        private const double _emaAlpha = 0.35;

        // Autocuración cuando el total queda en 0
        private int _zeroStreak = 0;
        private const int ZeroRebuildThreshold = 5;
        private bool _usingAggregatedOnly = true;

        public void Initialize()
        {
            try
            {
                _gpuCategoryName = TryResolveCategory("GPU Engine", "Motor de GPU");
                if (string.IsNullOrEmpty(_gpuCategoryName))
                {
                    IsSupported = false;
                    return;
                }

                _gpuMemCategoryName = TryResolveCategory("GPU Adapter Memory", "Memoria de adaptador de GPU");
                IsSupported = true;

                RefreshEngineCounters(forceRebuild: true, fallbackToPerProcess: false);
                SetupMemoryCounters();

                try { _totalVramCached = SystemInfoHelper.GetGpuTotalMemory(); } catch { _totalVramCached = 0; }

                if (_gpuAllEnginesCounters.Count == 0 && _gpuVramCounters.Count == 0 && _gpuSharedCounters.Count == 0)
                {
                    IsSupported = false;
                    Dispose();
                }

                _samplingWatch.Restart();
            }
            catch
            {
                IsSupported = false;
                Dispose();
            }
        }

        public GpuDetailInfo GetStaticInfo()
        {
            var info = new GpuDetailInfo();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController");
                foreach (var mo in searcher.Get())
                {
                    info.Name = mo["Name"]?.ToString() ?? "GPU Genérica";
                    info.DriverVersion = mo["DriverVersion"]?.ToString() ?? "";
                    info.PnpDeviceId = mo["PNPDeviceID"]?.ToString() ?? "";

                    string dateStr = mo["DriverDate"]?.ToString() ?? "";
                    if (dateStr.Length >= 8 && DateTime.TryParseExact(dateStr.Substring(0, 8), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt))
                        info.DriverDate = dt;
                    break;
                }

                info.TotalDedicatedBytes = _totalVramCached > 0 ? _totalVramCached : SystemInfoHelper.GetGpuTotalMemory();
                ulong totalPhys = 0;
                try { totalPhys = SystemInfoHelper.GetTotalPhysicalMemory(); } catch { }
                info.TotalSharedBytes = totalPhys > 0 ? totalPhys / 2 : 0;
            }
            catch { }
            return info;
        }

        public GpuAdapterDynamicInfo GetUsage()
        {
            if (!IsSupported) return new GpuAdapterDynamicInfo();

            if (_samplingWatch.IsRunning && _samplingWatch.ElapsedMilliseconds < _minSamplingMs)
            {
                return new GpuAdapterDynamicInfo
                {
                    GlobalUsagePercent = Math.Min(100.0, _emaTotal),
                    DedicatedMemoryUsed = 0,
                    SharedMemoryUsed = 0
                };
            }
            _samplingWatch.Restart();

            if (++_refreshTick > 30)
            {
                RefreshEngineCounters();
                _refreshTick = 0;
            }

            // Lecturas por motor
            double threeD = ReadSum(_gpu3dAggregated);
            double compute = ReadSum(_gpuComputeAggregated);
            double copy = ReadSum(_gpuCopyAggregated);
            double vdec = ReadSum(_gpuVdecAggregated);
            double venc = ReadSum(_gpuVencAggregated);

            // Suma “total” (puede subestimar o sobreestimar en algunos drivers)
            double totalAll = ReadSum(_gpuAllEnginesCounters);

            // Autocuración si totalAll parece muerto
            if (totalAll <= 0.1 && threeD <= 0.1 && compute <= 0.1 && copy <= 0.1 && vdec <= 0.1 && venc <= 0.1)
            {
                _zeroStreak++;
                if (_zeroStreak >= ZeroRebuildThreshold)
                {
                    RefreshEngineCounters(forceRebuild: true, fallbackToPerProcess: true);
                    _zeroStreak = 0;
                }
            }
            else
            {
                _zeroStreak = 0;
            }

            // Aproximación al “Uso” de Windows: tomar el motor más ocupado
            double enginesMax = Math.Max(threeD, Math.Max(compute, Math.Max(copy, Math.Max(vdec, venc))));

            // Para evitar infravalorar en algunos drivers, usar el máximo entre suma y máximo por motor (luego clamped)
            double rawOverall = Math.Max(enginesMax, totalAll);
            rawOverall = Math.Min(100.0, rawOverall);

            // Suavizado EMA para una gráfica más estable
            _emaTotal = (_emaTotal == 0) ? rawOverall : (_emaAlpha * rawOverall + (1 - _emaAlpha) * _emaTotal);

            // Memoria
            ulong vram = ReadSumUlong(_gpuVramCounters);
            ulong shared = ReadSumUlong(_gpuSharedCounters);

            return new GpuAdapterDynamicInfo
            {
                GlobalUsagePercent = Math.Min(100.0, _emaTotal),
                ThreeDPercent = Math.Min(100.0, threeD),
                ComputePercent = Math.Min(100.0, compute),
                DedicatedMemoryUsed = vram,
                SharedMemoryUsed = shared
            };
        }

        private void RefreshEngineCounters(bool forceRebuild = false, bool fallbackToPerProcess = false)
        {
            if (string.IsNullOrEmpty(_gpuCategoryName)) return;

            if (forceRebuild)
            {
                DisposeList(_gpuAllEnginesCounters);
                DisposeList(_gpu3dAggregated);
                DisposeList(_gpuComputeAggregated);
                DisposeList(_gpuCopyAggregated);
                DisposeList(_gpuVdecAggregated);
                DisposeList(_gpuVencAggregated);
                _validEngineCounterName = "";
            }

            if (_gpuAllEnginesCounters.Count > 0) return;

            try
            {
                var cat = new PerformanceCounterCategory(_gpuCategoryName);
                var instances = cat.GetInstanceNames();

                if (string.IsNullOrEmpty(_validEngineCounterName))
                {
                    _validEngineCounterName = TryResolveCounter(_gpuCategoryName,
                        "Utilization Percentage", "Porcentaje de utilización");
                }
                if (string.IsNullOrEmpty(_validEngineCounterName)) return;

                // 1) Preferir instancias agregadas si están disponibles
                var aggregated = instances.Where(i => i.StartsWith("engtype_", StringComparison.OrdinalIgnoreCase)).ToArray();

                if (aggregated.Length > 0 && !fallbackToPerProcess)
                {
                    _usingAggregatedOnly = true;

                    foreach (var inst in aggregated)
                        TryAddCounter(_gpuAllEnginesCounters, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in aggregated.Where(i => string.Equals(i, "engtype_3D", StringComparison.OrdinalIgnoreCase)))
                        TryAddCounter(_gpu3dAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in aggregated.Where(i => i.IndexOf("Compute", StringComparison.OrdinalIgnoreCase) >= 0).Take(8))
                        TryAddCounter(_gpuComputeAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in aggregated.Where(i => i.IndexOf("Copy", StringComparison.OrdinalIgnoreCase) >= 0).Take(4))
                        TryAddCounter(_gpuCopyAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in aggregated.Where(i => i.IndexOf("VideoDecode", StringComparison.OrdinalIgnoreCase) >= 0).Take(4))
                        TryAddCounter(_gpuVdecAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in aggregated.Where(i => i.IndexOf("VideoEncode", StringComparison.OrdinalIgnoreCase) >= 0).Take(4))
                        TryAddCounter(_gpuVencAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);
                }
                else
                {
                    // 2) Fallback: instancias por proceso (pid_..._engtype_*)
                    _usingAggregatedOnly = false;

                    var perProcessAll = instances.Where(i => i.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase) >= 0)
                                                .Take(64)
                                                .ToArray();

                    foreach (var inst in perProcessAll)
                        TryAddCounter(_gpuAllEnginesCounters, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in perProcessAll.Where(i => i.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) >= 0).Take(32))
                        TryAddCounter(_gpu3dAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in perProcessAll.Where(i => i.IndexOf("Compute", StringComparison.OrdinalIgnoreCase) >= 0).Take(16))
                        TryAddCounter(_gpuComputeAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in perProcessAll.Where(i => i.IndexOf("Copy", StringComparison.OrdinalIgnoreCase) >= 0).Take(16))
                        TryAddCounter(_gpuCopyAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in perProcessAll.Where(i => i.IndexOf("VideoDecode", StringComparison.OrdinalIgnoreCase) >= 0).Take(16))
                        TryAddCounter(_gpuVdecAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in perProcessAll.Where(i => i.IndexOf("VideoEncode", StringComparison.OrdinalIgnoreCase) >= 0).Take(16))
                        TryAddCounter(_gpuVencAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);
                }
            }
            catch
            {
                // categoría inaccesible o dañada
            }
        }

        private void SetupMemoryCounters()
        {
            if (string.IsNullOrEmpty(_gpuMemCategoryName)) return;

            DisposeList(_gpuVramCounters);
            DisposeList(_gpuSharedCounters);

            try
            {
                var cat = new PerformanceCounterCategory(_gpuMemCategoryName);
                var instances = cat.GetInstanceNames();
                if (instances.Length == 0) return;

                if (string.IsNullOrEmpty(_validVramCounterName))
                {
                    _validVramCounterName = TryResolveCounter(_gpuMemCategoryName,
                        "Dedicated Usage", "Bytes dedicados", "Dedicated Memory");
                }
                if (string.IsNullOrEmpty(_validSharedCounterName))
                {
                    _validSharedCounterName = TryResolveCounter(_gpuMemCategoryName,
                        "Shared Usage", "Bytes compartidos", "Shared Memory");
                }

                if (string.IsNullOrEmpty(_validVramCounterName) || string.IsNullOrEmpty(_validSharedCounterName)) return;

                foreach (var inst in instances.Take(8))
                {
                    TryAddCounter(_gpuVramCounters, _gpuMemCategoryName, _validVramCounterName, inst, prime: true);
                    TryAddCounter(_gpuSharedCounters, _gpuMemCategoryName, _validSharedCounterName, inst, prime: true);
                }
            }
            catch { }
        }

        private static double ReadSum(List<PerformanceCounter> list)
        {
            double total = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                try
                {
                    var v = c.NextValue();
                    if (!double.IsNaN(v) && v >= 0) total += v;
                }
                catch
                {
                    TryRemoveCounter(list, i--);
                }
            }
            return total;
        }

        private static ulong ReadSumUlong(List<PerformanceCounter> list)
        {
            ulong total = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                try
                {
                    var v = c.NextValue();
                    if (!double.IsNaN(v) && v >= 0) total += (ulong)v;
                }
                catch
                {
                    TryRemoveCounter(list, i--);
                }
            }
            return total;
        }

        private static void TryAddCounter(List<PerformanceCounter> list, string category, string counter, string instance, bool prime)
        {
            try
            {
                var pc = new PerformanceCounter(category, counter, instance, true);
                if (prime) { try { pc.NextValue(); } catch { } }
                list.Add(pc);
            }
            catch { }
        }

        private static void DisposeList(List<PerformanceCounter> list)
        {
            foreach (var c in list) { try { c.Dispose(); } catch { } }
            list.Clear();
        }

        private static void TryRemoveCounter(List<PerformanceCounter> list, int index)
        {
            try { list[index].Dispose(); } catch { }
            list.RemoveAt(index);
        }

        private static string TryResolveCategory(params string[] candidates)
        {
            try { return PerfCounterHelper.ResolveCategory(candidates); }
            catch
            {
                foreach (var name in candidates)
                {
                    try { if (PerformanceCounterCategory.Exists(name)) return name; }
                    catch { }
                }
                return string.Empty;
            }
        }

        private static string TryResolveCounter(string category, params string[] candidates)
        {
            try { return PerfCounterHelper.ResolveCounter(category, candidates); }
            catch
            {
                try
                {
                    var cat = new PerformanceCounterCategory(category);
                    var instance = cat.GetInstanceNames().FirstOrDefault();
                    if (string.IsNullOrEmpty(instance)) return candidates[0];

                    foreach (var name in candidates)
                    {
                        try
                        {
                            using var pc = new PerformanceCounter(category, name, instance, true);
                            pc.NextValue();
                            return name;
                        }
                        catch { }
                    }
                }
                catch { }
                return "";
            }
        }

        public void Dispose()
        {
            DisposeList(_gpuAllEnginesCounters);
            DisposeList(_gpu3dAggregated);
            DisposeList(_gpuComputeAggregated);
            DisposeList(_gpuCopyAggregated);
            DisposeList(_gpuVdecAggregated);
            DisposeList(_gpuVencAggregated);
            DisposeList(_gpuVramCounters);
            DisposeList(_gpuSharedCounters);
        }
    }
}