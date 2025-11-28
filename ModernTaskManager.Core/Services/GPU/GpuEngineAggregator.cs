using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace ModernTaskManager.Core.Services.GPU
{
    /// <summary>
    /// Agregador estilo Administrador de Tareas para uso global de GPU.
    /// - Agrupa instancias por adaptador (luid_xx_phys_N).
    /// - Clasifica motores: 3D, Compute, Copy, VideoDecode, VideoEncode.
    /// - El uso global se calcula como suma (clamp 0-100) de 3D + Compute (o máximo motor si muy bajo).
    /// - Aplica suavizado (EMA) para evitar fluctuaciones bruscas.
    /// </summary>
    internal sealed class GpuEngineAggregator : IDisposable
    {
        private static readonly Regex AdapterRegex =
            new(@"luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_(\d+)", RegexOptions.Compiled);

        private readonly Dictionary<string, List<PerformanceCounter>> _adapterCounters = new();
        private readonly Dictionary<string, double> _emaGlobalUsage = new();

        private bool _initialized;
        private bool _available;
        private const double EmaAlpha = 0.35;

        private GpuGlobalSnapshot? _lastSnapshot;

        public bool Available => _available;
        public GpuGlobalSnapshot? LastSnapshot => _lastSnapshot;

        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                if (!PerformanceCounterCategory.Exists("GPU Engine"))
                {
                    _available = false;
                    return;
                }

                var cat = new PerformanceCounterCategory("GPU Engine");
                var instances = cat.GetInstanceNames();
                string[] candidates = { "Utilization Percentage", "% GPU Usage", "GPU Usage", "Utilization" };

                foreach (var inst in instances)
                {
                    if (inst.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    string adapterKey = ExtractAdapterKey(inst);
                    if (!_adapterCounters.TryGetValue(adapterKey, out var list))
                    {
                        list = new List<PerformanceCounter>();
                        _adapterCounters[adapterKey] = list;
                    }

                    foreach (var cn in candidates)
                    {
                        try
                        {
                            var pc = new PerformanceCounter("GPU Engine", cn, inst, true);
                            try { _ = pc.NextValue(); } catch { pc.Dispose(); continue; }
                            list.Add(pc);
                            break;
                        }
                        catch { }
                    }
                }

                _available = _adapterCounters.Values.Any(l => l.Count > 0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GpuEngineAggregator.Initialize error: {ex.Message}");
                _available = false;
            }
        }

        public GpuGlobalSnapshot Refresh()
        {
            if (!_available)
            {
                _lastSnapshot = new GpuGlobalSnapshot();
                return _lastSnapshot;
            }

            var adapters = new List<GpuAdapterSnapshot>();

            foreach (var kv in _adapterCounters.ToList())
            {
                string adapterKey = kv.Key;
                var counters = kv.Value;

                double sum3D = 0, sumCompute = 0, maxEngine = 0;
                int c3D = 0, cCompute = 0;

                foreach (var pc in counters.ToList())
                {
                    double raw;
                    try { raw = pc.NextValue(); }
                    catch { try { pc.Dispose(); } catch { } counters.Remove(pc); continue; }

                    if (double.IsNaN(raw) || double.IsInfinity(raw)) continue;
                    double val = Clamp(raw, 0, 100);
                    if (val > maxEngine) maxEngine = val;

                    string inst = pc.InstanceName ?? string.Empty;
                    if (inst.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    { sum3D += val; c3D++; }
                    else if (inst.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase))
                    { sumCompute += val; cCompute++; }
                }

                double threeD = Aggregate(sum3D);
                double compute = Aggregate(sumCompute);
                double rawGlobal = Clamp(threeD + compute, 0, 100);
                if (rawGlobal < 0.5 && maxEngine > rawGlobal) rawGlobal = maxEngine;

                if (_emaGlobalUsage.TryGetValue(adapterKey, out var prev))
                    rawGlobal = prev * (1 - EmaAlpha) + rawGlobal * EmaAlpha;
                _emaGlobalUsage[adapterKey] = rawGlobal;

                adapters.Add(new GpuAdapterSnapshot
                {
                    AdapterKey = adapterKey,
                    GlobalUsagePercent = rawGlobal,
                    ThreeDPercent = threeD,
                    ComputePercent = compute
                });
            }

            _lastSnapshot = new GpuGlobalSnapshot
            {
                Adapters = adapters,
                GlobalHighestAdapterUsage = adapters.Select(a => a.GlobalUsagePercent).DefaultIfEmpty(0).Max()
            };
            return _lastSnapshot;
        }

        private static double Aggregate(double sum) => sum > 100 ? 100 : sum;
        private static string ExtractAdapterKey(string inst)
        {
            var m = AdapterRegex.Match(inst);
            if (m.Success) return $"{m.Groups[1].Value}_phys_{m.Groups[2].Value}";
            int iPhys = inst.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase);
            if (iPhys >= 0)
            {
                int iEng = inst.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
                return iEng > iPhys ? inst.Substring(iPhys, iEng - iPhys) : inst.Substring(iPhys);
            }
            return "adapter_default";
        }
        private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

        public void Dispose()
        {
            foreach (var list in _adapterCounters.Values)
                foreach (var c in list) try { c.Dispose(); } catch { }
            _adapterCounters.Clear();
        }
    }

    internal sealed class GpuAdapterSnapshot
    {
        public string AdapterKey { get; set; } = string.Empty;
        public double GlobalUsagePercent { get; set; }
        public double ThreeDPercent { get; set; }
        public double ComputePercent { get; set; }
    }

    internal sealed class GpuGlobalSnapshot
    {
        public List<GpuAdapterSnapshot> Adapters { get; set; } = new();
        public double GlobalHighestAdapterUsage { get; set; }
    }
}