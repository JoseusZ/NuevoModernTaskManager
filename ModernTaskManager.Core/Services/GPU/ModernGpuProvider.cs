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
        private double _emaTotal = double.NaN; // sentinel para evitar sesgo inicial
        private const double _emaAlpha = 0.45; // reacción algo más rápida

        // Autocuración cuando el total queda en 0
        private int _zeroStreak = 0;
        private const int ZeroRebuildThreshold = 5;
        private bool _usingAggregatedOnly = true;

        // Pequeña caché DXGI para evitar COM allocations por lectura
        private DXGIWrapper.DXGIResult? _dxgiCache;
        private long _dxgiCacheTick;
        private const int DxgiCacheTtlMs = 1000; // refrescar como Task Manager ~1s

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
                var ema = double.IsNaN(_emaTotal) ? 0.0 : _emaTotal;
                return new GpuAdapterDynamicInfo
                {
                    GlobalUsagePercent = Math.Min(100.0, ema),
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
            bool allFlat = totalAll <= 0.1 && threeD <= 0.1 && compute <= 0.1 && copy <= 0.1 && vdec <= 0.1 && venc <= 0.1;
            if (allFlat)
            {
                _zeroStreak++;
                if (_zeroStreak >= ZeroRebuildThreshold)
                {
                    // Forzar fallback per-process y permitir más instancias temporalmente
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

            // Fallback adicional: D3DKMT por delta de running times de nodos
            if (NodeBusySampler.TrySampleTotalUsagePercent(out var d3dBusyPercent))
            {
                if (allFlat && d3dBusyPercent > rawOverall)
                {
                    Debug.WriteLine($"[GPU] D3DKMT fallback usado. raw={rawOverall:F2} d3dBusy={d3dBusyPercent:F2}");
                }
                rawOverall = Math.Max(rawOverall, d3dBusyPercent);
            }

            rawOverall = Math.Min(100.0, Math.Max(0.0, rawOverall));

            // Suavizado EMA para una gráfica más estable (inicialización con primera muestra real)
            _emaTotal = double.IsNaN(_emaTotal)
                ? rawOverall
                : (_emaAlpha * rawOverall + (1 - _emaAlpha) * _emaTotal);

            // Memoria: usar caché DXGI para minimizar COM allocations
            ulong vram = 0;
            var nowTicks = Stopwatch.GetTimestamp();
            var msSince = (_dxgiCacheTick == 0) ? long.MaxValue : (long)((nowTicks - _dxgiCacheTick) * 1000.0 / Stopwatch.Frequency);
            if (_dxgiCache == null || msSince > DxgiCacheTtlMs)
            {
                try
                {
                    var dxgi = DXGIWrapper.QueryVideoMemoryInfo();
                    if (dxgi != null && dxgi.Success)
                    {
                        _dxgiCache = dxgi;
                        _dxgiCacheTick = nowTicks;
                    }
                }
                catch { }
            }
            if (_dxgiCache != null && _dxgiCache.CurrentUsage > 0)
                vram = _dxgiCache.CurrentUsage;
            if (vram == 0)
                vram = ReadSumUlong(_gpuVramCounters);

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

                    // Limitar el número de contadores para reducir memoria (8-12 agregados)
                    foreach (var inst in aggregated.Take(12))
                        TryAddCounter(_gpuAllEnginesCounters, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in aggregated.Where(i => string.Equals(i, "engtype_3D", StringComparison.OrdinalIgnoreCase)).Take(1))
                        TryAddCounter(_gpu3dAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in aggregated.Where(i => i.IndexOf("Compute", StringComparison.OrdinalIgnoreCase) >= 0).Take(4))
                        TryAddCounter(_gpuComputeAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in aggregated.Where(i => i.IndexOf("Copy", StringComparison.OrdinalIgnoreCase) >= 0).Take(2))
                        TryAddCounter(_gpuCopyAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in aggregated.Where(i => i.IndexOf("VideoDecode", StringComparison.OrdinalIgnoreCase) >= 0).Take(2))
                        TryAddCounter(_gpuVdecAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in aggregated.Where(i => i.IndexOf("VideoEncode", StringComparison.OrdinalIgnoreCase) >= 0).Take(2))
                        TryAddCounter(_gpuVencAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);
                }
                else
                {
                    // 2) Fallback: instancias por proceso (pid_..._engtype_*)
                    _usingAggregatedOnly = false;

                    var perProcessAll = instances.Where(i => i.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase) >= 0)
                                                .Take(24) // 16-24 para menor consumo pero mejor cobertura
                                                .ToArray();

                    foreach (var inst in perProcessAll)
                        TryAddCounter(_gpuAllEnginesCounters, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in perProcessAll.Where(i => i.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) >= 0).Take(8))
                        TryAddCounter(_gpu3dAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in perProcessAll.Where(i => i.IndexOf("Compute", StringComparison.OrdinalIgnoreCase) >= 0).Take(8))
                        TryAddCounter(_gpuComputeAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in perProcessAll.Where(i => i.IndexOf("Copy", StringComparison.OrdinalIgnoreCase) >= 0).Take(8))
                        TryAddCounter(_gpuCopyAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in perProcessAll.Where(i => i.IndexOf("VideoDecode", StringComparison.OrdinalIgnoreCase) >= 0).Take(8))
                        TryAddCounter(_gpuVdecAggregated, _gpuCategoryName, _validEngineCounterName, inst, prime: true);

                    foreach (var inst in perProcessAll.Where(i => i.IndexOf("VideoEncode", StringComparison.OrdinalIgnoreCase) >= 0).Take(8))
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

                foreach (var inst in instances.Take(4)) // reducir número de instancias para consumo
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

        // --------- Fallback D3DKMT: delta de tiempos de ejecución por nodos ----------
        private static class NodeBusySampler
        {
            public static bool TrySampleTotalUsagePercent(out double percent)
            {
                percent = 0;

                try
                {
                    var s1 = Capture();
                    System.Threading.Thread.Sleep(120);
                    var s2 = Capture();

                    if (!s1.Valid || !s2.Valid || s1.NodeTimes.Length == 0 || s2.NodeTimes.Length == 0) return false;

                    int n = Math.Min(s1.NodeTimes.Length, s2.NodeTimes.Length);
                    double deltaNs = (s2.TimestampNs - s1.TimestampNs);
                    if (deltaNs <= 0) return false;

                    ulong totalDelta = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (s2.NodeTimes[i] >= s1.NodeTimes[i])
                            totalDelta += (s2.NodeTimes[i] - s1.NodeTimes[i]);
                    }

                    double busy = (totalDelta / deltaNs) * 100.0;

                    // Normalización conservadora y clamp
                    if (busy > 400) busy = busy / 10.0;
                    if (busy > 100) busy = 100;
                    if (busy < 0) busy = 0;

                    percent = busy;
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            private readonly struct Snapshot
            {
                public readonly bool Valid;
                public readonly ulong[] NodeTimes;
                public readonly double TimestampNs;

                public Snapshot(bool valid, ulong[] times, double tsNs)
                {
                    Valid = valid;
                    NodeTimes = times;
                    TimestampNs = tsNs;
                }
            }

            private static Snapshot Capture()
            {
                try
                {
                    const int BufferSize = 8192;
                    IntPtr pBuffer = IntPtr.Zero;

                    try
                    {
                        pBuffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(BufferSize);
                        for (int i = 0; i < BufferSize; i++) System.Runtime.InteropServices.Marshal.WriteByte(pBuffer, i, 0);

                        // Type=ADAPTER
                        System.Runtime.InteropServices.Marshal.WriteInt32(pBuffer, 0, (int)D3DKMT.D3DKMT_QUERYSTATISTICS_ADAPTER);

                        // Escribir LUID del adaptador primario si lo tenemos (offset 4/8)
                        if (D3DKMT.TryGetPrimaryAdapterHandle(out _, out var luid))
                        {
                            System.Runtime.InteropServices.Marshal.WriteInt32(pBuffer, 4, (int)luid.LowPart);
                            System.Runtime.InteropServices.Marshal.WriteInt32(pBuffer, 8, (int)luid.HighPart);
                        }

                        uint status = D3DKMT.D3DKMTQueryStatistics(pBuffer);
                        if (status != D3DKMT.STATUS_SUCCESS)
                            return new Snapshot(false, Array.Empty<ulong>(), 0);

                        // Intento: NodeCount en offset 16 (coincide con nuestra estructura parcial)
                        int nodeCount = 0;
                        try { nodeCount = System.Runtime.InteropServices.Marshal.ReadInt32(pBuffer, 16 + 0); } catch { nodeCount = 0; }
                        if (nodeCount <= 0 || nodeCount > 64) nodeCount = 8; // suposición conservadora

                        // Buscar bloque plausible de 'nodeCount' ulongs de RunningTime
                        int[] candidates = new[] { 64, 96, 128, 160, 192, 256, 384, 512, 768, 1024, 1536 };

                        foreach (int off in candidates)
                        {
                            if (off + (nodeCount * 8) > BufferSize) continue;

                            var times = new ulong[nodeCount];
                            bool anyNonZero = false;
                            for (int i = 0; i < nodeCount; i++)
                            {
                                ulong v = SafeReadUInt64(pBuffer, off + i * 8);
                                times[i] = v;
                                if (v != 0) anyNonZero = true;
                            }

                            if (anyNonZero)
                            {
                                double tsNs = Stopwatch.GetTimestamp() * (1e9 / Stopwatch.Frequency);
                                return new Snapshot(true, times, tsNs);
                            }
                        }

                        return new Snapshot(false, Array.Empty<ulong>(), 0);
                    }
                    finally
                    {
                        if (pBuffer != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(pBuffer);
                    }
                }
                catch
                {
                    return new Snapshot(false, Array.Empty<ulong>(), 0);
                }
            }

            private static ulong SafeReadUInt64(IntPtr basePtr, int offset)
            {
                try { return unchecked((ulong)System.Runtime.InteropServices.Marshal.ReadInt64(basePtr, offset)); } catch { return 0; }
            }
        }
    }
}