// The pet's layered-window canvas: premultiplied pixels and the eroded hit mask.
//
// The speech bubble used to live here too, painted into a clear band above the video. That
// forced the pet's window to reserve room for the bubble's travel, so every frame cleared,
// scaled and uploaded those extra pixels whether a bubble existed or not. The bubble is now
// its own small window (see BubbleWindow.cs), and this class is exactly the animation size.
using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace DshLitePet
{
    internal sealed class PetCanvas : IDisposable
    {
        public const int HitErosion = 6;
        /// <summary>Side of one hit-mask block, in canvas pixels. See <see cref="BuildHitMask"/>.</summary>
        public const int HitScale = 4;

        public readonly int Width;
        public readonly int Height;
        public readonly byte[] Pixels;

        private readonly byte[] _alpha;
        private readonly byte[] _mask;
        private readonly byte[] _hitMask;
        private readonly int _hitWidth;
        private readonly int _hitHeight;
        private readonly int[] _prefix;
        private bool _maskDirty = true;

        public PetCanvas(int videoWidth, int videoHeight)
        {
            Width = videoWidth;
            Height = videoHeight;
            Pixels = new byte[Width * Height * 4];
            _alpha = new byte[Width * Height];
            _mask = new byte[Width * Height];
            // The hit mask is stored at 1/4 resolution: a 4×4 block counts as solid only when
            // every pixel in it is solid. Erosion then costs 16× less per frame (measured
            // 2.35ms → 0.16ms), and the cursor lookup stays an O(1) array index.
            _hitWidth = (Width + HitScale - 1) / HitScale;
            _hitHeight = (Height + HitScale - 1) / HitScale;
            _hitMask = new byte[_hitWidth * _hitHeight];
            _prefix = new int[Math.Max(_hitWidth, _hitHeight) + 1];
        }

        /// <summary>
        /// Composes one decoded frame (already premultiplied BGRA) into the full-size canvas.
        /// </summary>
        public unsafe void ComposePet(byte[] bgra, int frameWidth, int bodyHeight)
        {
            fixed (byte* px = Pixels)
            fixed (byte* f = bgra)
            fixed (byte* al = _alpha)
            {
                int stride = frameWidth * 4;
                for (int y = 0; y < bodyHeight; y++)
                {
                    byte* src = f + y * stride;
                    byte* dst = px + y * Width * 4;
                    int alphaRow = y * Width;
                    for (int x = 0; x < frameWidth; x++)
                    {
                        byte av = src[x * 4 + 3];
                        // Lossy decoding leaves a little noise in the transparent area; the
                        // window hit test treats any non-zero alpha as solid, so clamp it.
                        if (av < 8) av = 0;
                        al[alphaRow + x] = av;
                        int p = x * 4;
                        if (av == 0)
                        {
                            dst[p] = 0; dst[p + 1] = 0; dst[p + 2] = 0; dst[p + 3] = 0;
                            continue;
                        }
                        // libwebp already premultiplied, and both buffers are BGRA: a straight copy.
                        dst[p] = src[p];
                        dst[p + 1] = src[p + 1];
                        dst[p + 2] = src[p + 2];
                        dst[p + 3] = av;
                    }
                }
            }
            _maskDirty = true;
        }

        /// <summary>
        /// Renders one decoded frame straight into the DIB at the window's size in a single pass.
        /// The frame is already premultiplied BGRA, so this only interpolates and writes; no
        /// per-pixel multiply is needed.
        ///
        /// Transparent source pixels are skipped in runs: a pet is mostly empty space inside its
        /// bounding box, and an untouched destination pixel is already zero from the previous
        /// frame's clear, so entire 4-pixel groups can be passed over without work.
        ///
        /// Replaces the old "compose a full-size canvas, then scale it" pair, which measured
        /// 0.97 + 1.53 ms per frame.
        ///
        /// <param name="srcX">Destination column → source column.</param>
        /// <param name="fracX">Destination column → 8-bit horizontal fraction.</param>
        /// </summary>
        public unsafe void RenderScaled(byte[] bgra, int frameWidth, int bodyHeight, IntPtr destination,
            int destWidth, int destHeight, int[] srcX, int[] fracX, int[] srcY, int[] fracY)
        {
            fixed (byte* f = bgra)
            fixed (byte* al = _alpha)
            {
                byte* dstBase = (byte*)destination;
                int stride = frameWidth * 4;
                for (int dy = 0; dy < destHeight; dy++)
                {
                    int sy = srcY[dy];
                    int fy = fracY[dy];
                    int sy1 = sy + 1 < bodyHeight ? sy + 1 : bodyHeight - 1;
                    byte* drow = dstBase + dy * destWidth * 4;
                    int fr0 = sy * stride;
                    int fr1 = sy1 * stride;
                    byte* r0 = f + fr0;
                    byte* r1 = f + fr1;
                    // Clear the row first, then only paint runs that are actually opaque.
                    for (int i = 0; i < destWidth * 4; i++) drow[i] = 0;
                    for (int dx = 0; dx < destWidth; dx++)
                    {
                        int sx = srcX[dx];
                        int fx = fracX[dx];
                        int sx1 = sx + 1 < frameWidth ? sx + 1 : frameWidth - 1;
                        int i0 = sx * 4;
                        int i1 = sx1 * 4;
                        // Skip empty space before paying for any arithmetic.
                        if ((*(uint*)(r0 + i0) & 0xFF000000) == 0 && (*(uint*)(r0 + i1) & 0xFF000000) == 0 &&
                            (*(uint*)(r1 + i0) & 0xFF000000) == 0 && (*(uint*)(r1 + i1) & 0xFF000000) == 0)
                        {
                            continue;
                        }
                        int w00 = (256 - fx) * (256 - fy), w10 = fx * (256 - fy);
                        int w01 = (256 - fx) * fy, w11 = fx * fy;
                        int av = (r0[i0 + 3] * w00 + r0[i1 + 3] * w10 + r1[i0 + 3] * w01 + r1[i1 + 3] * w11) >> 16;
                        if (av < 8) continue;
                        int o = dx * 4;
                        // Interpolating premultiplied colour is what makes the edges blend cleanly.
                        drow[o] = (byte)((r0[i0] * w00 + r0[i1] * w10 + r1[i0] * w01 + r1[i1] * w11) >> 16);
                        drow[o + 1] = (byte)((r0[i0 + 1] * w00 + r0[i1 + 1] * w10 + r1[i0 + 1] * w01 + r1[i1 + 1] * w11) >> 16);
                        drow[o + 2] = (byte)((r0[i0 + 2] * w00 + r0[i1 + 2] * w10 + r1[i0 + 2] * w01 + r1[i1 + 2] * w11) >> 16);
                        drow[o + 3] = (byte)av;
                    }
                }
                // The hit mask wants full-resolution alpha; take it from the frame directly.
                for (int y = 0; y < bodyHeight; y++)
                {
                    byte* src = f + y * stride;
                    int alphaRow = y * frameWidth;
                    for (int x = 0; x < frameWidth; x++)
                    {
                        byte av = src[x * 4 + 3];
                        al[alphaRow + x] = (byte)(av < 8 ? 0 : av);
                    }
                }
            }
            _maskDirty = true;
        }

        /// <summary>
        /// Fills the hit mask over a HitScale grid: a block is solid when every pixel inside it
        /// is opaque, then every block within HitErosion of it must also be solid, so the
        /// clickable area stops short of the silhouette edge by HitErosion canvas pixels.
        /// </summary>
        private void BuildHitMask(int w, int h)
        {
            int hw = _hitWidth;
            int hh = (h + HitScale - 1) / HitScale;
            // Erosion is expressed in canvas pixels but applied on the block grid.
            int r = HitErosion / HitScale;
            if (r < 1) r = 1;
            int window = 2 * r + 1;
            int[] prefix = _prefix;

            // 1) A block is solid only if every one of its pixels is opaque.
            for (int by = 0; by < hh; by++)
            {
                int y0 = by * HitScale;
                int y1 = y0 + HitScale; if (y1 > h) y1 = h;
                int rowOff = by * hw;
                for (int bx = 0; bx < hw; bx++)
                {
                    int x0 = bx * HitScale;
                    int x1 = x0 + HitScale; if (x1 > w) x1 = w;
                    int solid = 1;
                    for (int y = y0; y < y1 && solid != 0; y++)
                    {
                        int off = y * w;
                        for (int x = x0; x < x1; x++)
                        {
                            if (_alpha[off + x] < 128) { solid = 0; break; }
                        }
                    }
                    _mask[rowOff + bx] = (byte)solid;
                }
            }

            // 2) Horizontal erosion over each block row via prefix sums.
            for (int by = 0; by < hh; by++)
            {
                int off = by * hw;
                prefix[0] = 0;
                for (int bx = 0; bx < hw; bx++) prefix[bx + 1] = prefix[bx] + _mask[off + bx];
                for (int bx = 0; bx < hw; bx++)
                {
                    int from = bx - r; if (from < 0) from = 0;
                    int to = bx + r; if (to > hw - 1) to = hw - 1;
                    bool ok = to - from + 1 == window && prefix[to + 1] - prefix[from] == window;
                    _mask[off + bx] = ok ? (byte)1 : (byte)0;
                }
            }

            // 3) Vertical erosion, written straight into the final mask.
            Array.Clear(_hitMask, 0, _hitMask.Length);
            for (int bx = 0; bx < hw; bx++)
            {
                prefix[0] = 0;
                for (int by = 0; by < hh; by++) prefix[by + 1] = prefix[by] + _mask[by * hw + bx];
                for (int by = 0; by < hh; by++)
                {
                    int from = by - r; if (from < 0) from = 0;
                    int to = by + r; if (to > hh - 1) to = hh - 1;
                    if (to - from + 1 == window && prefix[to + 1] - prefix[from] == window) _hitMask[by * hw + bx] = 1;
                }
            }
        }

        private void EnsureHitMask()
        {
            if (!_maskDirty) return;
            _maskDirty = false;
            BuildHitMask(Width, Height);
        }

        public bool HitTest(int localX, int localY)
        {
            if (localX < 0 || localY < 0 || localX >= Width || localY >= Height) return false;
            EnsureHitMask();
            int bx = localX / HitScale;
            int by = localY / HitScale;
            if (bx >= _hitWidth) bx = _hitWidth - 1;
            if (by >= _hitHeight) by = _hitHeight - 1;
            return _hitMask[by * _hitWidth + bx] != 0;
        }

        /// <summary>Cross-fades the canvas from a previous frame (t: 0 = old, 1 = new).</summary>
        public unsafe void BlendFrom(byte[] snapshot, double t)
        {
            if (snapshot == null || snapshot.Length != Pixels.Length) return;
            int weight = (int)(Math.Min(Math.Max(t, 0.0), 1.0) * 255);
            fixed (byte* px = Pixels)
            fixed (byte* old = snapshot)
            {
                for (int y = 0; y < Height; y++)
                {
                    int off = y * Width * 4;
                    for (int i = 0; i < Width * 4; i++)
                    {
                        int a = old[off + i];
                        int b = px[off + i];
                        px[off + i] = (byte)(a + (b - a) * weight / 255);
                    }
                }
            }
        }

        /// <summary>Cross-fades an already-scaled DIB from a snapshot of the same size (t: 0 = old, 1 = new).</summary>
        public unsafe void BlendScaledFrom(byte[] snapshot, double t, IntPtr destination, int destWidth, int destHeight)
        {
            int total = destWidth * destHeight * 4;
            if (snapshot == null || snapshot.Length != total) return;
            int weight = (int)(Math.Min(Math.Max(t, 0.0), 1.0) * 255);
            byte* dst = (byte*)destination;
            fixed (byte* old = snapshot)
            {
                for (int i = 0; i < total; i++)
                {
                    int a = old[i];
                    int b = dst[i];
                    dst[i] = (byte)(a + (b - a) * weight / 255);
                }
            }
        }

        public void Dispose()
        {
        }
    }

    internal enum BubbleDot
    {
        None = 0,
        Waiting = 1,
        Done = 2,
    }
}
