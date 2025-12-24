using ModernTaskManager.Core.Gpu;
using ModernTaskManager.Core.Models;
using ModernTaskManager.Core.Native;
using System;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using ModernTaskManager.Core.Models;
using ModernTaskManager.Core.Gpu;

namespace ModernTaskManager.Core.Services.GPU
{
    // Proveedor de compatibilidad avanzada para Windows 7 / drivers antiguos.
    // Prioriza APIs del fabricante para medir % “real” de GPU.
    public sealed class ExtremeLegacyGpuProvider : IGpuUsageProvider
    {
        public string ProviderName => "Extreme Legacy (NVAPI/ADL/D3DKMT)";
        public bool IsSupported { get; private set; }

        private Backend _backend = Backend.None;

        private enum Backend
        {
            None,
            Nvidia,
            Amd,
            IntelOrUnknown
        }

        public void Initialize()
        {
            try
            {
                var vendor = DetectVendorName();

                // Intentar NVIDIA primero (NVAPI)
                if (vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    if (NvApi.TryInitialize())
                    {
                        _backend = Backend.Nvidia;
                        IsSupported = true;
                        return;
                    }
                }

                // AMD (ADL Overdrive5)
                if (vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                    vendor.Contains("ATI", StringComparison.OrdinalIgnoreCase))
                {
                    if (Adl.TryInitialize())
                    {
                        _backend = Backend.Amd;
                        IsSupported = true;
                        return;
                    }
                }

                // Intel / desconocido: intentar D3DKMT (método defensivo por nodos)
                if (LegacyD3DkmtUtil.IsSupported())
                {
                    _backend = Backend.IntelOrUnknown;
                    IsSupported = true;
                    return;
                }

                _backend = Backend.None;
                IsSupported = false;
            }
            catch
            {
                _backend = Backend.None;
                IsSupported = false;
            }
        }

        public GpuDetailInfo GetStaticInfo()
        {
            var info = new GpuDetailInfo();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController");
                foreach (var mo in searcher.Get())
                {
                    info.Name = mo["Name"]?.ToString() ?? "GPU Genérica";
                    info.DriverVersion = mo["DriverVersion"]?.ToString() ?? "";
                    info.PnpDeviceId = mo["PNPDeviceID"]?.ToString() ?? "";

                    string dateStr = mo["DriverDate"]?.ToString() ?? "";
                    if (dateStr.Length >= 8 && DateTime.TryParseExact(dateStr.Substring(0, 8), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt))
                        info.DriverDate = dt;

                    if (ulong.TryParse(mo["AdapterRAM"]?.ToString(), out var ram))
                        info.TotalDedicatedBytes = ram;

                    // Heurística de compartida (RAM/2)
                    try
                    {
                        using var sys = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                        foreach (var m in sys.Get())
                        {
                            var totalPhys = Convert.ToUInt64(m["TotalPhysicalMemory"]);
                            info.TotalSharedBytes = totalPhys / 2;
                            break;
                        }
                    }
                    catch { }

                    break;
                }
            }
            catch { }
            return info;
        }

        public GpuAdapterDynamicInfo GetUsage()
        {
            if (!IsSupported) return new GpuAdapterDynamicInfo();

            try
            {
                switch (_backend)
                {
                    case Backend.Nvidia:
                        {
                            // Preferir DynamicPstates si está disponible, si no, GetUsages[0]
                            double usage = NvApi.TryGetDynamicPstateGpuPercent(out var dyn) ? dyn : NvApi.TryGetTotalGpuUsagePercent();
                            return new GpuAdapterDynamicInfo
                            {
                                GlobalUsagePercent = Clamp01(usage),
                                DedicatedMemoryUsed = 0,
                                SharedMemoryUsed = 0
                            };
                        }
                    case Backend.Amd:
                        {
                            double usage = Adl.TryGetActivityPercent();
                            return new GpuAdapterDynamicInfo
                            {
                                GlobalUsagePercent = Clamp01(usage),
                                DedicatedMemoryUsed = 0,
                                SharedMemoryUsed = 0
                            };
                        }
                    case Backend.IntelOrUnknown:
                        {
                            if (LegacyD3DkmtUtil.TrySampleTotalUsagePercent(out var percent))
                            {
                                return new GpuAdapterDynamicInfo
                                {
                                    GlobalUsagePercent = Clamp01(percent),
                                    DedicatedMemoryUsed = 0,
                                    SharedMemoryUsed = 0
                                };
                            }
                            // fallback a 0 si el driver no expone nada útil
                            return new GpuAdapterDynamicInfo();
                        }
                    default:
                        return new GpuAdapterDynamicInfo();
                }
            }
            catch
            {
                return new GpuAdapterDynamicInfo();
            }
        }

        public void Dispose()
        {
            try
            {
                if (_backend == Backend.Nvidia) NvApi.Unload();
                if (_backend == Backend.Amd) Adl.Unload();
            }
            catch { }
        }

        private static double Clamp01(double v) => Math.Max(0, Math.Min(100, v));

        private static string DetectVendorName()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                foreach (var mo in searcher.Get())
                {
                    var name = mo["Name"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            catch { }
            return "";
        }

        // ---------------- NVAPI (NVIDIA) ----------------
        private static class NvApi
        {
            private const uint NVAPI_OK = 0;

            private const uint NvAPI_Initialize_Id = 0x0150E828;
            private const uint NvAPI_Unload_Id = 0xD22BDD7E;
            private const uint NvAPI_EnumPhysicalGPUs_Id = 0xE5AC921F;
            private const uint NvAPI_GPU_GetUsages_Id = 0x189A1FDF;
            private const uint NvAPI_GPU_GetDynamicPstatesInfoEx_Id = 0x60DED2ED;

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int NvAPI_Initialize_Delegate();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int NvAPI_Unload_Delegate();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int NvAPI_EnumPhysicalGPUs_Delegate([Out] IntPtr[] handles, out int count);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int NvAPI_GPU_GetUsages_Delegate(IntPtr gpuHandle, ref NV_USAGES usages);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int NvAPI_GPU_GetDynamicPstatesInfoEx_Delegate(IntPtr gpuHandle, ref NV_GPU_DYNAMIC_PSTATES_INFO_EX pStates);

            [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr NvAPI_QueryInterface64(uint id);
            [DllImport("nvapi.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr NvAPI_QueryInterface32(uint id);

            private static bool _initialized;

            private static T GetDelegate<T>(uint id) where T : Delegate
            {
                IntPtr p = IntPtr.Zero;
                try { p = Environment.Is64BitProcess ? NvAPI_QueryInterface64(id) : NvAPI_QueryInterface32(id); }
                catch { p = IntPtr.Zero; }
                if (p == IntPtr.Zero) throw new DllNotFoundException("NVAPI interface not found.");
                return Marshal.GetDelegateForFunctionPointer<T>(p);
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct NV_USAGES
            {
                public uint Version;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 34)]
                public uint[] Usage;
            }

            // Simplificado: P-state info
            [StructLayout(LayoutKind.Sequential)]
            private struct NV_GPU_DYNAMIC_PSTATES_INFO_EX
            {
                public uint Version;
                public uint Flags;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
                public NV_GPU_DPS_INFO[] Utilization; // 0: GPU, 1: FB, etc.
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct NV_GPU_DPS_INFO
            {
                public uint bIsPresent;
                public uint Percentage;
            }

            public static bool TryInitialize()
            {
                if (_initialized) return true;
                try
                {
                    var init = GetDelegate<NvAPI_Initialize_Delegate>(NvAPI_Initialize_Id);
                    _initialized = (uint)init() == NVAPI_OK;
                }
                catch { _initialized = false; }
                return _initialized;
            }

            public static void Unload()
            {
                if (!_initialized) return;
                try { var f = GetDelegate<NvAPI_Unload_Delegate>(NvAPI_Unload_Id); f(); } catch { }
                _initialized = false;
            }

            public static bool TryGetDynamicPstateGpuPercent(out double percent)
            {
                percent = 0;
                if (!_initialized) return false;
                try
                {
                    var enumGpu = GetDelegate<NvAPI_EnumPhysicalGPUs_Delegate>(NvAPI_EnumPhysicalGPUs_Id);
                    var getDp = GetDelegate<NvAPI_GPU_GetDynamicPstatesInfoEx_Delegate>(NvAPI_GPU_GetDynamicPstatesInfoEx_Id);

                    var handles = new IntPtr[64];
                    if ((uint)enumGpu(handles, out int count) != NVAPI_OK || count <= 0) return false;

                    var h = handles[0];
                    var info = new NV_GPU_DYNAMIC_PSTATES_INFO_EX
                    {
                        Version = (uint)((uint)Marshal.SizeOf<NV_GPU_DYNAMIC_PSTATES_INFO_EX>() | 0x10000),
                        Utilization = new NV_GPU_DPS_INFO[8]
                    };

                    if ((uint)getDp(h, ref info) != NVAPI_OK) return false;

                    // índice 0 suele ser GPU overall
                    percent = info.Utilization[0].Percentage;
                    return true;
                }
                catch { return false; }
            }

            public static double TryGetTotalGpuUsagePercent()
            {
                if (!_initialized) return 0;
                try
                {
                    var enumGpu = GetDelegate<NvAPI_EnumPhysicalGPUs_Delegate>(NvAPI_EnumPhysicalGPUs_Id);
                    var getUsages = GetDelegate<NvAPI_GPU_GetUsages_Delegate>(NvAPI_GPU_GetUsages_Id);

                    var handles = new IntPtr[64];
                    if ((uint)enumGpu(handles, out int count) != NVAPI_OK || count <= 0) return 0;

                    var h = handles[0];
                    var u = new NV_USAGES
                    {
                        Version = (uint)(Marshal.SizeOf<NV_USAGES>() | 0x10000),
                        Usage = new uint[34]
                    };

                    if ((uint)getUsages(h, ref u) != NVAPI_OK) return 0;

                    // índice 0 = GPU Core (1/100 %)
                    return u.Usage[0] / 100.0;
                }
                catch { return 0; }
            }
        }

        // ---------------- AMD ADL (Overdrive5) ----------------
        private static class Adl
        {
            private const int ADL_OK = 0;
            private const string DllName64 = "atiadlxx.dll";
            private const string DllName32 = "atiadlxy.dll";

            private static bool _initialized;
            private static IntPtr _dll = IntPtr.Zero;

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int ADL_Main_Control_Create_Delegate(AdlAllocCallback callback, int enumConnectedAdapters);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int ADL_Main_Control_Destroy_Delegate();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int ADL_Adapter_NumberOfAdapters_Get_Delegate(out int numAdapters);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int ADL_Overdrive5_CurrentActivity_Get_Delegate(int adapterIndex, ref ADLPMActivity activity);

            private static ADL_Main_Control_Create_Delegate? ADL_Main_Control_Create;
            private static ADL_Main_Control_Destroy_Delegate? ADL_Main_Control_Destroy;
            private static ADL_Adapter_NumberOfAdapters_Get_Delegate? ADL_Adapter_NumberOfAdapters_Get;
            private static ADL_Overdrive5_CurrentActivity_Get_Delegate? ADL_Overdrive5_CurrentActivity_Get;

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate IntPtr AdlAllocCallback(int size);

            private static IntPtr AdlAlloc(int size) => Marshal.AllocHGlobal(size);

            [StructLayout(LayoutKind.Sequential)]
            private struct ADLPMActivity
            {
                public int Size;
                public int EngineClock;
                public int MemoryClock;
                public int Vddc;
                public int ActivityPercent;
                public int CurrentPerformanceLevel;
                public int CurrentBusSpeed;
                public int CurrentPCIELane;
                public int MaximumPerformanceLevel;
                public int Reserved;
            }

            public static bool TryInitialize()
            {
                if (_initialized) return true;
                try
                {
                    _dll = LoadLibrary(Environment.Is64BitProcess ? DllName64 : DllName32);
                    if (_dll == IntPtr.Zero) return false;

                    ADL_Main_Control_Create = GetProc<ADL_Main_Control_Create_Delegate>(_dll, "ADL_Main_Control_Create");
                    ADL_Main_Control_Destroy = GetProc<ADL_Main_Control_Destroy_Delegate>(_dll, "ADL_Main_Control_Destroy");
                    ADL_Adapter_NumberOfAdapters_Get = GetProc<ADL_Adapter_NumberOfAdapters_Get_Delegate>(_dll, "ADL_Adapter_NumberOfAdapters_Get");
                    ADL_Overdrive5_CurrentActivity_Get = GetProc<ADL_Overdrive5_CurrentActivity_Get_Delegate>(_dll, "ADL_Overdrive5_CurrentActivity_Get");

                    if (ADL_Main_Control_Create == null || ADL_Main_Control_Destroy == null ||
                        ADL_Adapter_NumberOfAdapters_Get == null || ADL_Overdrive5_CurrentActivity_Get == null)
                        return false;

                    int r = ADL_Main_Control_Create(AdlAlloc, 1);
                    _initialized = (r == ADL_OK);
                }
                catch { _initialized = false; }
                return _initialized;
            }

            public static void Unload()
            {
                try
                {
                    if (_initialized && ADL_Main_Control_Destroy != null) ADL_Main_Control_Destroy();
                }
                catch { }
                _initialized = false;

                if (_dll != IntPtr.Zero) { try { FreeLibrary(_dll); } catch { } _dll = IntPtr.Zero; }
            }

            public static double TryGetActivityPercent()
            {
                if (!_initialized) return 0;
                try
                {
                    if (ADL_Adapter_NumberOfAdapters_Get == null || ADL_Overdrive5_CurrentActivity_Get == null) return 0;

                    if (ADL_Adapter_NumberOfAdapters_Get(out int count) != ADL_OK || count <= 0) return 0;

                    var act = new ADLPMActivity { Size = Marshal.SizeOf<ADLPMActivity>() };
                    if (ADL_Overdrive5_CurrentActivity_Get(0, ref act) != ADL_OK) return 0;

                    return Math.Max(0, Math.Min(100, act.ActivityPercent));
                }
                catch { return 0; }
            }

            private static T? GetProc<T>(IntPtr hModule, string name) where T : class
            {
                var p = GetProcAddress(hModule, name);
                if (p == IntPtr.Zero) return null;
                return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr LoadLibrary(string lpLibFileName);
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
            private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool FreeLibrary(IntPtr hModule);
        }

        // ---------------- Intel/Desconocido: D3DKMT (best-effort por nodos) ----------------
        private static class LegacyD3DkmtUtil
        {
            // Medición por delta de tiempos de ejecución en nodos (dos lecturas breves).
            public static bool IsSupported() => true; // D3DKMT existe desde WDDM 1.0

            public static bool TrySampleTotalUsagePercent(out double percent)
            {
                percent = 0;

                try
                {
                    // Tomar dos muestras con breve intervalo
                    var s1 = D3DkmtNodeSnapshot.Capture();
                    System.Threading.Thread.Sleep(120);
                    var s2 = D3DkmtNodeSnapshot.Capture();

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

                    // La suma de RunningTime por nodos vs tiempo transcurrido → % ocupado.
                    // RunningTime suele venir en 100ns units; si no, la normalización intenta ser conservadora.
                    // Intentamos detectar gran escala: si el delta total es muy superior al tiempo, aplicamos escalado.
                    double busy = (totalDelta / deltaNs) * 100.0;

                    // Normalización conservadora
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

            private readonly struct D3DkmtNodeSnapshot
            {
                public readonly bool Valid;
                public readonly ulong[] NodeTimes;
                public readonly double TimestampNs;

                private D3DkmtNodeSnapshot(bool valid, ulong[] times, double tsNs)
                {
                    Valid = valid;
                    NodeTimes = times;
                    TimestampNs = tsNs;
                }

                public static D3DkmtNodeSnapshot Capture()
                {
                    try
                    {
                        // Hacemos una QueryStatistics con Type=ADAPTER (0) y con LUID del adaptador primario cuando sea posible.
                        // Reutilizamos el buffer grande del binding existente y buscamos un bloque plausible con NodeCount y una lista de ulongs de RunningTime.
                        const int BufferSize = 8192;
                        IntPtr pBuffer = IntPtr.Zero;

                        try
                        {
                            pBuffer = Marshal.AllocHGlobal(BufferSize);
                            for (int i = 0; i < BufferSize; i++) Marshal.WriteByte(pBuffer, i, 0);

                            // Type=ADAPTER
                            Marshal.WriteInt32(pBuffer, 0, 0);

                            // Intento: escribir LUID del adaptador primario si lo tenemos (offset 4/8)
                            if (D3DKMT.TryGetPrimaryAdapterHandle(out _, out var luid))
                            {
                                Marshal.WriteInt32(pBuffer, 4, (int)luid.LowPart);
                                Marshal.WriteInt32(pBuffer, 8, (int)luid.HighPart);
                            }

                            uint status = D3DKMT.D3DKMTQueryStatistics(pBuffer);
                            if (status != D3DKMT.STATUS_SUCCESS)
                                return new D3DkmtNodeSnapshot(false, Array.Empty<ulong>(), 0);

                            // Leer NodeCount en offset 16 (como en nuestra estructura parcial)
                            int nodeCount = 0;
                            try { nodeCount = Marshal.ReadInt32(pBuffer, 16 + 0); } catch { nodeCount = 0; }
                            if (nodeCount <= 0 || nodeCount > 64) nodeCount = 8; // suposición conservadora

                            // Buscar en el buffer un bloque de 'nodeCount' ulongs plausibles “correlacionados” (posible RunningTime)
                            // Estrategia: probar offsets posibles y tomar el primer bloque que no sea todo 0 y tenga monotonicidad entre capturas.
                            // En una sola captura no sabemos si aumenta, pero al menos exigimos que no sean todos cero.
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
                                    return new D3DkmtNodeSnapshot(true, times, tsNs);
                                }
                            }

                            return new D3DkmtNodeSnapshot(false, Array.Empty<ulong>(), 0);
                        }
                        finally
                        {
                            if (pBuffer != IntPtr.Zero) Marshal.FreeHGlobal(pBuffer);
                        }
                    }
                    catch
                    {
                        return new D3DkmtNodeSnapshot(false, Array.Empty<ulong>(), 0);
                    }
                }

                private static ulong SafeReadUInt64(IntPtr basePtr, int offset)
                {
                    try { return unchecked((ulong)Marshal.ReadInt64(basePtr, offset)); } catch { return 0; }
                }
            }
        }
    }
}