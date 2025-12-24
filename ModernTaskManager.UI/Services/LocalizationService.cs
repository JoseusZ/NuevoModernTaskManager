using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using System;
using System.Globalization;
using System.Linq;

namespace ModernTaskManager.UI.Services
{
    public class LocalizationService
    {
        private const string EnglishUri = "avares://ModernTaskManager.UI/Assets/Lang/en-US.axaml";
        private const string SpanishUri = "avares://ModernTaskManager.UI/Assets/Lang/es-ES.axaml";

        public void Initialize()
        {
            // 1. Detectar idioma del sistema
            var currentCulture = CultureInfo.CurrentCulture;
            string twoLetterIso = currentCulture.TwoLetterISOLanguageName;

            // 2. Determinar qué archivo cargar (Fallback a inglés)
            string targetUri = EnglishUri; // Default

            if (twoLetterIso == "es")
            {
                targetUri = SpanishUri;
            }
            // Aquí podrías agregar más 'else if' para otros idiomas en el futuro

            // 3. Cargar el diccionario
            LoadLanguage(targetUri);
        }

        private void LoadLanguage(string uriString)
        {
            var app = Application.Current;
            if (app == null) return;

            // Cargar el recurso usando AvaloniaXamlLoader
            var newDict = (ResourceDictionary)AvaloniaXamlLoader.Load(new Uri(uriString));

            // Para evitar duplicados o superposiciones, gestionamos los MergedDictionaries.
            // Una estrategia simple es tener un diccionario dedicado para idiomas.
            // Por simplicidad aquí, lo inyectamos en los recursos de la App.

            // Eliminamos claves anteriores si existen (opcional, pero limpio)
            foreach (var key in newDict.Keys)
            {
                if (app.Resources.ContainsKey(key))
                    app.Resources.Remove(key);
            }

            // Agregamos las nuevas claves
            foreach (var kvp in newDict)
            {
                app.Resources[kvp.Key] = kvp.Value;
            }
        }
    }
}