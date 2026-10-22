using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetroCap.Recording;

namespace RetroCap.Services
{
    public sealed class Settings
    {
        public string SaveFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RetroCap");
        public bool AutoSave { get; set; }
        public bool HideWindowOnCapture { get; set; } = true;
        public bool StartWithWindows { get; set; }
        /// <summary>Last format picked for Ctrl+Alt+Z screen recording.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RecordFormat RecordFormat { get; set; } = RecordFormat.Mp4;

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RetroCap", "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch (Exception) { /* corrupt file: start from defaults */ }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception) { /* settings are best-effort */ }
        }
    }

    public static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "RetroCap";

        public static void Apply(bool enabled)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                if (key == null) return;
                if (enabled) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
                else key.DeleteValue(Name, throwOnMissingValue: false);
            }
            catch (Exception) { /* policy may block HKCU\Run */ }
        }
    }
}
