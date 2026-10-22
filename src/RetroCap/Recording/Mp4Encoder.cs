using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RetroCap.Recording
{
    /// <summary>
    /// H.264 MP4 writer using the Media Foundation sink writer that ships with Windows 10/11.
    /// The sink writer converts our RGB32 frames to the encoder's YUV input itself.
    /// Create, use and dispose it on a single MTA thread.
    /// </summary>
    public sealed class Mp4Encoder : IFrameEncoder
    {
        private readonly int _w, _h, _fps;
        private readonly string _path;
        private IMFSinkWriter? _writer;
        private int _stream;
        private long _lastTime = -1;
        private bool _started;

        public int FrameCount { get; private set; }

        /// <param name="width">Must be even.</param>
        /// <param name="height">Must be even.</param>
        public Mp4Encoder(string path, int width, int height, int fps)
        {
            if (width % 2 != 0 || height % 2 != 0) throw new ArgumentException("H.264 needs even dimensions.");
            _path = path;
            _w = width;
            _h = height;
            _fps = fps;

            try
            {
                MF.MFStartup(MF.Version, 0);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
            {
                throw new NotSupportedException("Windows Media Foundation is not available (Windows N edition? Install the Media Feature Pack, or record a GIF).", ex);
            }
            _started = true;

            try
            {
                MF.MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out var writer);
                _writer = writer;

                // Roughly 0.1 bit per pixel per frame is plenty for screen content.
                long bitrate = Math.Clamp((long)width * height * fps / 10, 1_000_000, 24_000_000);
                ulong size = (ulong)width << 32 | (uint)height;
                ulong rate = (ulong)fps << 32 | 1u;
                ulong par = 1ul << 32 | 1u;

                MF.MFCreateMediaType(out var output);
                output.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Video);
                output.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_H264);
                output.SetUINT32(MF.MT_AVG_BITRATE, (int)bitrate);
                output.SetUINT32(MF.MT_INTERLACE_MODE, MF.Interlace_Progressive);
                output.SetUINT32(MF.MT_MPEG2_PROFILE, MF.H264Profile_High);
                output.SetUINT64(MF.MT_FRAME_SIZE, (long)size);
                output.SetUINT64(MF.MT_FRAME_RATE, (long)rate);
                output.SetUINT64(MF.MT_PIXEL_ASPECT_RATIO, (long)par);
                writer.AddStream(output, out _stream);
                Marshal.ReleaseComObject(output);

                MF.MFCreateMediaType(out var input);
                input.SetGUID(MF.MT_MAJOR_TYPE, MF.MediaType_Video);
                input.SetGUID(MF.MT_SUBTYPE, MF.VideoFormat_RGB32);
                input.SetUINT32(MF.MT_INTERLACE_MODE, MF.Interlace_Progressive);
                input.SetUINT32(MF.MT_DEFAULT_STRIDE, width * 4); // positive = top-down rows
                input.SetUINT64(MF.MT_FRAME_SIZE, (long)size);
                input.SetUINT64(MF.MT_FRAME_RATE, (long)rate);
                input.SetUINT64(MF.MT_PIXEL_ASPECT_RATIO, (long)par);
                writer.SetInputMediaType(_stream, input, IntPtr.Zero);
                Marshal.ReleaseComObject(input);

                writer.BeginWriting();
            }
            catch
            {
                Dispose();
                TryDelete();
                throw;
            }
        }

        public void AddFrame(byte[] bgra, long time)
        {
            if (_writer == null) throw new ObjectDisposedException(nameof(Mp4Encoder));
            int length = _w * _h * 4;
            if (bgra.Length < length) throw new ArgumentException("Frame is too small.", nameof(bgra));
            if (time <= _lastTime) time = _lastTime + 1; // timestamps must increase
            _lastTime = time;

            MF.MFCreateMemoryBuffer(length, out var buffer);
            MF.MFCreateSample(out var sample);
            try
            {
                buffer.Lock(out IntPtr data, out _, out _);
                Marshal.Copy(bgra, 0, data, length);
                buffer.Unlock();
                buffer.SetCurrentLength(length);
                sample.AddBuffer(buffer);
                sample.SetSampleTime(time);
                sample.SetSampleDuration(10_000_000L / _fps);
                _writer.WriteSample(_stream, sample);
                FrameCount++;
            }
            finally
            {
                // Release now: each buffer holds a whole frame and the GC would keep them around.
                Marshal.ReleaseComObject(sample);
                Marshal.ReleaseComObject(buffer);
            }
        }

        public void Finish(long endTime)
        {
            if (_writer == null) return;
            if (FrameCount == 0) throw new InvalidOperationException("No frames were recorded.");
            _writer.DoFinalize();
        }

        public void Dispose()
        {
            if (_writer != null)
            {
                Marshal.ReleaseComObject(_writer);
                _writer = null;
            }
            if (_started)
            {
                MF.MFShutdown();
                _started = false;
            }
        }

        private void TryDelete()
        {
            try { File.Delete(_path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // ---------------------------------------------------------------------
        // Media Foundation interop. Interfaces declare only the vtable prefix we call;
        // the _xx placeholders keep slots in order and are never invoked.
        // ---------------------------------------------------------------------

        private static class MF
        {
            public const int Version = 0x00020070; // MF_VERSION
            public const int Interlace_Progressive = 2;
            public const int H264Profile_High = 100;

            public static readonly Guid MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
            public static readonly Guid MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
            public static readonly Guid MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
            public static readonly Guid MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
            public static readonly Guid MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
            public static readonly Guid MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
            public static readonly Guid MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
            public static readonly Guid MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
            public static readonly Guid MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
            public static readonly Guid MediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
            public static readonly Guid VideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
            public static readonly Guid VideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");

            [DllImport("mfplat.dll", PreserveSig = false)]
            public static extern void MFStartup(int version, int flags);

            [DllImport("mfplat.dll", PreserveSig = false)]
            public static extern void MFShutdown();

            [DllImport("mfplat.dll", PreserveSig = false)]
            public static extern void MFCreateMediaType(out IMFMediaType type);

            [DllImport("mfplat.dll", PreserveSig = false)]
            public static extern void MFCreateSample(out IMFSample sample);

            [DllImport("mfplat.dll", PreserveSig = false)]
            public static extern void MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

            [DllImport("mfreadwrite.dll", PreserveSig = false)]
            public static extern void MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url,
                IntPtr byteStream, IntPtr attributes, out IMFSinkWriter writer);
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType
        {
            // IMFAttributes
            void _00(); void _01(); void _02(); void _03(); void _04(); void _05(); void _06(); void _07();
            void _08(); void _09(); void _10(); void _11(); void _12(); void _13(); void _14(); void _15();
            void _16(); void _17();
            void SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
            void SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, long value);
            void _20();
            void SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            // IMFAttributes (30 methods)
            void _00(); void _01(); void _02(); void _03(); void _04(); void _05(); void _06(); void _07();
            void _08(); void _09(); void _10(); void _11(); void _12(); void _13(); void _14(); void _15();
            void _16(); void _17(); void _18(); void _19(); void _20(); void _21(); void _22(); void _23();
            void _24(); void _25(); void _26(); void _27(); void _28(); void _29();
            // IMFSample
            void _GetSampleFlags(); void _SetSampleFlags(); void _GetSampleTime();
            void SetSampleTime(long time);
            void _GetSampleDuration();
            void SetSampleDuration(long duration);
            void _GetBufferCount(); void _GetBufferByIndex(); void _ConvertToContiguousBuffer();
            void AddBuffer(IMFMediaBuffer buffer);
        }

        [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            void Unlock();
            void _GetCurrentLength();
            void SetCurrentLength(int length);
        }

        [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSinkWriter
        {
            void AddStream(IMFMediaType targetType, out int streamIndex);
            void SetInputMediaType(int streamIndex, IMFMediaType inputType, IntPtr encodingParameters);
            void BeginWriting();
            void WriteSample(int streamIndex, IMFSample sample);
            void _SendStreamTick(); void _PlaceMarker(); void _NotifyEndOfSegment(); void _Flush();
            void DoFinalize(); // IMFSinkWriter::Finalize
        }
    }
}
