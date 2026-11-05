// The layered window canvas: premultiplied pet pixels, the eroded hit mask, and the speech bubble
// (which can be dragged vertically, so it is composited over the pet instead of living in a fixed strip).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace DshLitePet
{
    internal sealed class PetCanvas : IDisposable
    {
        /// <summary>Height of the clear strip kept above the pet video for the bubble at rest.</summary>
        public const int BubbleArea = 104;
        public const int BubbleWidth = 336;
        public const int BubbleMargin = 10;
        public const int DotSize = 8;
        public const int CornerRadius = 11;
        public const int HitErosion = 6;
        public const int BubbleOverlapLimit = 72;

        public readonly int Width;
        public readonly int Height;
        public readonly int PetTop;
        public readonly byte[] Pixels;

        private readonly byte[] _alpha;
        private readonly byte[] _mask;
        private readonly byte[] _hitMask;
        private readonly int[] _prefix;
        private readonly Bitmap _bubbleBitmap;
        private readonly Graphics _bubbleGraphics;
        private Rectangle _bubbleRect;
        private bool _bubbleVisible;
        private string _drawnText;
        private BubbleDot _drawnDot;
        private int _drawnOffset = int.MinValue;
        private bool _maskDirty = true;

        /// <summary>Vertical nudge of the bubble, in canvas pixels; persisted by the caller.</summary>
        public int BubbleOffsetY;

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
            _bubbleBitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            _bubbleGraphics = Graphics.FromImage(_bubbleBitmap);
            _bubbleGraphics.SmoothingMode = SmoothingMode.AntiAlias;
            _bubbleGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        public Rectangle BubbleRect { get { return _bubbleRect; } }

        public unsafe void ComposePet(byte[] color, byte[] alpha, int videoWidth, int videoHeight)
        {
            fixed (byte* px = Pixels)
            fixed (byte* a = alpha)
            fixed (byte* c = color)
            fixed (byte* al = _alpha)
            {
                // The strip above the pet is redrawn from scratch: the bubble may have moved.
                for (int y = 0; y < PetTop; y++)
                {
                    byte* row = px + y * Width * 4;
                    for (int x = 0; x < Width * 4; x++) row[x] = 0;
                }
                for (int y = 0; y < videoHeight; y++)
                {
                    byte* src = a + y * videoWidth * 4;
                    byte* rgb = c + y * videoWidth * 4;
                    byte* dst = px + (PetTop + y) * Width * 4;
                    int alphaRow = y * videoWidth;
                    for (int x = 0; x < videoWidth; x++)
                    {
                        byte av = src[x * 4 + 1]; // alpha video is gray; any channel works
                        // H.264 noise gives the "transparent" background alpha 1..20; the
                        // window's per-pixel hit test treats any non-zero alpha as solid,
                        // so clamp the noise floor to zero.
                        if (av < 8) av = 0;
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

        /// <summary>Marks pixels whose whole square neighbourhood is opaque: clicks there land.</summary>
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

        private void EnsureHitMask()
        {
            if (!_maskDirty) return;
            _maskDirty = false;
            BuildHitMask(Width, Height - PetTop);
        }

        public bool HitTestPet(int localX, int localY)
        {
            int y = localY - PetTop;
            if (localX < 0 || y < 0 || localX >= Width || y >= Height - PetTop) return false;
            EnsureHitMask();
            return _hitMask[y * Width + localX] != 0;
        }

        public bool HitTest(int localX, int localY)
        {
            if (_bubbleVisible && _bubbleRect.Contains(localX, localY)) return true;
            return HitTestPet(localX, localY);
        }

        public bool HitTestBubble(int localX, int localY)
        {
            return _bubbleVisible && _bubbleRect.Contains(localX, localY);
        }

        /// <summary>Redraws the bubble bitmap when its content or position changed, then composites it.</summary>
        public void DrawBubble(string text, BubbleDot dot)
        {
            bool visible = text != null && text.Length > 0;
            if (!visible)
            {
                _bubbleVisible = false;
                _bubbleRect = Rectangle.Empty;
                _drawnText = null;
                return;
            }
            if (!_bubbleVisible || text != _drawnText || dot != _drawnDot || BubbleOffsetY != _drawnOffset)
            {
                _bubbleVisible = true;
                PaintBubble(text, dot);
                _drawnText = text;
                _drawnDot = dot;
                _drawnOffset = BubbleOffsetY;
            }
            CompositeBubble();
        }

        private unsafe void CompositeBubble()
        {
            if (!_bubbleVisible || _bubbleRect.IsEmpty) return;
            BitmapData data = _bubbleBitmap.LockBits(_bubbleRect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                fixed (byte* px = Pixels)
                {
                    for (int y = 0; y < _bubbleRect.Height; y++)
                    {
                        byte* src = (byte*)data.Scan0 + y * data.Stride;
                        byte* dst = px + (_bubbleRect.Y + y) * Width * 4 + _bubbleRect.X * 4;
                        int length = _bubbleRect.Width * 4;
                        for (int x = 0; x < length; x++) dst[x] = src[x];
                    }
                }
            }
            finally
            {
                _bubbleBitmap.UnlockBits(data);
            }
        }

        private void PaintBubble(string text, BubbleDot dot)
        {
            Font font = SystemFonts.MessageBoxFont;
            int textLeft = BubbleMargin + DotSize + 8;
            int maxTextWidth = BubbleWidth - textLeft - 12;
            var format = new StringFormat(StringFormatFlags.NoWrap);
            format.Trimming = StringTrimming.EllipsisCharacter;
            SizeF measured = _bubbleGraphics.MeasureString(text, font, new SizeF(maxTextWidth, 200), format);
            int lines = Math.Max(1, (int)Math.Ceiling(measured.Height / font.GetHeight()));
            if (lines > 3) measured.Height = font.GetHeight() * 3 + 2;
            int boxHeight = (int)Math.Ceiling(measured.Height) + 16;
            int boxWidth = (int)Math.Ceiling(measured.Width) + textLeft + 12;
            if (boxWidth < 96) boxWidth = 96;
            int boxX = (Width - boxWidth) / 2;
            int boxY = PetTop - boxHeight - 6 + BubbleOffsetY;
            if (boxY < 4) boxY = 4;
            if (boxY + boxHeight > Height - 8) boxY = Height - 8 - boxHeight;

            _bubbleGraphics.Clear(Color.Transparent);
            using (var path = RoundedRect(new Rectangle(boxX, boxY, boxWidth, boxHeight), CornerRadius))
            using (var fill = new SolidBrush(Color.FromArgb(255, 252, 252, 252)))
            using (var border = new Pen(Color.FromArgb(34, 0, 0, 0), 1f))
            {
                _bubbleGraphics.FillPath(fill, path);
                _bubbleGraphics.DrawPath(border, path);
            }

            if (dot != BubbleDot.None)
            {
                int dotX = boxX + BubbleMargin;
                int dotY = boxY + (boxHeight - DotSize) / 2;
                Color dotColor = dot == BubbleDot.Waiting
                    ? Color.FromArgb(255, 245, 183, 0)
                    : Color.FromArgb(255, 46, 204, 113);
                using (var dotBrush = new SolidBrush(dotColor))
                    _bubbleGraphics.FillEllipse(dotBrush, dotX, dotY, DotSize, DotSize);
            }

            var textRect = new RectangleF(boxX + textLeft, boxY + 8, maxTextWidth, boxHeight - 12);
            using (var ink = new SolidBrush(Color.FromArgb(255, 26, 26, 26)))
                _bubbleGraphics.DrawString(text, font, ink, textRect, format);
            _bubbleRect = new Rectangle(boxX, boxY, boxWidth, boxHeight);
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
            _bubbleBitmap.Dispose();
        }
    }

    internal enum BubbleDot
    {
        None = 0,
        Waiting = 1,
        Done = 2,
    }
}

