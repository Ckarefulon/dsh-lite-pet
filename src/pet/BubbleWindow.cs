// The speech bubble as its own layered window.
//
// It used to be painted into the pet's bitmap, which forced the pet's window to reserve
// clear bands above and below the video for the bubble to travel through — every frame
// then cleared, scaled and uploaded those extra pixels whether a bubble existed or not.
// A second, small window costs one HWND and nothing else: it is sized to the text, it is
// hidden (not merely transparent) when there is no text, and the pet's window is exactly
// the size of the animation.
//
// This class owns the bubble's geometry entirely. The pet only reports where it is on
// screen; everything here is in physical screen pixels, which is the only coordinate
// system in which "just above the pet's head" and "below its feet" are meaningful.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace DshLitePet
{
    internal sealed class BubbleWindow : IDisposable
    {
        private const string WindowClass = "DshLitePetBubble";

        /// <summary>
        /// Hard cap on the bubble width. A speech bubble is a glanceable hint, so a long
        /// agent reply is ellipsised rather than allowed to stretch across the screen.
        /// Deliberately narrower than the pet: the bubble should read as a caption.
        /// </summary>
        internal const int Width = 260;
        /// <summary>Height used for the resting position before any text has been measured.</summary>
        internal const int DefaultHeight = 30;
        private const int Margin = 10;
        private const int DotSize = 8;
        private const int CornerRadius = 11;
        /// <summary>Largest bubble the window is ever sized for; longer text is cut short.</summary>
        private const int MaxHeight = 84;
        /// <summary>Two lines: enough for a short status, not a paragraph.</summary>
        private const int MaxLines = 2;
        /// <summary>Clearance between the bubble's bottom edge and the pet's top edge.</summary>
        internal const int Gap = 6;
        /// <summary>Pixels of the bubble that must stay inside the work area.</summary>
        internal const int KeepOnScreen = 16;

        private static readonly Color DotWaiting = Color.FromArgb(255, 245, 183, 0);
        private static readonly Color DotDone = Color.FromArgb(255, 46, 204, 113);

        private static readonly Native.WndProcDelegate WndProcThunk = WndProc;
        private static bool _classRegistered;

        private IntPtr _hwnd;
        private Native.LayeredSurface _surface;
        private readonly Bitmap _bitmap;
        private readonly Graphics _graphics;
        private int _height = DefaultHeight;
        private string _drawnText;
        private BubbleDot _drawnDot;
        private bool _visible;

        // ——— geometry ————————————————————————————————————————————————

        /// <summary>The pet's rectangle in screen pixels; the bubble is anchored to it.</summary>
        private Native.RECT _pet = new Native.RECT();
        private bool _hasPet;

        /// <summary>Vertical nudge the user dragged the bubble by, in screen pixels.</summary>
        internal int OffsetY;

        // Drag state. The window procedure is static, so it needs a way back to the
        // instance; there is exactly one bubble window per process.
        private static BubbleWindow _current;
        private bool _dragging;
        private int _dragOriginY;
        private int _dragOriginOffset;
        private bool _dragMoved;
        private Action _offsetChanged;

        internal BubbleWindow()
        {
            _bitmap = new Bitmap(Width, MaxHeight, PixelFormat.Format32bppPArgb);
            _graphics = Graphics.FromImage(_bitmap);
            _graphics.SmoothingMode = SmoothingMode.AntiAlias;
            _graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            _current = this;
        }

        internal IntPtr Handle { get { return _hwnd; } }

        internal bool Visible { get { return _visible; } }

        /// <summary>Called when a finished drag changes the offset, so it can be persisted.</summary>
        internal void OnOffsetChanged(Action callback) { _offsetChanged = callback; }

        /// <summary>Creates the window; it starts hidden and is shown once there is text.</summary>
        internal void Create()
        {
            IntPtr instance = Native.GetModuleHandle(null);
            if (!_classRegistered)
            {
                var wc = new Native.WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX)),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcThunk),
                    hInstance = instance,
                    lpszClassName = WindowClass,
                    hCursor = Native.LoadCursor(IntPtr.Zero, (IntPtr)32512),
                };
                Native.RegisterClassEx(ref wc);
                _classRegistered = true;
            }
            _hwnd = Native.CreateWindowEx(
                Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
                WindowClass, "dsh-lite-pet-bubble", Native.WS_POPUP,
                0, 0, Width, _height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            _surface = new Native.LayeredSurface(Width, _height);
        }

        /// <summary>
        /// Tells the bubble where the pet is. Any move, resize or scale change has to come
        /// through here, because the resting place is derived from it.
        /// </summary>
        internal void SetPetRect(Native.RECT pet)
        {
            _pet = pet;
            _hasPet = pet.Right > pet.Left && pet.Bottom > pet.Top;
            if (_bubbleVisibleSafe()) Reposition();
        }

        private bool _bubbleVisibleSafe() { return _visible && _hwnd != IntPtr.Zero; }

        /// <summary>
        /// The bubble's resting top edge: centred on the pet, sitting just above its head.
        /// </summary>
        private int RestTop()
        {
            int petTop = _hasPet ? _pet.Top : 0;
            return petTop - _height - Gap;
        }

        /// <summary>
        /// The travel limits, in offsets from the resting place.
        ///
        /// They are deliberately asymmetric, because the two directions mean different
        /// things: down has to be able to clear the pet's whole body so the bubble can sit
        /// below its feet, while up only needs a little room. Making them equal (an earlier
        /// bug) let the bubble fly far above the pet while still overlapping it below.
        /// </summary>
        private void TravelLimits(out int up, out int down)
        {
            int rest = RestTop();
            // Down: just far enough that the bubble sits clear of the pet's feet, plus one
            // gap so it reads as "under the pet" rather than touching it. A larger budget
            // let it drift to the bottom of the screen, where it stopped reading as the
            // pet's bubble at all.
            int petHeight = _hasPet ? _pet.Bottom - _pet.Top : DefaultHeight;
            down = petHeight + _height + Gap * 2;
            if (down > petHeight + MaxHeight + Gap * 2) down = petHeight + MaxHeight + Gap * 2;

            // Up: no lift at all. The resting place is already the highest position that
            // reads as "attached to the pet"; raising it only detaches the bubble from the
            // pet it is describing, which is what made it look like it had flown away.
            up = 0;

            Native.RECT work = new Native.RECT();
            Native.SystemParametersInfo(Native.SPI_GETWORKAREA, 0, ref work, 0);
            // Neither direction may push the bubble out of the work area.
            int roomAbove = rest - work.Top;
            int roomBelow = work.Bottom - KeepOnScreen - rest;
            if (up > roomAbove) up = roomAbove;
            if (down > roomBelow) down = roomBelow;
            if (up < 0) up = 0;
            if (down < 0) down = 0;
        }

        /// <summary>
        /// Sizes the bubble to its text, repaints it, and places it relative to the pet plus
        /// the current offset. Empty text hides the window.
        /// </summary>
        internal void Update(string text, BubbleDot dot)
        {
            if (_hwnd == IntPtr.Zero) return;
            if (text == null || text.Length == 0)
            {
                Hide();
                return;
            }
            if (text != _drawnText || dot != _drawnDot)
            {
                Paint(text, dot);
                _drawnText = text;
                _drawnDot = dot;
            }
            ClampOffset();
            // ULW paints a window that is already on screen, so the window is shown first;
            // the first paint of a hidden window is lost.
            if (!_visible)
            {
                _visible = true;
                Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            }
            Reposition();
        }

        internal void Hide()
        {
            if (!_visible) return;
            _visible = false;
            Native.ShowWindow(_hwnd, Native.SW_HIDE);
            _drawnText = null;
            _drawnDot = BubbleDot.None;
        }

        private void ClampOffset()
        {
            int down, up;
            TravelLimits(out up, out down);
            if (OffsetY < -up) OffsetY = -up;
            if (OffsetY > down) OffsetY = down;
        }

        /// <summary>Window rectangle in screen coordinates.</summary>
        internal Native.RECT ScreenRect()
        {
            var rect = new Native.RECT();
            if (_hwnd != IntPtr.Zero) Native.GetWindowRect(_hwnd, ref rect);
            return rect;
        }

        // ——— painting ————————————————————————————————————————————————

        /// <summary>
        /// Draws the bubble into its own bitmap and uploads it. GDI+ owns the antialiasing,
        /// so the corners and the text land on a translucent edge instead of hard pixels.
        /// </summary>
        private void Paint(string text, BubbleDot dot)
        {
            Font font = BubbleFont();
            int textLeft = Margin + DotSize + 8;
            int maxTextWidth = Width - textLeft - 12;
            var format = new StringFormat(StringFormatFlags.NoWrap);
            format.Trimming = StringTrimming.EllipsisCharacter;
            SizeF measured = _graphics.MeasureString(text, font, new SizeF(maxTextWidth, 400), format);
            int lines = Math.Max(1, (int)Math.Ceiling(measured.Height / font.GetHeight()));
            if (lines > MaxLines) measured.Height = font.GetHeight() * MaxLines + 2;
            int boxHeight = (int)Math.Ceiling(measured.Height) + 16;
            if (boxHeight > MaxHeight) boxHeight = MaxHeight;
            if (boxHeight < DefaultHeight) boxHeight = DefaultHeight;
            int boxWidth = (int)Math.Ceiling(measured.Width) + textLeft + 12;
            if (boxWidth < 96) boxWidth = 96;
            if (boxWidth > Width) boxWidth = Width;
            int boxX = (Width - boxWidth) / 2;
            int previousHeight = _height;
            _height = boxHeight;

            _graphics.Clear(Color.Transparent);
            using (var path = RoundedRect(new Rectangle(boxX, 0, boxWidth, boxHeight), CornerRadius))
            using (var fill = new SolidBrush(Color.FromArgb(255, 252, 252, 252)))
            using (var border = new Pen(Color.FromArgb(34, 0, 0, 0), 1f))
            {
                _graphics.FillPath(fill, path);
                _graphics.DrawPath(border, path);
            }

            if (dot != BubbleDot.None)
            {
                int dotX = boxX + Margin;
                int dotY = (boxHeight - DotSize) / 2;
                Color color = dot == BubbleDot.Waiting ? DotWaiting : DotDone;
                using (var brush = new SolidBrush(color))
                    _graphics.FillEllipse(brush, dotX, dotY, DotSize, DotSize);
            }

            var textRect = new RectangleF(boxX + textLeft, 8, maxTextWidth, boxHeight - 12);
            using (var ink = new SolidBrush(Color.FromArgb(255, 26, 26, 26)))
                _graphics.DrawString(text, font, ink, textRect, format);

            if (boxHeight != previousHeight || _surface == null)
            {
                if (_surface != null) _surface.Dispose();
                _surface = new Native.LayeredSurface(Width, _height);
                Native.SetWindowPos(_hwnd, (IntPtr)(-1), 0, 0, Width, _height,
                    Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            }

            BitmapData data = _bitmap.LockBits(new Rectangle(0, 0, Width, _height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { _surface.UploadRows(data.Scan0, data.Stride, Width * 4, _height); }
            finally { _bitmap.UnlockBits(data); }
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

        /// <summary>
        /// The system UI face at its standard size. It was enlarged once to answer a
        /// "text is a bit small" note, which overshot: the bubble then dominated the pet.
        /// </summary>
        private static Font _font;
        private static Font BubbleFont()
        {
            if (_font == null) _font = SystemFonts.MessageBoxFont;
            return _font;
        }

        // ——— placement and dragging ————————————————————————————————

        /// <summary>
        /// Raises the bubble to the top of the topmost band.
        ///
        /// Both the pet and the bubble are topmost, and Windows orders windows *within* that
        /// band by whoever was raised last. The pet is raised whenever it is dragged or
        /// resized, which would otherwise put it in front of its own bubble — visible as
        /// soon as the bubble is dragged down over the pet's body.
        /// </summary>
        internal void BringToTop()
        {
            if (_hwnd == IntPtr.Zero || !_visible) return;
            Native.SetWindowPos(_hwnd, (IntPtr)(-1), 0, 0, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
        }

        /// <summary>Moves the window to its anchor plus the current offset.</summary>
        private void Reposition()
        {
            if (_hwnd == IntPtr.Zero || !_visible) return;
            int left = _hasPet ? _pet.Left + ((_pet.Right - _pet.Left) - Width) / 2 : 0;
            int top = RestTop() + OffsetY;
            // Stay above the pet: it is raised on every drag, and the bubble has to follow.
            BringToTop();
            // UpdateLayeredWindow both moves and repaints; a separate SetWindowPos is not
            // needed and, when the size is left to it, can disagree with the DIB.
            _surface.Present(_hwnd, left, top);
        }

        private static int CursorY()
        {
            Native.POINT cursor;
            return Native.GetCursorPos(out cursor) ? cursor.Y : 0;
        }

        private void BeginDrag(int screenY)
        {
            _dragging = true;
            _dragMoved = false;
            _dragOriginY = screenY;
            _dragOriginOffset = OffsetY;
        }

        private void DragTo(int screenY)
        {
            if (!_dragging) return;
            int delta = screenY - _dragOriginY;
            if (Math.Abs(delta) >= 3) _dragMoved = true;
            if (!_dragMoved) return;
            int offset = _dragOriginOffset + delta;
            int down, up;
            TravelLimits(out up, out down);
            if (offset < -up) offset = -up;
            if (offset > down) offset = down;
            if (offset == OffsetY) return;
            OffsetY = offset;
            Reposition();
        }

        private void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;
            if (_dragMoved && _offsetChanged != null) _offsetChanged();
        }

        /// <summary>
        /// The bubble handles its own mouse input, because it is a separate window: a drag
        /// here adjusts <see cref="OffsetY"/>, which is the only state that moves it.
        /// </summary>
        private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_MOUSEACTIVATE:
                    // Never steal focus from the window the user is working in.
                    return (IntPtr)Native.MA_NOACTIVATE;
                case Native.WM_LBUTTONDOWN:
                    if (_current != null)
                    {
                        _current.BeginDrag(CursorY());
                        Native.SetCapture(hwnd);
                    }
                    return IntPtr.Zero;
                case Native.WM_MOUSEMOVE:
                    if (_current != null) _current.DragTo(CursorY());
                    return IntPtr.Zero;
                case Native.WM_LBUTTONUP:
                    if (_current != null) _current.EndDrag();
                    Native.ReleaseCapture();
                    return IntPtr.Zero;
                case Native.WM_CAPTURECHANGED:
                    if (_current != null) _current.EndDrag();
                    return IntPtr.Zero;
                case Native.WM_SETCURSOR:
                    if (((long)lParam & 0xFFFF) == Native.HTCLIENT)
                    {
                        Native.SetCursor(Native.LoadCursor(IntPtr.Zero, (IntPtr)32649));
                        return (IntPtr)1;
                    }
                    break;
            }
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                Native.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
            if (_surface != null) { _surface.Dispose(); _surface = null; }
            _graphics.Dispose();
            _bitmap.Dispose();
            _current = null;
        }
    }
}
