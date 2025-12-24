using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ModernTaskManager.UI.Services;

namespace ModernTaskManager.UI
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            
            // CRÍTICO: Inicializar recursos DESPUÉS de cargar XAML
            // para que no sean sobrescritos por Theme.axaml
            BackdropService.InitializeResources();
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // 1. Cargar Configuración del Usuario
            var settingsService = new SettingsService();
            var userSettings = settingsService.LoadSettings();

            // 2. Inicializar Temas con la configuración cargada
            var themeService = new ThemeService();
            themeService.SetTheme(userSettings.Theme);

            // 3. Inicializar Idioma
            var localizationService = new LocalizationService();
            localizationService.Initialize();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}