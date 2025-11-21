// ModernTaskManager.Core/Native/AdvApi32.cs

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ModernTaskManager.Core.Native
{
    public static class AdvApi32
    {
        // --- TOKENS Y USUARIOS (Para ProcessService) ---

        [Flags]
        public enum TokenAccessFlags : uint
        {
            Query = 0x0008
        }

        public enum TOKEN_INFORMATION_CLASS
        {
            TokenUser = 1
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SID_AND_ATTRIBUTES
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_USER
        {
            public SID_AND_ATTRIBUTES User;
        }

        public enum SID_NAME_USE
        {
            SidTypeUser = 1,
            SidTypeGroup,
            SidTypeDomain,
            SidTypeAlias,
            SidTypeWellKnownGroup,
            SidTypeDeletedAccount,
            SidTypeInvalid,
            SidTypeUnknown,
            SidTypeComputer
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(
            IntPtr ProcessHandle,
            TokenAccessFlags DesiredAccess,
            out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(
            IntPtr TokenHandle,
            TOKEN_INFORMATION_CLASS TokenInformationClass,
            IntPtr TokenInformation,
            uint TokenInformationLength,
            out uint ReturnLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool LookupAccountSid(
            string? lpSystemName,
            IntPtr lpSid,
            StringBuilder lpName,
            ref uint cchName,
            StringBuilder lpReferencedDomainName,
            ref uint cchReferencedDomainName,
            out SID_NAME_USE peUse);


        // --- SERVICIOS (Service Control Manager) - ¡NUEVA SECCIÓN! ---

        // Constantes de acceso
        public const int SC_MANAGER_CONNECT = 0x0001;
        public const int SC_MANAGER_ENUMERATE_SERVICE = 0x0004;

        public const int SERVICE_QUERY_STATUS = 0x0004;
        public const int SC_STATUS_PROCESS_INFO = 0; // Info level 0

        // Estructura que contiene el PID del servicio
        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_STATUS_PROCESS
        {
            public int dwServiceType;
            public int dwCurrentState;
            public int dwControlsAccepted;
            public int dwWin32ExitCode;
            public int dwServiceSpecificExitCode;
            public int dwCheckPoint;
            public int dwWaitHint;
            public int dwProcessId; // ¡El PID que buscamos!
            public int dwServiceFlags;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr OpenSCManager(
            string? machineName,
            string? databaseName,
            uint dwAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr OpenService(
            IntPtr hSCManager,
            string lpServiceName,
            uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryServiceStatusEx(
            IntPtr hService,
            int InfoLevel,
            IntPtr lpBuffer,
            uint cbBufSize,
            out uint pcbBytesNeeded);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr hSCObject);
    }
}