using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ModernTaskManager.Core.Native
{
    internal static class IconHelper
    {
        [Flags]
        private enum Shgfi : uint
        {
            Icon = 0x000000100,
            SmallIcon = 0x000000001,
            UseFileAttributes = 0x000000010
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            out SHFILEINFO psfi,
            uint cbFileInfo,
            Shgfi uFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(
            IntPtr hProcess,
            int flags,
            StringBuilder exeName,
            ref int size);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        public static IntPtr TryGetProcessIcon(int pid)
        {
            string? path = TryGetExecutablePath(pid);
            if (string.IsNullOrEmpty(path))
                return IntPtr.Zero;

            try
            {
                var flags = Shgfi.Icon | Shgfi.SmallIcon | Shgfi.UseFileAttributes;
                var ok = SHGetFileInfo(path, 0, out SHFILEINFO info,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
                if (ok != IntPtr.Zero && info.hIcon != IntPtr.Zero)
                    return info.hIcon;
            }
            catch { }
            return IntPtr.Zero;
        }

        private static string? TryGetExecutablePath(int pid)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.Handle == IntPtr.Zero) return null;

                // Primero QueryFullProcessImageName (compatible Win7+)
                var sb = new StringBuilder(1024);
                int len = sb.Capacity;
                if (QueryFullProcessImageName(proc.Handle, 0, sb, ref len))
                    return sb.ToString();

                // Fallback MainModule (puede fallar por permisos)
                try { return proc.MainModule?.FileName; } catch { }
            }
            catch { }
            return null;
        }

        public static void DestroyIconSafe(IntPtr hIcon)
        {
            if (hIcon != IntPtr.Zero)
            {
                try { DestroyIcon(hIcon); } catch { }
            }
        }
    }
}