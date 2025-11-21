// En: ModernTaskManager.Core/Native/D3DKMT.cs

using System;
using System.Runtime.InteropServices;

namespace ModernTaskManager.Core.Native
{
    public static class D3DKMT
    {
        // --- Constantes y Enums ---
        public const uint D3DKMT_QUERYSTATISTICS_ADAPTER = 0;
        public const uint D3DKMT_QUERYSTATISTICS_PROCESS = 1;
        public const uint D3DKMT_QUERYSTATISTICS_PROCESS_ADAPTER = 2;
        public const uint D3DKMT_QUERYSTATISTICS_SEGMENT = 3;
        public const uint D3DKMT_QUERYSTATISTICS_VIDEO_MEMORY = 4;

        // --- Estructuras ---

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;    // Entrada: Nombre (ej: "\\\\.\\DISPLAY1")
            public uint hAdapter;        // Salida: Handle del adaptador
            public LUID AdapterLuid;     // Salida: ID único del adaptador
            public uint VidPnSourceId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_QUERYSTATISTICS_QUERY_SEGMENT
        {
            public uint SegmentId;
        }

        // Estructura simplificada para consultar estadísticas
        // Nota: D3DKMT_QUERYSTATISTICS es una unión gigante en C++, aquí la simulamos
        [StructLayout(LayoutKind.Explicit, Size = 2000)] // Tamaño seguro para evitar desbordamiento
        public struct D3DKMT_QUERYSTATISTICS
        {
            [FieldOffset(0)]
            public uint Type;

            [FieldOffset(4)]
            public LUID AdapterLuid;

            [FieldOffset(12)]
            public uint hProcess;

            [FieldOffset(16)]
            public D3DKMT_QUERYSTATISTICS_RESULT_ADAPTER ResultAdapter;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_QUERYSTATISTICS_RESULT_ADAPTER
        {
            public uint NodeCount;
            public uint SegmentCount;
            public ulong DeviceSharedSystemMemory;
            public ulong DeviceDedicatedSystemMemory;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_ADAPTERINFO
        {
            public uint hAdapter;
            public LUID AdapterLuid;
            public ulong NumOfSegments;
            public ulong NodeCount;
        }

        // --- Funciones Exportadas ---

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern uint D3DKMTOpenAdapterFromGdiDisplayName(ref D3DKMT_OPENADAPTERFROMGDIDISPLAYNAME pData);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern uint D3DKMTQueryStatistics(IntPtr pData);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern uint D3DKMTCloseAdapter(ref uint hAdapter);
    }
}