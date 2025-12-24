using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using System;

namespace ModernTaskManager.UI.Services
{
    public class ThemeService : IThemeService
    {
        public AppTheme CurrentTheme { get; private set; } = AppTheme.System;

        public void Initialize()
        {
            // Al inicio, usamos la configuración por defecto (System)
            SetTheme(AppTheme.System);
        }

        public void SetTheme(AppTheme theme)
        {
            CurrentTheme = theme;
            var app = Application.Current;
            if (app == null) return;

            switch (theme)
            {
                case AppTheme.Light:
                    app.RequestedThemeVariant = ThemeVariant.Light;
                    break;
                case AppTheme.Dark:
                    app.RequestedThemeVariant = ThemeVariant.Dark;
                    break;
                case AppTheme.System:
                    if (IsWindows10OrNewer())
                    {
                        // En Win10/11: Dejar que Avalonia siga al sistema
                        app.RequestedThemeVariant = ThemeVariant.Default;
                    }
                    else
                    {
                        // En Win7/8: Como no hay "System Theme", definimos uno por defecto (Dark)
                        app.RequestedThemeVariant = ThemeVariant.Dark;
                    }
                    break;
            }
        }

        private bool IsWindows10OrNewer()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            return Environment.OSVersion.Version.Major >= 10;
        }

        public Color GetSystemAccentColor() => Color.Parse("#0EA5FF");

        public Color GetUsageColor(double percent)
        {
            if (percent < 70) return Color.Parse("#0EA5FF");
            if (percent < 90) return Color.Parse("#FFD166");
            return Color.Parse("#FF5C5C");
        }
    }
}