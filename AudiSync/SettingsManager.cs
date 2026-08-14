using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AudiSync
{
    internal class DeviceSetting
    {
        public int DelayMs { get; set; }
        public int VolumePercent { get; set; } = 100;
    }

    internal static class SettingsManager
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudiSync", "device-settings.json");

        public static Dictionary<string, DeviceSetting> Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new Dictionary<string, DeviceSetting>();

                string json = File.ReadAllText(SettingsPath);
                var result = JsonSerializer.Deserialize<Dictionary<string, DeviceSetting>>(json);
                return result ?? new Dictionary<string, DeviceSetting>();
            }
            catch
            {
                // Corrupt or unreadable settings file — fall back to defaults rather than crashing
                return new Dictionary<string, DeviceSetting>();
            }
        }

        public static void Save(Dictionary<string, DeviceSetting> settings)
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsPath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch
            {
                // Saving is best-effort — a failed save shouldn't crash the app
            }
        }
    }
}