using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace ModernTaskManager.UI.Services
{
    public enum WindowsVersion
    {
        Unknown,
        Windows7,
        Windows8,
        Windows8_1,
        Windows10,
        Windows11
    }

    internal static class BackdropService
    {
        #region Windows version detection (RtlGetVersion)

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OSVERSIONINFOW
        {
            public int dwOSVersionInfoSize;
            public int dwMajorVersion;
            public int dwMinorVersion;
            public int dwBuildNumber;
            public int dwPlatformId;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szCSDVersion;
        }

        [DllImport("ntdll.dll")]
        private static extern int RtlGetVersion(out OSVERSIONINFOW versionInfo);

        #endregion

        #region Native APIs for Window Region (Win7 rounded corners)

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

        [DllImport("user32.dll")]
        private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        #endregion

        private static WindowsVersion? _cachedVersion;

        public static WindowsVersion GetWindowsVersion()
        {
            if (_cachedVersion.HasValue)
                return _cachedVersion.Value;

            if (!OperatingSystem.IsWindows())
            {
                _cachedVersion = WindowsVersion.Unknown;
                return WindowsVersion.Unknown;
            }

            try
            {
                var info = new OSVERSIONINFOW
                {
                    dwOSVersionInfoSize = Marshal.SizeOf<OSVERSIONINFOW>()
                };

                if (RtlGetVersion(out info) == 0)
                {
                    _cachedVersion =
                        info.dwMajorVersion == 10 && info.dwBuildNumber >= 22000 ? WindowsVersion.Windows11 :
                        info.dwMajorVersion == 10 ? WindowsVersion.Windows10 :
                        info.dwMajorVersion == 6 && info.dwMinorVersion == 3 ? WindowsVersion.Windows8_1 :
                        info.dwMajorVersion == 6 && info.dwMinorVersion == 2 ? WindowsVersion.Windows8 :
                        info.dwMajorVersion == 6 && info.dwMinorVersion == 1 ? WindowsVersion.Windows7 :
                        WindowsVersion.Unknown;
                }
            }
            catch
            {
                _cachedVersion = WindowsVersion.Unknown;
            }

            return _cachedVersion ?? WindowsVersion.Unknown;
        }

        #region Theme helpers

        private static bool IsDarkTheme(Application? app)
        {
            var variant = app?.ActualThemeVariant ?? app?.RequestedThemeVariant;
            return variant != ThemeVariant.Light;
        }

        private static Color GetBackdropColor(WindowsVersion version, bool isDark)
        {
            return version switch
            {
                // Windows 7/8/8.1: Fondo SÓLIDO
                WindowsVersion.Windows7 or WindowsVersion.Windows8 or WindowsVersion.Windows8_1 => isDark
                    ? Color.FromArgb(255, 32, 32, 32)
                    : Color.FromArgb(255, 245, 245, 245),

                // Windows 10/11: Tinte semi-transparente para acrylic/mica
                WindowsVersion.Windows10 or WindowsVersion.Windows11 => isDark
                    ? Color.FromArgb(200, 32, 32, 32)
                    : Color.FromArgb(200, 245, 245, 245),

                // Fallback: Fondo sólido
                _ => isDark
                    ? Color.FromArgb(255, 45, 45, 48)
                    : Color.FromArgb(255, 240, 240, 240)
            };
        }

        #endregion

        #region Resources

        public static void InitializeResources()
        {
            var app = Application.Current;
            if (app == null) return;

            var version = GetWindowsVersion();
            var isDark = IsDarkTheme(app);

            SetResourceSafe(app, "WindowBackdropBrush",
                new SolidColorBrush(GetBackdropColor(version, isDark)));

            // Bordes redondeados: Win7 y Win11
            // Bordes rectos: Win8, Win8.1, Win10
            var cornerRadius = version switch
            {
                WindowsVersion.Windows7 => new CornerRadius(8),
                WindowsVersion.Windows11 => new CornerRadius(8),
                _ => new CornerRadius(0)
            };

            SetResourceSafe(app, "WindowCornerRadius", cornerRadius);

            SetResourceSafe(app, "WindowBorderThickness",
                version == WindowsVersion.Windows11 ? new Thickness(0) : new Thickness(1));
        }

        private static void SetResourceSafe(Application app, object key, object value)
        {
            try
            {
                app.Resources[key] = value;
            }
            catch { }
        }

        #endregion

        #region Window application

        public static void Apply(Window window, Border? backdropHost)
        {
            if (window == null)
                return;

            var app = Application.Current;
            var version = GetWindowsVersion();
            var isDark = IsDarkTheme(app);

            var backdropColor = GetBackdropColor(version, isDark);
            var backdropBrush = new SolidColorBrush(backdropColor);

            window.SystemDecorations = SystemDecorations.None;

            switch (version)
            {
                // ======================
                // WINDOWS 11
                // ======================
                case WindowsVersion.Windows11:
                    window.TransparencyLevelHint = new[]
                    {
                        WindowTransparencyLevel.Mica,
                        WindowTransparencyLevel.AcrylicBlur,
                        WindowTransparencyLevel.Blur
                    };
                    window.Background = Brushes.Transparent;
                    break;

                // ======================
                // WINDOWS 10
                // ======================
                case WindowsVersion.Windows10:
                    window.TransparencyLevelHint = new[]
                    {
                        WindowTransparencyLevel.AcrylicBlur,
                        WindowTransparencyLevel.Blur
                    };
                    window.Background = Brushes.Transparent;
                    break;

                // ======================
                // WINDOWS 7 (sin transparencia, región redondeada nativa)
                // ======================
                case WindowsVersion.Windows7:
                    window.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
                    window.Background = backdropBrush;
                    // Aplicar región redondeada cuando la ventana esté lista
                    window.Opened += OnWindowOpenedWin7;
                    window.PropertyChanged += (s, e) =>
                    {
                        if (e.Property == Window.BoundsProperty || e.Property == Window.WindowStateProperty)
                        {
                            ApplyRoundedRegion(window);
                        }
                    };
                    break;

                // ======================
                // WINDOWS 8 / 8.1 (sin transparencia, bordes rectos)
                // ======================
                case WindowsVersion.Windows8:
                case WindowsVersion.Windows8_1:
                    window.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
                    window.Background = backdropBrush;
                    break;

                // ======================
                // FALLBACK
                // ======================
                default:
                    window.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
                    window.Background = backdropBrush;
                    break;
            }

            // El backdropHost siempre recibe el color de fondo
            if (backdropHost != null)
            {
                backdropHost.Background = backdropBrush;

                if (app != null &&
                    app.TryGetResource("WindowCornerRadius", out var cr) &&
                    cr is CornerRadius corner)
                {
                    backdropHost.CornerRadius = corner;
                }
            }

            // Actualizar recurso global
            if (app != null)
            {
                SetResourceSafe(app, "WindowBackdropBrush", backdropBrush);
            }
        }

        private static void OnWindowOpenedWin7(object? sender, EventArgs e)
        {
            if (sender is Window window)
            {
                window.Opened -= OnWindowOpenedWin7;
                ApplyRoundedRegion(window);
            }
        }

        private static void ApplyRoundedRegion(Window window)
        {
            if (GetWindowsVersion() != WindowsVersion.Windows7)
                return;

            try
            {
                var handle = window.TryGetPlatformHandle();
                if (handle == null || handle.Handle == IntPtr.Zero)
                    return;

                IntPtr hwnd = handle.Handle;

                // Si está maximizado, quitar la región (ventana rectangular)
                if (window.WindowState == WindowState.Maximized)
                {
                    SetWindowRgn(hwnd, IntPtr.Zero, true);
                    return;
                }

                // Obtener tamaño actual de la ventana
                int width = (int)window.Bounds.Width;
                int height = (int)window.Bounds.Height;

                if (width <= 0 || height <= 0)
                    return;

                // Crear región redondeada (radio de 10 píxeles)
                int radius = 10;
                IntPtr rgn = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius, radius);

                if (rgn != IntPtr.Zero)
                {
                    // Aplicar la región a la ventana
                    SetWindowRgn(hwnd, rgn, true);
                    // No eliminar rgn aquí; Windows toma propiedad de él
                }
            }
            catch
            {
                // Silenciar errores
            }
        }

        #endregion
    }
}
