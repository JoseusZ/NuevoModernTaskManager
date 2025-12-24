using ModernTaskManager.UI.Models;
using ModernTaskManager.UI.Services;
using System.Collections.Generic;
using System.Linq;

namespace ModernTaskManager.UI.ViewModels
{
    // Clase auxiliar para mostrar el tema traducido en el ComboBox
    public class ThemeDisplayItem
    {
        public AppTheme Value { get; set; }
        public string ResourceKey { get; set; } // La clave del diccionario (ej: Lang_Theme_Light)

        public ThemeDisplayItem(AppTheme value, string resourceKey)
        {
            Value = value;
            ResourceKey = resourceKey;
        }
    }

    public class SettingsViewModel : ViewModelBase
    {
        private readonly IThemeService _themeService;
        private readonly SettingsService _settingsService;
        private ThemeDisplayItem _selectedThemeItem;

        // Lista para el ComboBox (Ahora usamos el wrapper)
        public List<ThemeDisplayItem> Themes { get; } = new List<ThemeDisplayItem>
        {
            new ThemeDisplayItem(AppTheme.System, "Lang_Theme_System"),
            new ThemeDisplayItem(AppTheme.Light, "Lang_Theme_Light"),
            new ThemeDisplayItem(AppTheme.Dark, "Lang_Theme_Dark")
        };

        public ThemeDisplayItem SelectedThemeItem
        {
            get => _selectedThemeItem;
            set
            {
                if (_selectedThemeItem != value)
                {
                    _selectedThemeItem = value;

                    // 1. Aplicar tema visualmente
                    _themeService.SetTheme(value.Value);

                    // 2. Guardar en disco
                    SaveCurrentSettings();

                    OnPropertyChanged();
                }
            }
        }

        public SettingsViewModel()
        {
            _themeService = new ThemeService();
            _settingsService = new SettingsService();

            // Cargar configuración guardada
            var savedSettings = _settingsService.LoadSettings();

            // Buscar el item correspondiente en la lista para seleccionarlo
            _selectedThemeItem = Themes.FirstOrDefault(x => x.Value == savedSettings.Theme) ?? Themes[0];

            // Asegurarnos de que el servicio de temas esté sincronizado (por si acaso)
            if (_themeService.CurrentTheme != _selectedThemeItem.Value)
            {
                _themeService.SetTheme(_selectedThemeItem.Value);
            }
        }

        private void SaveCurrentSettings()
        {
            var settings = new UserSettings
            {
                Theme = _selectedThemeItem.Value
                // Aquí guardarías otras propiedades futuras
            };
            _settingsService.SaveSettings(settings);
        }
    }
}