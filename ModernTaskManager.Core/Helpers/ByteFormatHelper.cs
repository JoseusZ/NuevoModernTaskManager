using System;

namespace ModernTaskManager.Core.Helpers
{
    public static class ByteFormatHelper
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

        public static string Format(ulong bytes) => Format((double)bytes);

        public static string Format(double bytes)
        {
            if (bytes < 0) return "0 B";
            int idx = 0;
            while (bytes >= 1024 && idx < Units.Length - 1)
            {
                bytes /= 1024;
                idx++;
            }
            return $"{bytes:0.0} {Units[idx]}";
        }
    }
}