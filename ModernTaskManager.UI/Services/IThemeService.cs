using Avalonia;
using Avalonia.Media;

namespace ModernTaskManager.UI.Services
{
    // Opciones que verá el usuario en el ComboBox
    public enum AppTheme
    {
        System, // "Usar la configuración del sistema"
        Light,  // "Claro"
        Dark    // "Oscuro"
    }

    public interface IThemeService
    {
        void Initialize();
        void SetTheme(AppTheme theme); // Cambiamos ThemeMode por AppTheme
        AppTheme CurrentTheme { get; }
        Color GetSystemAccentColor();
        Color GetUsageColor(double percent);
    }
}