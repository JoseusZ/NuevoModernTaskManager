// En: ModernTaskManager.Core/Native/Kernel32.cs

using System;
using System.Runtime.InteropServices;

namespace ModernTaskManager.Core.Native
{
    public static class Kernel32
    {
        // --- Banderas de Acceso a Procesos ---
        [Flags]
        public enum ProcessAccessFlags : uint
        {
            Terminate = 0x0001,
            SetInformation = 0x0200,
            QueryLimitedInformation = 0x1000,
            ReadControl = 0x00020000,
            Synchronize = 0x00100000
        }

        // --- Constantes de Prioridad ---
        public const uint IDLE_PRIORITY_CLASS = 0x00000040;
        public const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
        public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
        public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
        public const uint HIGH_PRIORITY_CLASS = 0x00000080;
        public const uint REALTIME_PRIORITY_CLASS = 0x00000100;

        // --- Funciones de Procesos (Control) ---

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(
            ProcessAccessFlags processAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
            int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessAffinityMask(IntPtr hProcess, IntPtr dwProcessAffinityMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        // --- CPU: Tiempos del Sistema ---

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;

            public readonly ulong ToULong()
            {
                return ((ulong)dwHighDateTime << 32) | dwLowDateTime;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(
            out FILETIME lpIdleTime,
            out FILETIME lpKernelTime,
            out FILETIME lpUserTime);

        // --- DISCO: Contadores de I/O ---

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;  // Bytes leídos
            public ulong WriteTransferCount; // Bytes escritos
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

        // --- MEMORIA: GlobalMemoryStatusEx (¡ESTA ES LA QUE FALTABA!) ---

        [StructLayout(LayoutKind.Sequential)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        // --- OTROS ---
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWow64Process(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);
    }
}