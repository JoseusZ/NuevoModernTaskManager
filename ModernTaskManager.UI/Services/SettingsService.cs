using ModernTaskManager.UI.Models;
using System;
using System.IO;
using System.Text.Json;

namespace ModernTaskManager.UI.Services
{
    public class SettingsService
    {
        private readonly string _filePath;

        public SettingsService()
        {
            // Guarda en %AppData%/ModernTaskManager/settings.json
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ModernTaskManager");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            _filePath = Path.Combine(folder, "settings.json");
        }

        public UserSettings LoadSettings()
        {
            if (!File.Exists(_filePath))
            {
                return new UserSettings(); // Retorna defaults si no existe
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
            }
            catch
            {
                return new UserSettings(); // Si falla, retorna defaults
            }
        }

        public void SaveSettings(UserSettings settings)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }
    }
}