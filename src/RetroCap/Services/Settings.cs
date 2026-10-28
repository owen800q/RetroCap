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
        /// <summary>Launch RetroCap when the user signs in to Windows (HKCU\...\Run).</summary>
        public bool StartWithWindows { get; set; }
        /// <summary>When started with Windows, stay in the notification area instead of opening the window.</summary>
        public bool StartMinimized { get; set; } = true;
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

        /// <summary>True when a RetroCap entry exists under HKCU\...\Run.</summary>
        public static bool IsRegistered()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(Name) is string;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Adds or removes the Run entry. Returns false if the registry could not be written.</summary>
        public static bool Apply(bool enabled, bool minimized = true)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (key == null) return false;
                if (enabled)
                {
                    string cmd = $"\"{Environment.ProcessPath}\"" + (minimized ? " --tray" : "");
                    // Rewrite only when it changed, e.g. the exe was moved to a new folder.
                    if (key.GetValue(Name) as string != cmd) key.SetValue(Name, cmd);
                }
                else key.DeleteValue(Name, throwOnMissingValue: false);
                return true;
            }
            catch (Exception) { return false; } // policy may block HKCU\Run
        }

        /// <summary>Called on every launch so the Run entry matches the setting and points at this exe.</summary>
        public static void Sync(Settings settings) => Apply(settings.StartWithWindows, settings.StartMinimized);
    }
}
