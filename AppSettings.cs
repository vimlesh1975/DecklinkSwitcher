using System;
using System.IO;
using System.Text.Json;

namespace DecklinkSwitcher
{
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
        
        public int MatteColorIndex { get; set; } = 4;
        
        public static AppSettings Load()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
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
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
            }
            catch { }
        }
    }
}
