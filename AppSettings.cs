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
        
        public string OutputDevice { get; set; } = "";
        public string Input1Device { get; set; } = "";
        public string Input2Device { get; set; } = "";
        public string Input3Device { get; set; } = "";
        public string Input4Device { get; set; } = "";
        
        public double AudioLevelPgm { get; set; } = 1.0;
        public double AudioLevel1 { get; set; } = 1.0;
        public double AudioLevel2 { get; set; } = 1.0;
        public double AudioLevel3 { get; set; } = 1.0;
        public double AudioLevel4 { get; set; } = 1.0;
        public double AudioLevelColorBars { get; set; } = 1.0;
        public double AudioLevelMedia { get; set; } = 1.0;
        
        public AudioState AudioState1 { get; set; } = AudioState.AFV;
        public AudioState AudioState2 { get; set; } = AudioState.AFV;
        public AudioState AudioState3 { get; set; } = AudioState.AFV;
        public AudioState AudioState4 { get; set; } = AudioState.AFV;
        public AudioState AudioStateColorBars { get; set; } = AudioState.AFV;
        public AudioState AudioStateMedia { get; set; } = AudioState.AFV;
        
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
            return Path.Combine(appFolder, "settings.json");
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
