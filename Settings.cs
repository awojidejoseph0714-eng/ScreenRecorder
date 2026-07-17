using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ScreenRecorder
{
    public class AppSettings
    {
        public int BufferHours { get; set; } = 24;
        public string BufferDir { get; set; } = "";
        public string SavedDir { get; set; } = "";
        public bool HasAcceptedTerms { get; set; } = false;
        public List<string> ExcludedProcesses { get; set; } = new List<string>();
        public bool EnableOcr { get; set; } = true;
        public string SelectedMonitor { get; set; } = "Primary";
        public bool ShowBorderIndicator { get; set; } = true;
        public bool AutoUpdateCheck { get; set; } = true;

        // Hotkeys
        public uint RecordHotkeyModifiers { get; set; } = 0x0003; // Ctrl + Alt
        public uint RecordHotkeyKey { get; set; } = 82;          // R (82)
        public uint ReplayHotkeyModifiers { get; set; } = 0x0003; // Ctrl + Alt
        public uint ReplayHotkeyKey { get; set; } = 86;          // V (86)

        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenRecorderV2",
            "settings.json"
        );

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        settings.EnsureDefaults();
                        return settings;
                    }
                }
            }
            catch
            {
                // Fallback to default
            }

            var defaultSettings = new AppSettings();
            defaultSettings.EnsureDefaults();
            return defaultSettings;
        }

        public void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        private void EnsureDefaults()
        {
            if (string.IsNullOrEmpty(BufferDir))
            {
                BufferDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ScreenRecorderV2",
                    "Buffer"
                );
            }

            if (string.IsNullOrEmpty(SavedDir))
            {
                string videosPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                if (string.IsNullOrEmpty(videosPath))
                {
                    videosPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
                }
                SavedDir = Path.Combine(videosPath, "ScreenRecorder");
            }

            if (ExcludedProcesses == null)
            {
                ExcludedProcesses = new List<string>();
            }
        }
    }
}
