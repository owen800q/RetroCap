using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using RetroCap.Interop;

namespace RetroCap.Recording
{
    /// <summary>
    /// Records a screen region (virtual-screen pixels) to MP4 or GIF. A capture thread grabs frames
    /// with GDI at a fixed rate and draws the mouse cursor; an encoder thread writes them. If the
    /// encoder falls behind, frames are dropped rather than queued without limit.
    /// </summary>
    public sealed class ScreenRecorder
    {
        private const int QueueLimit = 8;

        private readonly BlockingCollection<(byte[] Pixels, long Time)> _queue = new(QueueLimit);
        private readonly ConcurrentBag<byte[]> _pool = new();
        private readonly ManualResetEventSlim _stop = new();
        private readonly Stopwatch _clock = new();
        private Thread? _captureThread, _encodeThread;
        private int _dropped;

        public Int32Rect Region { get; }
        public RecordFormat Format { get; }
        public string OutputPath { get; }
        public int Fps { get; }
        public TimeSpan Elapsed => _clock.Elapsed;
        public bool IsStopping => _stop.IsSet;

        /// <summary>Raised once on a background thread: null on success, otherwise an error message.</summary>
        public event Action<string?>? Finished;

        public ScreenRecorder(Int32Rect region, RecordFormat format, string outputPath)
        {
            // H.264 needs even dimensions; drop the odd row/column.
            if (format == RecordFormat.Mp4) region = new Int32Rect(region.X, region.Y, region.Width & ~1, region.Height & ~1);
            if (region.Width < 2 || region.Height < 2) throw new ArgumentException("The recording region is too small.");
            Region = region;
            Format = format;
            OutputPath = outputPath;
            Fps = format == RecordFormat.Gif ? 10 : 30;
        }

        /// <summary>Opens the output file and starts recording. Throws if the encoder cannot start.</summary>
        public void Start()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
            Exception? startError = null;
            using var ready = new ManualResetEventSlim();
            _encodeThread = new Thread(() => EncodeLoop(ready, e => startError = e)) { IsBackground = true, Name = "RetroCap encoder" };
            _encodeThread.SetApartmentState(ApartmentState.MTA);
            _encodeThread.Start();
            ready.Wait();
            if (startError != null) throw startError;

            _clock.Start();
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "RetroCap capture", Priority = ThreadPriority.AboveNormal };
            _captureThread.Start();
        }

        /// <summary>Stops capturing; the file is finished in the background and <see cref="Finished"/> fires.</summary>
        public void Stop() => _stop.Set();

        /// <summary>Blocks until the file has been finished (used on app exit).</summary>
        public void WaitForExit(TimeSpan timeout) => _encodeThread?.Join(timeout);

        // ---------------------------------------------------------------------

        private void CaptureLoop()
        {
            int w = Region.Width, h = Region.Height, length = w * h * 4;
            long interval = TimeSpan.TicksPerSecond / Fps;
            var cursors = new Dictionary<IntPtr, (int X, int Y)>();

            IntPtr desktop = Native.GetDesktopWindow();
            IntPtr screen = Native.GetWindowDC(desktop);
            IntPtr mem = Native.CreateCompatibleDC(screen);
            var bmi = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            IntPtr dib = Native.CreateDIBSection(mem, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr old = Native.SelectObject(mem, dib);
            Native.timeBeginPeriod(1); // 1 ms sleep resolution for even frame pacing
            try
            {
                long frame = 0;
                while (!_stop.IsSet)
                {
                    long now = _clock.Elapsed.Ticks;
                    long due = frame * interval;
                    if (now < due)
                    {
                        _stop.Wait(TimeSpan.FromTicks(due - now));
                        continue;
                    }
                    // Skip slots we missed instead of bursting to catch up.
                    frame = now / interval + 1;

                    Native.BitBlt(mem, 0, 0, w, h, screen, Region.X, Region.Y, Native.SRCCOPY);
                    DrawCursor(mem, cursors);

                    if (!_pool.TryTake(out var buf)) buf = new byte[length];
                    Marshal.Copy(bits, buf, 0, length);
                    if (!_queue.TryAdd((buf, now))) { _pool.Add(buf); _dropped++; }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Capture failed: " + ex);
            }
            finally
            {
                Native.timeEndPeriod(1);
                Native.SelectObject(mem, old);
                Native.DeleteObject(dib);
                Native.DeleteDC(mem);
                Native.ReleaseDC(desktop, screen);
                _clock.Stop();
                _queue.CompleteAdding();
            }
        }

        private void DrawCursor(IntPtr dc, Dictionary<IntPtr, (int X, int Y)> hotspots)
        {
            var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
            if (!Native.GetCursorInfo(ref ci) || (ci.flags & Native.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
            if (!hotspots.TryGetValue(ci.hCursor, out var hot))
            {
                if (Native.GetIconInfo(ci.hCursor, out var info))
                {
                    hot = (info.xHotspot, info.yHotspot);
                    if (info.hbmMask != IntPtr.Zero) Native.DeleteObject(info.hbmMask);
                    if (info.hbmColor != IntPtr.Zero) Native.DeleteObject(info.hbmColor);
                }
                hotspots[ci.hCursor] = hot;
            }
            int x = ci.ptScreenPos.X - Region.X - hot.X, y = ci.ptScreenPos.Y - Region.Y - hot.Y;
            if (x > Region.Width || y > Region.Height || x < -64 || y < -64) return;
            Native.DrawIconEx(dc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL);
        }

        private void EncodeLoop(ManualResetEventSlim ready, Action<Exception> fail)
        {
            IFrameEncoder encoder;
            try
            {
                encoder = Format == RecordFormat.Gif
                    ? GifEncoder.Create(OutputPath, Region.Width, Region.Height)
                    : new Mp4Encoder(OutputPath, Region.Width, Region.Height, Fps);
            }
            catch (Exception ex)
            {
                fail(Format == RecordFormat.Mp4 && ex is COMException
                    ? new InvalidOperationException($"MP4 encoder could not start (0x{ex.HResult:X8}). Very large regions are not supported by the Windows H.264 encoder; try a smaller region or GIF.", ex)
                    : ex);
                ready.Set();
                return;
            }
            ready.Set();

            string? error = null;
            try
            {
                using (encoder)
                {
                    foreach (var (pixels, time) in _queue.GetConsumingEnumerable())
                    {
                        if (error == null)
                        {
                            try { encoder.AddFrame(pixels, time); }
                            catch (Exception ex) { error = ex.Message; _stop.Set(); }
                        }
                        _pool.Add(pixels);
                    }
                    if (error == null)
                    {
                        if (encoder.FrameCount == 0) error = "No frames were recorded.";
                        else encoder.Finish(_clock.Elapsed.Ticks);
                    }
                }
            }
            catch (Exception ex)
            {
                error ??= ex.Message;
            }

            if (error != null)
            {
                try { File.Delete(OutputPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (_dropped > 0) Debug.WriteLine($"Recorder dropped {_dropped} frames.");
            Finished?.Invoke(error);
        }
    }
}
