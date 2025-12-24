using ModernTaskManager.UI.Services;

namespace ModernTaskManager.UI.Models
{
    public class UserSettings
    {
        // Aquí irán todas las opciones futuras
        public AppTheme Theme { get; set; } = AppTheme.System;

        // Ejemplo futuro: public string StartPage { get; set; } = "Processes";
    }
}