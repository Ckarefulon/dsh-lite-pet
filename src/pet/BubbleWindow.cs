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
    internal sealed class BubbleWindow : LayeredPopup
    {
        private const string BubbleClass = "DshLitePetBubble";

        protected override string WindowClass { get { return BubbleClass; } }
        protected override string WindowTitle { get { return "dsh-lite-pet-bubble"; } }

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
        private static readonly Color DotError = Color.FromArgb(255, 231, 76, 60);


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
            // No bitmap here: EnsureSurface allocates on the first paint, so
            // constructing the bubble costs nothing until there is text to show.
            _current = this;
        }

        internal IntPtr Handle { get { return Hwnd; } }

        internal bool Visible { get { return _visible; } }

        /// <summary>Called when a finished drag changes the offset, so it can be persisted.</summary>
        internal void OnOffsetChanged(Action callback) { _offsetChanged = callback; }

        /// <summary>
        /// Creates the window; it starts hidden and is shown once there is text.
        ///
        /// <paramref name="owner"/> is the pet's window. It is what makes the bubble an OWNED
        /// window, and Windows then keeps it above the pet on its own: the pet is raised on
        /// every drag tick, and each of those raises used to put the pet in front of its own
        /// bubble until the next restack — observed as the bubble blinking up and down through
        /// the pet while dragging. An owned window is never demoted below its owner, so there is
        /// no periodic repair to lose a race against.
        /// </summary>
        internal void Create(IntPtr owner)
        {
            // The window class, the window and the surface all come from the shared
            // base; the bubble only supplies its class name and its painting.
            CreateWindow(Width, _height, owner);
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

        private bool _bubbleVisibleSafe() { return _visible && Hwnd != IntPtr.Zero; }

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
            if (Hwnd == IntPtr.Zero) return;
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
                Native.ShowWindow(Hwnd, Native.SW_SHOWNOACTIVATE);
            }
            Reposition();
            // The bubble is an owned window of the pet, so Windows keeps it above the pet by
            // itself; the only ordering that still needs stating is against its SIBLING — the
            // menu — which the pet places. A height change resizes the window through
            // UpdateLayeredWindow, and UpdateLayeredWindow does not touch the z-order (measured),
            // so no repair raise belongs here.
            if (_visible) BringToTop();
        }

        internal void Hide()
        {
            if (!_visible) return;
            _visible = false;
            Native.ShowWindow(Hwnd, Native.SW_HIDE);
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
            if (Hwnd != IntPtr.Zero) Native.GetWindowRect(Hwnd, ref rect);
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
            using (var format = new StringFormat(StringFormatFlags.NoWrap))
            {
                format.Trimming = StringTrimming.EllipsisCharacter;

                // Measure before any surface exists: the base allocates lazily, so a
                // throwaway text context is used for this step.
                SizeF measured;
                using (var measure = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                    measured = measure.MeasureString(text, font, new SizeF(maxTextWidth, 400), format);
                int lines = Math.Max(1, (int)Math.Ceiling(measured.Height / font.GetHeight()));
                if (lines > MaxLines) measured.Height = font.GetHeight() * MaxLines + 2;
                int boxHeight = (int)Math.Ceiling(measured.Height) + 16;
                if (boxHeight > MaxHeight) boxHeight = MaxHeight;
                if (boxHeight < DefaultHeight) boxHeight = DefaultHeight;
                int boxWidth = (int)Math.Ceiling(measured.Width) + textLeft + 12;
                if (boxWidth < 96) boxWidth = 96;
                if (boxWidth > Width) boxWidth = Width;
                int boxX = (Width - boxWidth) / 2;
                _height = boxHeight;

                EnsureSurface(Width, _height);

                // Dark desktops get a dark bubble; the theme is shared with the menu and
                // cached in the base, so this costs nothing per paint.
                SurfaceTheme theme = SurfaceTheme.Current();
                Graphics.Clear(Color.Transparent);
                DrawPanel(new Rectangle(boxX, 0, boxWidth, boxHeight), CornerRadius, theme);

                if (dot != BubbleDot.None)
                {
                    int dotX = boxX + Margin;
                    int dotY = (boxHeight - DotSize) / 2;
                    Color color = dot == BubbleDot.Waiting ? DotWaiting
                        : dot == BubbleDot.Error ? DotError
                        : DotDone;
                    using (var brush = new SolidBrush(color))
                        Graphics.FillEllipse(brush, dotX, dotY, DotSize, DotSize);
                }

                var textRect = new RectangleF(boxX + textLeft, 8, maxTextWidth, boxHeight - 12);
                using (var ink = new SolidBrush(theme.Text))
                    Graphics.DrawString(text, font, ink, textRect, format);
            }

            Present(_bubbleX, _bubbleY);
            // A resize can reorder the topmost band, so "just in front of the pet" is stated
            // again after the surface changes. Only when the bubble is on screen: raising a
            // hidden window is pointless work and would reorder the band for nothing.
            if (_visible) BringToTop();
        }

        /// <summary>Screen position last used, so Paint can upload without recomputing it.</summary>
        private int _bubbleX;
        private int _bubbleY;


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
        /// Puts the bubble directly in front of the pet.
        ///
        /// Ownership already guarantees "above the pet"; this adds "and not above its own
        /// menu", which is the one relationship ownership cannot express. It deliberately does
        /// NOT send the bubble to the front of the topmost band: a front-of-band raise is what
        /// the old code did on every tick, and it is what let the menu and the bubble trade
        /// places under the cursor.
        ///
        /// This routes through <see cref="PlaceInFront"/>, so the pet decides the spot in one
        /// call (pet directly behind the bubble) and can keep the menu above both.
        /// </summary>
        internal void BringToTop()
        {
            if (Hwnd == IntPtr.Zero || !_visible) return;
            PlaceInFront();
        }

        /// <summary>
        /// True while the bubble has text on screen. The pet uses it to skip work during a
        /// drag: there is nothing to keep in order when the bubble is hidden.
        /// </summary>
        internal bool OnScreen { get { return _visible && Hwnd != IntPtr.Zero; } }

        /// <summary>Moves the window to its anchor plus the current offset.</summary>
        private void Reposition()
        {
            if (Hwnd == IntPtr.Zero || !_visible) return;
            int left = _hasPet ? _pet.Left + ((_pet.Right - _pet.Left) - Width) / 2 : 0;
            int top = RestTop() + OffsetY;
            // Stay in front of the pet. Ownership already guarantees it; stating it here costs
            // one relative SetWindowPos and keeps the invariant visible at the place that moves
            // the window, rather than relying on a restack that runs after the fact.
            BringToTop();
            // UpdateLayeredWindow both moves and repaints; a separate SetWindowPos is not
            // needed and, when the size is left to it, can disagree with the DIB.
            _bubbleX = left;
            _bubbleY = top;
            Present(left, top);
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
        protected override IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
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

        /// <summary>The base disposes the window and the drawing surface.</summary>
        public void Dispose()
        {
            base.Dispose();
            _current = null;
        }
    }
}
