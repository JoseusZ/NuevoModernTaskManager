// ModernTaskManager.Core/Helpers/WindowsVersion.cs
using System;

namespace ModernTaskManager.Core.Helpers
{
    public static class WindowsVersion
    {
        private static readonly Version _osVersion = Environment.OSVersion.Version;
        private static readonly bool _isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;

        public static bool IsWindows7 => _isWindows && _osVersion.Major == 6 && _osVersion.Minor == 1;
        public static bool IsWindows8 => _isWindows && _osVersion.Major == 6 && _osVersion.Minor == 2;
        public static bool IsWindows81 => _isWindows && _osVersion.Major == 6 && _osVersion.Minor == 3;
        public static bool IsWindows10OrGreater => _isWindows && _osVersion.Major >= 10;
        public static bool IsWindows8OrGreater => IsWindows8 || IsWindows81 || IsWindows10OrGreater;
    }
}