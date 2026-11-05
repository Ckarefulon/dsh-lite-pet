// The layered window canvas: premultiplied pet pixels, the eroded hit mask, and the speech bubble.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace DshLitePet
{
    internal sealed class PetCanvas : IDisposable
    {
        /// <summary>Height of the bubble strip above the pet video.</summary>
        public const int BubbleArea = 104;
        public const int BubbleWidth = 336;
        public const int BubbleMargin = 10;
        public const int DotSize = 8;
        public const int CornerRadius = 11;
        public const int HitErosion = 4;

        public readonly int Width;
        public readonly int Height;
        public readonly int PetTop;

        public readonly byte[] Pixels;
        private readonly byte[] _alpha;
        private readonly byte[] _mask;
        private readonly byte[] _hitMask;
        private readonly int[] _prefix;
        private readonly Bitmap _bubble;
        private readonly Graphics _bubbleGraphics;
        private Rectangle _bubbleRect;
        private bool _bubbleVisible;
        private bool _maskDirty = true;

        public PetCanvas(int videoWidth, int videoHeight)
        {
            Width = videoWidth;
            Height = videoHeight + BubbleArea;
            PetTop = BubbleArea;
            Pixels = new byte[Width * Height * 4];
            _alpha = new byte[videoWidth * videoHeight];
            _mask = new byte[videoWidth * videoHeight];
            _hitMask = new byte[videoWidth * videoHeight];
            _prefix = new int[Math.Max(videoWidth, videoHeight) + 1];
            _bubble = new Bitmap(Width, BubbleArea, PixelFormat.Format32bppPArgb);
            _bubbleGraphics = Graphics.FromImage(_bubble);
            _bubbleGraphics.SmoothingMode = SmoothingMode.AntiAlias;
            _bubbleGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            _bubbleGraphics.Clear(Color.Transparent);
        }

        public Rectangle BubbleRect { get { return _bubbleRect; } }
        public bool BubbleVisible { get { return _bubbleVisible; } }

        public unsafe void ComposePet(byte[] color, byte[] alpha, int videoWidth, int videoHeight)
        {
            // Clear previous pet pixels only (bubble strip stays untouched, it is redrawn per frame).
            fixed (byte* px = Pixels)
            fixed (byte* a = alpha)
            fixed (byte* c = color)
            fixed (byte* al = _alpha)
            {
                for (int y = 0; y < videoHeight; y++)
                {
                    byte* src = a + y * videoWidth * 4;
                    byte* rgb = c + y * videoWidth * 4;
                    int dstRow = (PetTop + y) * Width * 4;
                    byte* dst = px + dstRow;
                    int alphaRow = y * videoWidth;
                    for (int x = 0; x < videoWidth; x++)
                    {
                        byte av = src[x * 4 + 1]; // alpha video is gray; any channel works
                        al[alphaRow + x] = av;
                        int p = x * 4;
                        if (av == 0)
                        {
                            dst[p] = 0; dst[p + 1] = 0; dst[p + 2] = 0; dst[p + 3] = 0;
                            continue;
                        }
                        if (av == 255)
                        {
                            // Interior pixels are the overwhelming majority: copy, do not multiply.
                            dst[p] = rgb[p]; dst[p + 1] = rgb[p + 1]; dst[p + 2] = rgb[p + 2]; dst[p + 3] = 255;
                            continue;
                        }
                        dst[p] = (byte)(rgb[p] * av / 255);
                        dst[p + 1] = (byte)(rgb[p + 1] * av / 255);
                        dst[p + 2] = (byte)(rgb[p + 2] * av / 255);
                        dst[p + 3] = av;
                    }
                }
            }
            _maskDirty = true;
        }

        /// <summary>Separable min-filter: a pixel is hittable only when its whole (2r+1)^2 box is opaque.</summary>
        private void BuildHitMask(int w, int h)
        {
            int r = HitErosion;
            int window = 2 * r + 1;
            int[] prefix = _prefix;
            for (int y = 0; y < h; y++)
            {
                int off = y * w;
                prefix[0] = 0;
                for (int x = 0; x < w; x++) prefix[x + 1] = prefix[x] + (_alpha[off + x] >= 128 ? 1 : 0);
                for (int x = 0; x < w; x++)
                {
                    int from = x - r; if (from < 0) from = 0;
                    int to = x + r; if (to > w - 1) to = w - 1;
                    bool ok = to - from + 1 == window && prefix[to + 1] - prefix[from] == window;
                    _mask[off + x] = ok ? (byte)1 : (byte)0;
                }
            }
            // Vertical pass reads the horizontal result and writes the final mask into _hitMask.
            Array.Clear(_hitMask, 0, w * h);
            for (int x = 0; x < w; x++)
            {
                prefix[0] = 0;
                for (int y = 0; y < h; y++) prefix[y + 1] = prefix[y] + _mask[y * w + x];
                for (int y = 0; y < h; y++)
                {
                    int from = y - r; if (from < 0) from = 0;
                    int to = y + r; if (to > h - 1) to = h - 1;
                    if (to - from + 1 == window && prefix[to + 1] - prefix[from] == window) _hitMask[y * w + x] = 1;
                }
            }
        }

        public bool HitTestPet(int localX, int localY)
        {
            int y = localY - PetTop;
            if (localX < 0 || y < 0 || localX >= Width || y >= Height - PetTop) return false;
            EnsureHitMask();
            return _hitMask[y * Width + localX] != 0;
        }

        private void EnsureHitMask()
        {
            if (!_maskDirty) return;
            _maskDirty = false;
            BuildHitMask(Width, Height - PetTop);
        }

        public bool HitTest(int localX, int localY)
        {
            if (_bubbleVisible && _bubbleRect.Contains(localX, localY)) return true;
            return HitTestPet(localX, localY);
        }

        /// <summary>Draws the speech bubble into the bitmap strip and copies it into the canvas.</summary>
        public unsafe void DrawBubble(string text, BubbleDot dot)
        {
            _bubbleVisible = text != null && text.Length > 0;
            _bubbleGraphics.Clear(Color.Transparent);
            if (!_bubbleVisible)
            {
                _bubbleRect = Rectangle.Empty;
                ClearStrip();
                return;
            }

            Font font = SystemFonts.MessageBoxFont;
            int textLeft = BubbleMargin + DotSize + 8;
            int maxTextWidth = BubbleWidth - textLeft - 12;
            var format = new StringFormat(StringFormatFlags.NoWrap);
            format.Trimming = StringTrimming.EllipsisCharacter;
            var measured = _bubbleGraphics.MeasureString(text, font, new SizeF(maxTextWidth, 200), format);
            int lines = Math.Max(1, (int)Math.Ceiling(measured.Height / font.GetHeight()));
            if (lines > 3) measured.Height = font.GetHeight() * 3 + 2;
            int boxHeight = (int)Math.Ceiling(measured.Height) + 16;
            int boxWidth = (int)Math.Ceiling(measured.Width) + textLeft + 12;
            if (boxWidth < 96) boxWidth = 96;
            int boxX = (Width - boxWidth) / 2;
            int boxY = BubbleArea - boxHeight - 6;

            using (var path = RoundedRect(new Rectangle(boxX, boxY, boxWidth, boxHeight), CornerRadius))
            using (var fill = new SolidBrush(Color.FromArgb(244, 252, 252, 252)))
            using (var border = new Pen(Color.FromArgb(34, 0, 0, 0), 1f))
            {
                _bubbleGraphics.FillPath(fill, path);
                _bubbleGraphics.DrawPath(border, path);
            }

            int dotX = boxX + BubbleMargin;
            int dotY = boxY + (boxHeight - DotSize) / 2;
            if (dot != BubbleDot.None)
            {
                Color dotColor = dot == BubbleDot.Waiting ? Color.FromArgb(255, 245, 183, 0) : Color.FromArgb(255, 46, 204, 113);
                using (var dotBrush = new SolidBrush(dotColor))
                    _bubbleGraphics.FillEllipse(dotBrush, dotX, dotY, DotSize, DotSize);
            }

            var textRect = new RectangleF(boxX + textLeft, boxY + 8, maxTextWidth, boxHeight - 12);
            using (var ink = new SolidBrush(Color.FromArgb(255, 26, 26, 26)))
                _bubbleGraphics.DrawString(text, font, ink, textRect, format);
            _bubbleRect = new Rectangle(boxX, boxY, boxWidth, boxHeight);

            // Blit the bubble strip onto the canvas (premultiplied -> premultiplied, straight replace).
            BitmapData data = _bubble.LockBits(new Rectangle(0, 0, Width, BubbleArea), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                fixed (byte* px = Pixels)
                {
                    for (int y = 0; y < BubbleArea; y++)
                    {
                        byte* src = (byte*)data.Scan0 + y * data.Stride;
                        byte* dst = px + y * Width * 4;
                        for (int x = 0; x < Width * 4; x++) dst[x] = src[x];
                    }
                }
            }
            finally
            {
                _bubble.UnlockBits(data);
            }
        }

        private unsafe void ClearStrip()
        {
            fixed (byte* px = Pixels)
            {
                for (int y = 0; y < BubbleArea; y++)
                {
                    byte* dst = px + y * Width * 4;
                    for (int x = 0; x < Width * 4; x++) dst[x] = 0;
                }
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void Dispose()
        {
            _bubbleGraphics.Dispose();
            _bubble.Dispose();
        }
    }

    internal enum BubbleDot
    {
        None = 0,
        Waiting = 1,
        Done = 2,
    }
}



