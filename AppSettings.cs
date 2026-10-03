using System;
using System.IO;
using System.Text.Json;

namespace DecklinkSwitcher
{
    public enum AudioState { AFV, ON, OFF }

    public class AppSettings
    {
        public double WindowWidth { get; set; } = 1200;
        public double WindowHeight { get; set; } = 700;
        public double WindowLeft { get; set; } = double.NaN;
        public double WindowTop { get; set; } = double.NaN;
        public bool WindowMaximized { get; set; } = false;
        
        public string OutputDevice { get; set; } = "";
        public System.Collections.Generic.Dictionary<string, string> InputDevices { get; set; } = new();
        public System.Collections.Generic.Dictionary<string, double> InputAudioLevels { get; set; } = new();
        public string YouTubeStreamKey { get; set; } = "";
        public double AudioLevelPgm { get; set; } = 1.0;
        public double AudioLevelColorBars { get; set; } = 1.0;
        public double AudioLevelMedia { get; set; } = 1.0;
        
        public System.Collections.Generic.Dictionary<string, AudioState> InputAudioStates { get; set; } = new();
        public AudioState AudioStateColorBars { get; set; } = AudioState.AFV;
        public AudioState AudioStateMedia { get; set; } = AudioState.AFV;
        
        public System.Collections.Generic.Dictionary<string, double> MicLevels { get; set; } = new();
        public System.Collections.Generic.Dictionary<string, AudioState> MicStates { get; set; } = new();
        
        public int MatteColorIndex { get; set; } = 4;
        
        public bool SystemAudioMonitorEnabled { get; set; } = false;
        public bool LoopMediaEnabled { get; set; } = false;
        
        private static string GetSettingsPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolder = Path.Combine(appData, "DecklinkSwitcher");
            if (!Directory.Exists(appFolder))
            {
                Directory.CreateDirectory(appFolder);
            }
            return Path.Combine(appFolder, "decklink_switcher_settings.json");
        }

        public static AppSettings Load()
        {
            string path = GetSettingsPath();
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null) return settings;
                }
                catch { }
            }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                string path = GetSettingsPath();
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }
    }
}
