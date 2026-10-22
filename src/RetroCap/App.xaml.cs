using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using RetroCap.Capture;
using RetroCap.Interop;
using RetroCap.Recording;
using RetroCap.Services;
using RetroCap.Ui;

namespace RetroCap
{
    public partial class App : Application
    {
        private const string InstanceName = "RetroCap.SingleInstance";
        private const uint VK_A = 0x41;
        private const uint VK_Z = 0x5A;
        private const int CaptureHotkey = 1;
        private const int RecordHotkey = 2;

        private Mutex? _mutex;
        private EventWaitHandle? _wake;
        private HotkeyService? _hotkey;
        private TrayIcon? _tray;
        private MainWindow? _main;
        private CaptureWindow? _capture;
        private ScreenRecorder? _recorder;
        private RecordingOverlay? _recordingOverlay;
        private bool _restoreAfterRecording;
        private readonly Settings _settings = Settings.Load();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Second launch: poke the running instance (shows its window) and quit.
            _mutex = new Mutex(true, InstanceName, out bool first);
            if (!first)
            {
                try { EventWaitHandle.OpenExisting(InstanceName + ".Wake").Set(); } catch (WaitHandleCannotBeOpenedException) { }
                Shutdown();
                return;
            }
            _wake = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Wake");
            new Thread(() => { while (_wake.WaitOne()) Dispatcher.BeginInvoke(ShowMain); }) { IsBackground = true }.Start();

            _main = new MainWindow(_settings);
            MainWindow = _main;
            _main.CaptureRequested += () => StartCapture();
            _main.RecordRequested += ToggleRecording;
            _main.DelayedCaptureRequested += async () => { _main.Hide(); await Task.Delay(3000); StartCapture(forceHide: true); };
            _main.ExitRequested += ExitApp;

            _tray = new TrayIcon(() => StartCapture(), ToggleRecording, ShowMain, ExitApp);

            _hotkey = new HotkeyService();
            _hotkey.Pressed += id =>
            {
                if (id == CaptureHotkey) StartCapture();
                else if (id == RecordHotkey) ToggleRecording();
            };
            bool capture = _hotkey.Register(CaptureHotkey, Native.MOD_CONTROL | Native.MOD_ALT, VK_A);
            bool record = _hotkey.Register(RecordHotkey, Native.MOD_CONTROL | Native.MOD_ALT, VK_Z);
            _main.SetHotkeyState(capture, record);
            _main.SetStatus(capture && record ? "Ready. Hotkeys Ctrl+Alt+A and Ctrl+Alt+Z registered."
                : "Warning: hotkey " + string.Join(" and ", new[] { capture ? null : "Ctrl+Alt+A", record ? null : "Ctrl+Alt+Z" }.Where(k => k != null)) + " could not be registered.");

            if (!e.Args.Contains("--tray")) _main.Show();
            if (e.Args.Contains("--capture")) StartCapture();
            else if (e.Args.Contains("--record")) ToggleRecording();
        }

        private void ShowMain()
        {
            if (_main == null) return;
            _main.Show();
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
        }

        private async void StartCapture(bool forceHide = false, bool record = false)
        {
            if (_capture != null || _main == null || _recorder != null) return;

            bool restore = false;
            if (_main.IsVisible && (_settings.HideWindowOnCapture || forceHide))
            {
                _main.Hide();
                restore = !forceHide;
                await Task.Delay(300); // let the compositor drop the window before grabbing pixels
            }

            ScreenSnapshot shot;
            try
            {
                shot = ScreenCapture.Capture(new WindowInteropHelper(_main).Handle);
            }
            catch (Exception ex)
            {
                Toast.Show("Error: screen capture failed. " + ex.Message);
                if (restore) _main.Show();
                return;
            }

            bool recording = false;
            _capture = new CaptureWindow(shot, _settings, record);
            _capture.Completed += msg =>
            {
                _main.SetStatus(msg);
                Toast.Show(msg);
            };
            _capture.PinRequested += (img, at) =>
            {
                new PinWindow(img, at, _settings).Show();
                _main.SetStatus($"Image pinned to screen. {img.PixelWidth} x {img.PixelHeight}");
            };
            _capture.RecordRequested += (region, format) =>
            {
                recording = true;
                StartRecording(region, format, restore);
            };
            _capture.Closed += (_, _) =>
            {
                _capture = null;
                if (restore && !recording) _main.Show();
            };
            _capture.Show();
        }

        // =====================================================================
        // Screen recording (Ctrl+Alt+Z)
        // =====================================================================

        /// <summary>Ctrl+Alt+Z: pick a region to record, or stop the recording in progress.</summary>
        private void ToggleRecording()
        {
            if (_recorder != null) StopRecording();
            else StartCapture(record: true);
        }

        private async void StartRecording(Int32Rect region, RecordFormat format, bool restoreMain)
        {
            if (_main == null) return;
            _settings.RecordFormat = format;
            _settings.Save();
            _main.SetRecordFormat(format);

            string ext = format == RecordFormat.Gif ? ".gif" : ".mp4";
            string path = Path.Combine(_settings.SaveFolder, $"Recording_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
            _restoreAfterRecording = restoreMain;
            ScreenRecorder recorder;
            try
            {
                recorder = new ScreenRecorder(region, format, path);
                recorder.Finished += error => Dispatcher.BeginInvoke(() => OnRecordingFinished(recorder, error));
                _recorder = recorder;
                await Task.Delay(200); // let the selection overlay disappear before the first frame
                recorder.Start();
            }
            catch (Exception ex)
            {
                _recorder = null;
                Fail("Error: recording could not start. " + ex.Message);
                return;
            }
            if (recorder.IsStopping) return; // stopped during the start delay; Finished reports it

            _recordingOverlay = new RecordingOverlay(recorder.Region, format, () => recorder.Elapsed);
            _recordingOverlay.StopRequested += StopRecording;
            _recordingOverlay.Show();
            _tray?.SetRecording(true);
            _main.SetRecording(true);
            _main.SetStatus($"Recording {format.ToString().ToUpperInvariant()}  {recorder.Region.Width} x {recorder.Region.Height}. Press Ctrl+Alt+Z to stop.");
        }

        private void StopRecording()
        {
            if (_recorder == null || _recorder.IsStopping) return;
            _recorder.Stop();
            _recordingOverlay?.Close();
            _recordingOverlay = null;
            _main?.SetStatus("Saving recording...");
        }

        private void OnRecordingFinished(ScreenRecorder recorder, string? error)
        {
            if (_recorder != recorder) return;
            _recorder = null;
            _recordingOverlay?.Close();
            _recordingOverlay = null;
            _tray?.SetRecording(false);
            _main?.SetRecording(false);

            if (error != null)
            {
                Fail("Error: recording failed. " + error);
                return;
            }

            // Put the file on the clipboard too, so it can be pasted straight into chat apps.
            try
            {
                Clipboard.SetFileDropList(new StringCollection { recorder.OutputPath });
            }
            catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy: the file is still saved */ }

            string msg = $"Recording saved. {Path.GetFileName(recorder.OutputPath)} ({recorder.Elapsed.TotalSeconds:0.0} s)";
            _main?.SetStatus(msg);
            Toast.Show(msg);
            if (_restoreAfterRecording) _main?.Show();
        }

        private void Fail(string msg)
        {
            _main?.SetStatus(msg);
            Toast.Show(msg);
            if (_restoreAfterRecording) _main?.Show();
        }

        private void ExitApp()
        {
            // Finish a running recording so the file is not left half-written.
            if (_recorder != null)
            {
                _recorder.Stop();
                _recorder.WaitForExit(TimeSpan.FromSeconds(10));
            }
            _hotkey?.Dispose();
            _tray?.Dispose();
            if (_main != null) { _main.AllowClose = true; _main.Close(); }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _hotkey?.Dispose();
            _tray?.Dispose();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
