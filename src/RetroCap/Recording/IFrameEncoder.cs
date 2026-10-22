using System;

namespace RetroCap.Recording
{
    public enum RecordFormat { Mp4, Gif }

    /// <summary>Receives top-down Bgra32 frames (stride = width * 4) with timestamps in 100 ns units.</summary>
    public interface IFrameEncoder : IDisposable
    {
        int FrameCount { get; }
        void AddFrame(byte[] bgra, long time);
        void Finish(long endTime);
    }
}
