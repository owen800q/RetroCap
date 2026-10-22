using System;
using System.Collections.Generic;
using System.IO;

namespace RetroCap.Recording
{
    /// <summary>
    /// Streaming animated-GIF writer for screen recordings. Each frame only stores the rectangle that
    /// changed since the previous frame (unchanged pixels inside it are transparent), with its own
    /// 256-colour palette built by median cut, so typical screencasts stay small.
    /// </summary>
    public sealed class GifEncoder : IFrameEncoder
    {
        private const int TicksPerCentisecond = 100_000; // 100 ns units
        private const int MinDelay = 2;                  // browsers treat 0-1 cs as 10 cs

        private readonly Stream _out;
        private readonly int _w, _h;
        private byte[]? _prev;              // last frame actually written, Bgra32
        private byte[]? _pending;           // encoded frame waiting for its delay
        private int _pendingDelayOffset;
        private long _shownCs = -1;         // presentation time already accounted for, in centiseconds

        // Scratch buffers reused across frames.
        private readonly int[] _counts = new int[32768];
        private readonly long[] _sumR = new long[32768], _sumG = new long[32768], _sumB = new long[32768];
        private readonly byte[] _lut = new byte[32768];
        private readonly List<int> _used = new();
        private readonly LzwEncoder _lzw = new();

        public GifEncoder(Stream output, int width, int height)
        {
            if (width < 1 || height < 1 || width > 65535 || height > 65535) throw new ArgumentOutOfRangeException(nameof(width));
            _out = output;
            _w = width;
            _h = height;

            WriteAscii("GIF89a");
            WriteU16(width);
            WriteU16(height);
            _out.WriteByte(0); // no global colour table
            _out.WriteByte(0); // background index
            _out.WriteByte(0); // square pixels
            // NETSCAPE2.0 application extension: loop forever.
            _out.Write(new byte[] { 0x21, 0xFF, 0x0B });
            WriteAscii("NETSCAPE2.0");
            _out.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
        }

        public static GifEncoder Create(string path, int width, int height) =>
            new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16), width, height);

        public int FrameCount { get; private set; }

        /// <summary>Adds a top-down Bgra32 frame (stride = width * 4) shown from <paramref name="time"/> (100 ns units).</summary>
        public void AddFrame(byte[] bgra, long time)
        {
            if (bgra.Length < _w * _h * 4) throw new ArgumentException("Frame is too small.", nameof(bgra));

            int x0 = 0, y0 = 0, x1 = _w - 1, y1 = _h - 1;
            bool delta = _prev != null;
            if (delta && !DirtyRect(bgra, _prev!, out x0, out y0, out x1, out y1))
                return; // identical: the pending frame simply stays on screen longer

            var frame = EncodeFrame(bgra, x0, y0, x1 - x0 + 1, y1 - y0 + 1, delta, out int delayOffset);
            long cs = time / TicksPerCentisecond;
            FlushPending(cs);
            if (_shownCs < 0) _shownCs = cs;
            _pending = frame;
            _pendingDelayOffset = delayOffset;

            _prev ??= new byte[_w * _h * 4];
            Buffer.BlockCopy(bgra, 0, _prev, 0, _w * _h * 4);
            FrameCount++;
        }

        /// <summary>Writes the last frame (shown until <paramref name="endTime"/>) and the trailer.</summary>
        public void Finish(long endTime)
        {
            FlushPending(endTime / TicksPerCentisecond);
            _out.WriteByte(0x3B);
            _out.Flush();
        }

        public void Dispose() => _out.Dispose();

        private void FlushPending(long untilCs)
        {
            if (_pending == null) return;
            int delay = (int)Math.Clamp(untilCs - _shownCs, MinDelay, 65535);
            _shownCs += delay;
            _pending[_pendingDelayOffset] = (byte)delay;
            _pending[_pendingDelayOffset + 1] = (byte)(delay >> 8);
            _out.Write(_pending);
            _pending = null;
        }

        private bool DirtyRect(byte[] cur, byte[] prev, out int x0, out int y0, out int x1, out int y1)
        {
            var a = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(cur.AsSpan(0, _w * _h * 4));
            var b = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(prev.AsSpan(0, _w * _h * 4));
            x0 = _w; y0 = _h; x1 = -1; y1 = -1;
            for (int y = 0; y < _h; y++)
            {
                var ra = a.Slice(y * _w, _w);
                var rb = b.Slice(y * _w, _w);
                if (ra.SequenceEqual(rb)) continue;
                int l = 0;
                while (l < _w && (ra[l] ^ rb[l]) << 8 == 0) l++; // ignore the (undefined) alpha byte
                if (l == _w) continue;                          // only alpha differed
                int r = _w - 1;
                while ((ra[r] ^ rb[r]) << 8 == 0) r--;
                if (y < y0) y0 = y;
                y1 = y;
                if (l < x0) x0 = l;
                if (r > x1) x1 = r;
            }
            return x1 >= 0;
        }

        // ---------------------------------------------------------------------
        // Frame encoding
        // ---------------------------------------------------------------------

        private byte[] EncodeFrame(byte[] bgra, int fx, int fy, int fw, int fh, bool delta, out int delayOffset)
        {
            const byte Transparent = 255;
            int stride = _w * 4;
            byte[]? prev = delta ? _prev : null;

            // 1. Histogram of changed pixels in 5:5:5 bins, keeping exact sums for accurate palette colours.
            foreach (int k in _used) { _counts[k] = 0; _sumR[k] = _sumG[k] = _sumB[k] = 0; }
            _used.Clear();
            for (int y = fy; y < fy + fh; y++)
            {
                int i = y * stride + fx * 4;
                for (int x = 0; x < fw; x++, i += 4)
                {
                    if (prev != null && Same(bgra, prev, i)) continue;
                    int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                    int key = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
                    if (_counts[key]++ == 0) _used.Add(key);
                    _sumR[key] += r; _sumG[key] += g; _sumB[key] += b;
                }
            }

            // 2. Palette (index 255 is kept free for transparency in delta frames).
            var palette = new byte[256 * 3];
            BuildPalette(delta ? 255 : 256, palette);

            // 3. Indexed pixels.
            var indices = new byte[fw * fh];
            int n = 0;
            for (int y = fy; y < fy + fh; y++)
            {
                int i = y * stride + fx * 4;
                for (int x = 0; x < fw; x++, i += 4, n++)
                {
                    if (prev != null && Same(bgra, prev, i)) { indices[n] = Transparent; continue; }
                    indices[n] = _lut[(bgra[i + 2] >> 3) << 10 | (bgra[i + 1] >> 3) << 5 | (bgra[i] >> 3)];
                }
            }

            // 4. Serialise: graphic control extension, image descriptor, local palette, LZW data.
            var ms = new MemoryStream(fw * fh / 2 + 1024);
            ms.Write(new byte[] { 0x21, 0xF9, 0x04, (byte)(1 << 2 | (delta ? 1 : 0)) }); // disposal: leave in place
            delayOffset = (int)ms.Position;
            ms.Write(new byte[] { 0, 0, delta ? Transparent : (byte)0, 0 });
            ms.WriteByte(0x2C);
            WriteU16(ms, fx); WriteU16(ms, fy); WriteU16(ms, fw); WriteU16(ms, fh);
            ms.WriteByte(0x87); // local colour table, 256 entries
            ms.Write(palette);
            _lzw.Encode(indices, ms);
            return ms.ToArray();
        }

        private static bool Same(byte[] a, byte[] b, int i) => a[i] == b[i] && a[i + 1] == b[i + 1] && a[i + 2] == b[i + 2];

        /// <summary>Median cut over the used 5:5:5 bins. Fills <see cref="_lut"/> for every used bin.</summary>
        private void BuildPalette(int maxColors, byte[] palette)
        {
            var keys = _used.ToArray();
            var boxes = new List<Box> { MakeBox(keys, 0, keys.Length) };
            if (keys.Length > maxColors)
            {
                while (boxes.Count < maxColors)
                {
                    int best = -1;
                    double bestScore = 0;
                    for (int i = 0; i < boxes.Count; i++)
                    {
                        var bx = boxes[i];
                        if (bx.End - bx.Start < 2) continue;
                        double score = (double)bx.Count * bx.Range;
                        if (score > bestScore) { bestScore = score; best = i; }
                    }
                    if (best < 0) break;

                    var box = boxes[best];
                    int shift = box.Axis; // 10 = red, 5 = green, 0 = blue
                    keys.AsSpan(box.Start, box.End - box.Start).Sort((p, q) => ((p >> shift) & 31) - ((q >> shift) & 31));
                    long half = box.Count / 2, acc = 0;
                    int split = box.Start + 1;
                    for (int i = box.Start; i < box.End - 1; i++)
                    {
                        acc += _counts[keys[i]];
                        split = i + 1;
                        if (acc >= half) break;
                    }
                    boxes[best] = MakeBox(keys, box.Start, split);
                    boxes.Add(MakeBox(keys, split, box.End));
                }
            }
            else
            {
                // Few enough colours: one bin per entry.
                boxes.Clear();
                for (int i = 0; i < keys.Length; i++) boxes.Add(new Box { Start = i, End = i + 1 });
            }

            for (int c = 0; c < boxes.Count; c++)
            {
                long n = 0, r = 0, g = 0, b = 0;
                for (int i = boxes[c].Start; i < boxes[c].End; i++)
                {
                    int k = keys[i];
                    n += _counts[k]; r += _sumR[k]; g += _sumG[k]; b += _sumB[k];
                    _lut[k] = (byte)c;
                }
                if (n == 0) continue;
                palette[c * 3] = (byte)(r / n);
                palette[c * 3 + 1] = (byte)(g / n);
                palette[c * 3 + 2] = (byte)(b / n);
            }
        }

        private Box MakeBox(int[] keys, int start, int end)
        {
            int rMin = 31, rMax = 0, gMin = 31, gMax = 0, bMin = 31, bMax = 0;
            long count = 0;
            for (int i = start; i < end; i++)
            {
                int k = keys[i];
                int r = k >> 10, g = (k >> 5) & 31, b = k & 31;
                if (r < rMin) rMin = r; if (r > rMax) rMax = r;
                if (g < gMin) gMin = g; if (g > gMax) gMax = g;
                if (b < bMin) bMin = b; if (b > bMax) bMax = b;
                count += _counts[k];
            }
            // Weight green a little higher, as the eye is most sensitive to it.
            int dr = (rMax - rMin) * 3, dg = (gMax - gMin) * 4, db = (bMax - bMin) * 2;
            var box = new Box { Start = start, End = end, Count = count };
            if (dg >= dr && dg >= db) { box.Axis = 5; box.Range = dg; }
            else if (dr >= db) { box.Axis = 10; box.Range = dr; }
            else { box.Axis = 0; box.Range = db; }
            return box;
        }

        private struct Box
        {
            public int Start, End, Axis, Range;
            public long Count;
        }

        // ---------------------------------------------------------------------

        private void WriteAscii(string s) { foreach (char c in s) _out.WriteByte((byte)c); }
        private void WriteU16(int v) => WriteU16(_out, v);
        private static void WriteU16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }

        /// <summary>GIF variable-length LZW with 8-bit pixels, written as 255-byte sub-blocks.</summary>
        private sealed class LzwEncoder
        {
            private const int MaxCode = 4096;
            private readonly short[] _table = new short[MaxCode * 256]; // [prefix * 256 + pixel] -> code, 0 = none
            private readonly byte[] _block = new byte[256];
            private int _blockLen, _bits, _bitCount;
            private Stream _s = Stream.Null;

            public void Encode(byte[] pixels, Stream s)
            {
                const int MinCodeSize = 8, Clear = 256, Eoi = 257;
                _s = s;
                _blockLen = 0; _bits = 0; _bitCount = 0;
                s.WriteByte(MinCodeSize);

                Array.Clear(_table);
                int codeSize = MinCodeSize + 1, next = Eoi + 1;
                Emit(Clear, codeSize);

                int prefix = pixels[0];
                for (int i = 1; i < pixels.Length; i++)
                {
                    int c = pixels[i];
                    int slot = prefix << 8 | c;
                    int code = _table[slot];
                    if (code != 0) { prefix = code; continue; }

                    Emit(prefix, codeSize);
                    // The decoder adds its entry one code later, so grow the width based on the
                    // entries assigned before this step's one.
                    if (next >= 1 << codeSize && codeSize < 12) codeSize++;
                    if (next < MaxCode)
                    {
                        _table[slot] = (short)next++;
                    }
                    else
                    {
                        Emit(Clear, codeSize);
                        Array.Clear(_table);
                        codeSize = MinCodeSize + 1;
                        next = Eoi + 1;
                    }
                    prefix = c;
                }
                Emit(prefix, codeSize);
                if (next >= 1 << codeSize && codeSize < 12) codeSize++;
                Emit(Eoi, codeSize);

                if (_bitCount > 0) PutByte((byte)_bits);
                FlushBlock();
                s.WriteByte(0); // block terminator
            }

            private void Emit(int code, int size)
            {
                _bits |= code << _bitCount;
                _bitCount += size;
                while (_bitCount >= 8)
                {
                    PutByte((byte)_bits);
                    _bits >>= 8;
                    _bitCount -= 8;
                }
            }

            private void PutByte(byte b)
            {
                _block[_blockLen++] = b;
                if (_blockLen == 255) FlushBlock();
            }

            private void FlushBlock()
            {
                if (_blockLen == 0) return;
                _s.WriteByte((byte)_blockLen);
                _s.Write(_block, 0, _blockLen);
                _blockLen = 0;
            }
        }
    }
}
