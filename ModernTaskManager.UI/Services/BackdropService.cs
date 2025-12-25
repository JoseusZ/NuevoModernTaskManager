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

        #region DWM APIs for Windows 10/11 transparency effects

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int Left;
            public int Right;
            public int Top;
            public int Bottom;
        }

        // DWM Window Attributes
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWA_MICA_EFFECT = 1029; // Undocumented for older Win11 builds

        // Window Corner Preferences (Windows 11)
        private const int DWMWCP_DEFAULT = 0;
        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWCP_ROUND = 2;
        private const int DWMWCP_ROUNDSMALL = 3;

        // System Backdrop Types (Windows 11 22H2+)
        private const int DWMSBT_AUTO = 0;
        private const int DWMSBT_DISABLE = 1;
        private const int DWMSBT_MAINWINDOW = 2;  // Mica
        private const int DWMSBT_TRANSIENTWINDOW = 3;  // Acrylic
        private const int DWMSBT_TABBEDWINDOW = 4;  // Tabbed Mica

        #endregion

        private static WindowsVersion? _cachedVersion;
        private static int? _cachedBuildNumber;

        // Referencia al backdropHost para Windows 10 (para actualizar en cambio de tema)
        private static Border? _win10BackdropHost;
        private static Window? _win10Window;
        private static bool _win10EventsSubscribed = false;

        // Referencias para Windows 11
        private static Border? _win11BackdropHost;
        private static Window? _win11Window;
        private static bool _win11EventsSubscribed = false;

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
                    _cachedBuildNumber = info.dwBuildNumber;

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

        private static int GetBuildNumber()
        {
            if (_cachedBuildNumber.HasValue)
                return _cachedBuildNumber.Value;

            GetWindowsVersion(); // This will populate _cachedBuildNumber
            return _cachedBuildNumber ?? 0;
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

                // Windows 10: Fondo sólido
                WindowsVersion.Windows10 => isDark
                    ? Color.FromArgb(255, 32, 32, 32)
                    : Color.FromArgb(255, 243, 243, 243),

                // Windows 11: Fondo transparente para permitir efectos DWM
                WindowsVersion.Windows11 => isDark
                    ? Color.FromArgb(0, 0, 0, 0)
                    : Color.FromArgb(0, 0, 0, 0),

                // Fallback: Fondo sólido
                _ => isDark
                    ? Color.FromArgb(255, 45, 45, 48)
                    : Color.FromArgb(255, 240, 240, 240)
            };
        }

        // Color sólido para cuando la ventana pierde el foco
        private static Color GetInactiveBackdropColor(bool isDark)
        {
            return isDark
                ? Color.FromArgb(255, 32, 32, 32)
                : Color.FromArgb(255, 243, 243, 243);
        }

        #endregion

        #region Resources

        public static void InitializeResources()
        {
            var app = Application.Current;
            if (app == null) return;

            var version = GetWindowsVersion();
            var isDark = IsDarkTheme(app);

            // Para Windows 11 usamos transparente; Windows 10 y anteriores usan sólido
            IBrush backdropBrush = version switch
            {
                WindowsVersion.Windows11 => Brushes.Transparent,
                _ => new SolidColorBrush(GetBackdropColor(version, isDark))
            };

            SetResourceSafe(app, "WindowBackdropBrush", backdropBrush);

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

            switch (version)
            {
                // ======================
                // WINDOWS 11
                // ======================
                case WindowsVersion.Windows11:
                    ApplyWindows11Backdrop(window, backdropHost, isDark);
                    break;

                // ======================
                // WINDOWS 10
                // ======================
                case WindowsVersion.Windows10:
                    ApplyWindows10Backdrop(window, backdropHost, isDark);
                    break;

                // ======================
                // WINDOWS 7 (sin transparencia, región redondeada nativa)
                // ======================
                case WindowsVersion.Windows7:
                    ApplyWindows7Backdrop(window, backdropHost, isDark);
                    break;

                // ======================
                // WINDOWS 8 / 8.1 (sin transparencia, bordes rectos)
                // ======================
                case WindowsVersion.Windows8:
                case WindowsVersion.Windows8_1:
                    ApplyLegacyBackdrop(window, backdropHost, isDark);
                    break;

                // ======================
                // FALLBACK
                // ======================
                default:
                    ApplyLegacyBackdrop(window, backdropHost, isDark);
                    break;
            }
        }

        private static void ApplyWindows11Backdrop(Window window, Border? backdropHost, bool isDark)
        {
            // Guardar referencias
            _win11Window = window;
            _win11BackdropHost = backdropHost;

            // Configurar transparencia en Avalonia
            window.TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.Mica,
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.Transparent
            };
            window.Background = Brushes.Transparent;
            window.SystemDecorations = SystemDecorations.None;

            // Suscribirse a eventos solo una vez
            if (!_win11EventsSubscribed)
            {
                _win11EventsSubscribed = true;

                // Aplicar efectos DWM cuando la ventana esté lista
                window.Opened += (_, _) =>
                {
                    var currentIsDark = IsDarkTheme(Application.Current);
                    ApplyWindows11DwmEffects(window, currentIsDark);
                };

                // Actualizar si cambia el tema
                if (Application.Current != null)
                {
                    Application.Current.ActualThemeVariantChanged += OnWindows11ThemeChanged;
                }

                // Suscribirse a eventos de foco
                window.Activated += OnWindows11WindowActivated;
                window.Deactivated += OnWindows11WindowDeactivated;
            }

            // Aplicar fondo semitransparente al backdropHost para dar tinte
            if (backdropHost != null)
            {
                backdropHost.Background = isDark
                    ? new SolidColorBrush(Color.FromArgb(180, 32, 32, 32))
                    : new SolidColorBrush(Color.FromArgb(180, 245, 245, 245));

                backdropHost.CornerRadius = new CornerRadius(8);
            }

            // Actualizar recurso global
            if (Application.Current != null)
            {
                SetResourceSafe(Application.Current, "WindowBackdropBrush", Brushes.Transparent);
            }
        }

        private static void OnWindows11ThemeChanged(object? sender, EventArgs e)
        {
            if (_win11Window == null) return;

            var newIsDark = IsDarkTheme(Application.Current);
            
            // Actualizar backdropHost
            if (_win11BackdropHost != null && _win11Window.IsActive)
            {
                _win11BackdropHost.Background = newIsDark
                    ? new SolidColorBrush(Color.FromArgb(180, 32, 32, 32))
                    : new SolidColorBrush(Color.FromArgb(180, 245, 245, 245));
            }

            ApplyWindows11DwmEffects(_win11Window, newIsDark);
        }

        private static void OnWindows11WindowActivated(object? sender, EventArgs e)
        {
            if (_win11Window == null) return;

            var isDark = IsDarkTheme(Application.Current);

            // Activar transparencia
            _win11Window.TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.Mica,
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.Transparent
            };
            _win11Window.Background = Brushes.Transparent;

            // Actualizar backdropHost con fondo semi-transparente
            if (_win11BackdropHost != null)
            {
                _win11BackdropHost.Background = isDark
                    ? new SolidColorBrush(Color.FromArgb(180, 32, 32, 32))
                    : new SolidColorBrush(Color.FromArgb(180, 245, 245, 245));
            }

            // Re-aplicar efectos DWM
            ApplyWindows11DwmEffects(_win11Window, isDark);
        }

        private static void OnWindows11WindowDeactivated(object? sender, EventArgs e)
        {
            if (_win11Window == null) return;

            var isDark = IsDarkTheme(Application.Current);
            var inactiveColor = GetInactiveBackdropColor(isDark);

            // Desactivar transparencia - usar fondo sólido
            _win11Window.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            _win11Window.Background = new SolidColorBrush(inactiveColor);

            // Actualizar backdropHost con fondo sólido
            if (_win11BackdropHost != null)
            {
                _win11BackdropHost.Background = new SolidColorBrush(inactiveColor);
            }
        }

        private static void ApplyWindows11DwmEffects(Window window, bool isDark)
        {
            try
            {
                var handle = window.TryGetPlatformHandle();
                if (handle == null || handle.Handle == IntPtr.Zero)
                    return;

                IntPtr hwnd = handle.Handle;
                int buildNumber = GetBuildNumber();

                // 1. Establecer modo oscuro/claro
                int darkMode = isDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                // 2. Establecer esquinas redondeadas nativas de Windows 11
                int cornerPreference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));

                // 3. Aplicar Mica backdrop
                if (buildNumber >= 22523) // Windows 11 22H2+
                {
                    // Usar DWMWA_SYSTEMBACKDROP_TYPE para Mica
                    int backdropType = DWMSBT_MAINWINDOW; // Mica
                    DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                }
                else if (buildNumber >= 22000) // Windows 11 21H2
                {
                    // Usar el atributo no documentado para builds más antiguos
                    int micaEffect = 1;
                    DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref micaEffect, sizeof(int));
                }

                // 4. Extender el frame para permitir transparencia
                var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
            }
            catch
            {
                // Silenciar errores - fallback a comportamiento normal
            }
        }

        private static void ApplyWindows10Backdrop(Window window, Border? backdropHost, bool isDark)
        {
            // Guardar referencias para poder actualizar cuando cambie el tema
            _win10Window = window;
            _win10BackdropHost = backdropHost;

            // Windows 10: Usar transparencia Acrylic/Blur nativa
            window.TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.Transparent,
                WindowTransparencyLevel.None
            };
            window.Background = Brushes.Transparent;
            window.SystemDecorations = SystemDecorations.None;

            // Suscribirse a eventos solo una vez
            if (!_win10EventsSubscribed)
            {
                _win10EventsSubscribed = true;

                // Aplicar efectos DWM cuando la ventana esté lista
                window.Opened += OnWindows10WindowOpened;

                // Suscribirse a cambios de tema
                if (Application.Current != null)
                {
                    Application.Current.ActualThemeVariantChanged += OnWindows10ThemeChanged;
                }

                // Suscribirse a eventos de foco
                window.Activated += OnWindows10WindowActivated;
                window.Deactivated += OnWindows10WindowDeactivated;
            }

            // Aplicar fondo semi-transparente al backdropHost
            UpdateWindows10BackdropHost(isDark, true);

            // Actualizar recurso global
            if (Application.Current != null)
            {
                SetResourceSafe(Application.Current, "WindowBackdropBrush", Brushes.Transparent);
            }
        }

        private static void OnWindows10WindowOpened(object? sender, EventArgs e)
        {
            if (_win10Window != null)
            {
                var isDark = IsDarkTheme(Application.Current);
                ApplyWindows10DwmEffects(_win10Window, isDark);
            }
        }

        private static void UpdateWindows10BackdropHost(bool isDark, bool isActive)
        {
            if (_win10BackdropHost != null)
            {
                if (isActive)
                {
                    // Fondo semi-transparente que funciona sobre el efecto Acrylic/Blur
                    _win10BackdropHost.Background = isDark
                        ? new SolidColorBrush(Color.FromArgb(200, 32, 32, 32))
                        : new SolidColorBrush(Color.FromArgb(200, 243, 243, 243));
                }
                else
                {
                    // Fondo sólido cuando está inactivo
                    _win10BackdropHost.Background = new SolidColorBrush(GetInactiveBackdropColor(isDark));
                }

                _win10BackdropHost.CornerRadius = new CornerRadius(0); // Sin esquinas redondeadas en Win10
            }
        }

        private static void OnWindows10ThemeChanged(object? sender, EventArgs e)
        {
            if (_win10Window == null) return;

            var newIsDark = IsDarkTheme(Application.Current);
            bool isActive = _win10Window.IsActive;

            // Actualizar backdropHost con el nuevo tema
            UpdateWindows10BackdropHost(newIsDark, isActive);

            // Si está activo, mantener transparencia; si no, mantener sólido
            if (isActive)
            {
                _win10Window.Background = Brushes.Transparent;
            }
            else
            {
                _win10Window.Background = new SolidColorBrush(GetInactiveBackdropColor(newIsDark));
            }

            // Actualizar atributos DWM (modo oscuro/claro de la barra de título)
            ApplyWindows10DwmEffects(_win10Window, newIsDark);
        }

        private static void OnWindows10WindowActivated(object? sender, EventArgs e)
        {
            if (_win10Window == null) return;

            var isDark = IsDarkTheme(Application.Current);

            // Activar transparencia
            _win10Window.TransparencyLevelHint = new[]
            {
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur,
                WindowTransparencyLevel.Transparent,
                WindowTransparencyLevel.None
            };
            _win10Window.Background = Brushes.Transparent;

            // Actualizar backdropHost con fondo semi-transparente
            UpdateWindows10BackdropHost(isDark, true);

            // Re-aplicar efectos DWM
            ApplyWindows10DwmEffects(_win10Window, isDark);
        }

        private static void OnWindows10WindowDeactivated(object? sender, EventArgs e)
        {
            if (_win10Window == null) return;

            var isDark = IsDarkTheme(Application.Current);
            var inactiveColor = GetInactiveBackdropColor(isDark);

            // Desactivar transparencia - usar fondo sólido
            _win10Window.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            _win10Window.Background = new SolidColorBrush(inactiveColor);

            // Actualizar backdropHost con fondo sólido
            UpdateWindows10BackdropHost(isDark, false);
        }

        private static void ApplyWindows10DwmEffects(Window window, bool isDark)
        {
            try
            {
                var handle = window.TryGetPlatformHandle();
                if (handle == null || handle.Handle == IntPtr.Zero)
                    return;

                IntPtr hwnd = handle.Handle;
                int buildNumber = GetBuildNumber();

                // Establecer modo oscuro/claro (disponible desde build 18985)
                if (buildNumber >= 18985)
                {
                    int darkMode = isDark ? 1 : 0;
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
                }

                // Extender el frame para permitir el efecto Acrylic de Avalonia
                var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
            }
            catch
            {
                // Silenciar errores
            }
        }

        private static void ApplyWindows7Backdrop(Window window, Border? backdropHost, bool isDark)
        {
            var backdropColor = GetBackdropColor(WindowsVersion.Windows7, isDark);
            var backdropBrush = new SolidColorBrush(backdropColor);

            window.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            window.Background = backdropBrush;
            window.SystemDecorations = SystemDecorations.None;

            // Aplicar región redondeada cuando la ventana esté lista
            window.Opened += OnWindowOpenedWin7;
            window.PropertyChanged += (s, e) =>
            {
                if (e.Property == Window.BoundsProperty || e.Property == Window.WindowStateProperty)
                {
                    ApplyRoundedRegion(window);
                }
            };

            if (backdropHost != null)
            {
                backdropHost.Background = backdropBrush;
                backdropHost.CornerRadius = new CornerRadius(8);
            }

            if (Application.Current != null)
            {
                SetResourceSafe(Application.Current, "WindowBackdropBrush", backdropBrush);
            }
        }

        private static void ApplyLegacyBackdrop(Window window, Border? backdropHost, bool isDark)
        {
            var version = GetWindowsVersion();
            var backdropColor = GetBackdropColor(version, isDark);
            var backdropBrush = new SolidColorBrush(backdropColor);

            window.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            window.Background = backdropBrush;
            window.SystemDecorations = SystemDecorations.None;

            if (backdropHost != null)
            {
                backdropHost.Background = backdropBrush;
                backdropHost.CornerRadius = new CornerRadius(0);
            }

            if (Application.Current != null)
            {
                SetResourceSafe(Application.Current, "WindowBackdropBrush", backdropBrush);
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