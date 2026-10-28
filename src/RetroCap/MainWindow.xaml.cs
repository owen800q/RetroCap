using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using RetroCap.Recording;
using RetroCap.Services;
using RetroCap.Ui;

namespace RetroCap
{
    public partial class MainWindow : SapWindow
    {
        private readonly Settings _settings;

        /// <summary>Set by the app on exit so closing really closes instead of hiding to tray.</summary>
        public bool AllowClose { get; set; }

        public event Action? CaptureRequested;
        public event Action? DelayedCaptureRequested;
        public event Action? RecordRequested;
        public event Action? ExitRequested;

        public MainWindow(Settings settings)
        {
            _settings = settings;
            InitializeComponent();

            FolderBox.Text = settings.SaveFolder;
            AutoSaveCheck.IsChecked = settings.AutoSave;
            HideCheck.IsChecked = settings.HideWindowOnCapture;
            StartupCheck.IsChecked = settings.StartWithWindows;
            StartMinimizedCheck.IsChecked = settings.StartMinimized;
            StartMinimizedCheck.IsEnabled = settings.StartWithWindows;
            GifCheck.IsChecked = settings.RecordFormat == RecordFormat.Gif;

            CaptureButton.Click += (_, _) => CaptureRequested?.Invoke();
            CaptureNowButton.Click += (_, _) => CaptureRequested?.Invoke();
            DelayButton.Click += (_, _) => DelayedCaptureRequested?.Invoke();
            RecordButton.Click += (_, _) => RecordRequested?.Invoke();
            HideButton.Click += (_, _) => Hide();
            ExitButton.Click += (_, _) => ExitRequested?.Invoke();
            OpenFolderButton.Click += (_, _) => OpenFolder();
            BrowseButton.Click += (_, _) => Browse();

            FolderBox.LostFocus += (_, _) => { _settings.SaveFolder = FolderBox.Text.Trim(); _settings.Save(); };
            AutoSaveCheck.Click += (_, _) => { _settings.AutoSave = AutoSaveCheck.IsChecked == true; _settings.Save(); };
            GifCheck.Click += (_, _) =>
            {
                _settings.RecordFormat = GifCheck.IsChecked == true ? RecordFormat.Gif : RecordFormat.Mp4;
                _settings.Save();
            };
            HideCheck.Click += (_, _) => { _settings.HideWindowOnCapture = HideCheck.IsChecked == true; _settings.Save(); };
            StartupCheck.Click += (_, _) =>
            {
                _settings.StartWithWindows = StartupCheck.IsChecked == true;
                StartMinimizedCheck.IsEnabled = _settings.StartWithWindows;
                ApplyAutostart();
            };
            StartMinimizedCheck.Click += (_, _) =>
            {
                _settings.StartMinimized = StartMinimizedCheck.IsChecked == true;
                ApplyAutostart();
            };
        }

        private void ApplyAutostart()
        {
            if (Autostart.Apply(_settings.StartWithWindows, _settings.StartMinimized))
            {
                SetStatus(_settings.StartWithWindows ? "RetroCap will start when you sign in to Windows." : "RetroCap will no longer start with Windows.");
            }
            else
            {
                // Registry blocked (e.g. group policy): undo the change so the UI tells the truth.
                _settings.StartWithWindows = Autostart.IsRegistered();
                StartupCheck.IsChecked = _settings.StartWithWindows;
                StartMinimizedCheck.IsEnabled = _settings.StartWithWindows;
                SetStatus("Error: could not change the Windows startup entry.");
            }
            _settings.Save();
        }

        public void SetStatus(string text) => StatusText.Text = text;

        public void SetHotkeyState(bool capture, bool record)
        {
            SetTag(HotkeyTag, HotkeyTagText, capture);
            SetTag(RecordHotkeyTag, RecordHotkeyTagText, record);
            if (!capture)
                HintText.Text = "Ctrl+Alt+A is taken (QQ/WeChat?). Use New Capture or the tray icon.";
            else if (!record)
                HintText.Text = "Ctrl+Alt+Z is taken by another program. Use Record or the tray icon.";
        }

        private void SetTag(System.Windows.Controls.Border tag, System.Windows.Controls.TextBlock text, bool registered)
        {
            text.Text = registered ? "Registered" : "In use by another program";
            tag.Background = (System.Windows.Media.Brush)FindResource(registered ? "Sap.StatusSuccessBg" : "Sap.StatusWarningBg");
            tag.BorderBrush = (System.Windows.Media.Brush)FindResource(registered ? "Sap.StatusSuccess" : "Sap.StatusError");
        }

        public void SetRecordFormat(RecordFormat format) => GifCheck.IsChecked = format == RecordFormat.Gif;

        /// <summary>Turns the Record toolbar button into Stop while a recording runs.</summary>
        public void SetRecording(bool recording)
        {
            RecordButtonText.Text = recording ? "Stop" : "Record";
            RecordButton.ToolTip = recording ? "Stop Recording  (Ctrl+Alt+Z)" : "Record Screen  (Ctrl+Alt+Z)";
            RecordGlyph.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Browse()
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select Save Folder",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(FolderBox.Text) ? FolderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            FolderBox.Text = dlg.SelectedPath;
            _settings.SaveFolder = dlg.SelectedPath;
            _settings.Save();
            SetStatus("Save folder changed.");
        }

        private void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(_settings.SaveFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.SaveFolder}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message);
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // The close box hides to the notification area; Exit really quits.
            if (!AllowClose)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }
    }
}
