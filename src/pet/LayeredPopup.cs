// Shared base for the two small owner-drawn layered windows: the speech bubble and the
// right-click menu.
//
// Both used to repeat the same four things: register a window class, create a
// WS_EX_LAYERED|WS_EX_NOACTIVATE|WS_EX_TOPMOST popup, keep a pre-multiplied BGRA bitmap,
// and upload it through UpdateLayeredWindow. That is one implementation now, which means
// one place to fix and — the point of this refactor — one place that knows how to paint
// for a dark desktop.
//
// The theme is read once per process rather than per paint. Both surfaces are redrawn on
// user action (a hover, a new message), not on a frame clock, so a cached flag is both
// cheaper and sufficient; Windows sends WM_SETTINGCHANGE and the app picks the new scheme
// up on its next start, which is the same latency the rest of this pet has.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace DshLitePet
{
    /// <summary>Light/dark colours for a self-drawn surface.</summary>
    internal sealed class SurfaceTheme
    {
        internal Color Surface;
        internal Color Border;
        internal Color Text;
        internal Color TextMuted;
        internal Color Hover;
        internal Color Separator;

        internal static readonly SurfaceTheme Light = new SurfaceTheme
        {
            Surface = Color.FromArgb(255, 251, 251, 252),
            Border = Color.FromArgb(30, 0, 0, 0),
            Text = Color.FromArgb(255, 28, 28, 30),
            TextMuted = Color.FromArgb(255, 128, 130, 136),
            Hover = Color.FromArgb(255, 237, 239, 246),
            Separator = Color.FromArgb(24, 0, 0, 0),
        };

        internal static readonly SurfaceTheme Dark = new SurfaceTheme
        {
            Surface = Color.FromArgb(255, 43, 45, 50),
            Border = Color.FromArgb(38, 255, 255, 255),
            Text = Color.FromArgb(255, 235, 237, 242),
            TextMuted = Color.FromArgb(255, 150, 154, 162),
            Hover = Color.FromArgb(255, 59, 62, 70),
            Separator = Color.FromArgb(28, 255, 255, 255),
        };

        private static bool? _dark;

        /// <summary>
        /// True when Windows is set to a dark app theme. Read once and cached: the value
        /// only changes when the user changes their Windows theme, and this surfacing is
        /// repainted on interaction, not on a clock.
        /// </summary>
        internal static bool IsDark()
        {
            if (_dark.HasValue) return _dark.Value;
            bool dark = false;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    if (value is int) dark = (int)value == 0;
                }
            }
            catch
            {
                /* an unreadable key means "light" */
            }
            _dark = dark;
            return dark;
        }

        internal static SurfaceTheme Current()
        {
            return IsDark() ? Dark : Light;
        }
    }

    /// <summary>
    /// A self-drawn layered popup: antialiased GDI+ content uploaded as one premultiplied
    /// BGRA blit. Subclasses supply the painting and the message handling.
    /// </summary>
    internal abstract class LayeredPopup : IDisposable
    {
        // Keyed by class name: the bubble and the menu are two DIFFERENT window classes,
        // and one shared flag meant the second class was never registered at all.
        private static readonly System.Collections.Generic.HashSet<string> RegisteredClasses =
            new System.Collections.Generic.HashSet<string>();
        private static readonly Native.WndProcDelegate Thunk = Dispatch;
        private static readonly System.Collections.Generic.Dictionary<IntPtr, LayeredPopup> Live =
            new System.Collections.Generic.Dictionary<IntPtr, LayeredPopup>();

        protected IntPtr Hwnd;
        protected Native.LayeredSurface Surface;
        protected Bitmap Bitmap;
        protected Graphics Graphics;
        protected int SurfaceWidth;
        protected int SurfaceHeight;

        protected abstract string WindowClass { get; }
        protected abstract string WindowTitle { get; }
        protected abstract IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Installed by the pet so that every "raise me" goes through ONE place that knows
        /// the whole stack (pet ▸ bubble ▸ menu).
        ///
        /// Raising a single window cannot express a stacking order. Within the topmost band
        /// Windows orders windows by whichever was raised last, so the pet being raised on a
        /// drag buried a bubble that had been raised earlier, and the bubble repainting
        /// while the menu was open buried the menu. Routing every raise through the owner
        /// is what makes the order an invariant instead of a race.
        /// </summary>
        internal static Action RestackRequest;

        /// <summary>
        /// Raises this window, asking the owner to restack the whole band when it can.
        /// Falls back to a plain raise so the surfaces still work on their own.
        /// </summary>
        protected void Raise()
        {
            Action restack = RestackRequest;
            if (restack != null) { restack(); return; }
            RaiseRaw();
        }

        /// <summary>Raises this window without consulting the stack. Only the owner calls this.</summary>
        internal void RaiseRaw()
        {
            if (Hwnd == IntPtr.Zero) return;
            Native.SetWindowPos(Hwnd, (IntPtr)Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
        }

        /// <summary>Registers the class once per process and creates the window, hidden.</summary>
        protected void CreateWindow(int width, int height)
        {
            IntPtr instance = Native.GetModuleHandle(null);
            if (!RegisteredClasses.Contains(WindowClass))
            {
                var wc = new Native.WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX)),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Thunk),
                    hInstance = instance,
                    lpszClassName = WindowClass,
                    hCursor = Native.LoadCursor(IntPtr.Zero, (IntPtr)32512),
                };
                Native.RegisterClassEx(ref wc);
                RegisteredClasses.Add(WindowClass);
            }
            Hwnd = Native.CreateWindowEx(
                Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
                WindowClass, WindowTitle, Native.WS_POPUP,
                0, 0, width, height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            lock (Live) Live[Hwnd] = this;
        }

        private static IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            LayeredPopup self;
            lock (Live)
            {
                if (!Live.TryGetValue(hwnd, out self)) self = null;
            }
            if (self == null) return Native.DefWindowProc(hwnd, msg, wParam, lParam);
            return self.OnMessage(hwnd, msg, wParam, lParam);
        }

        /// <summary>
        /// Ensures the bitmap, Graphics and DIB match the given size. Reusing them across
        /// repaints is what keeps an open menu allocation-free.
        /// </summary>
        protected void EnsureSurface(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            if (Surface != null && width == SurfaceWidth && height == SurfaceHeight) return;

            SurfaceWidth = width;
            SurfaceHeight = height;
            if (Surface != null) { Surface.Dispose(); Surface = null; }
            if (Graphics != null) { Graphics.Dispose(); Graphics = null; }
            if (Bitmap != null) { Bitmap.Dispose(); Bitmap = null; }

            Surface = new Native.LayeredSurface(width, height);
            Bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            Graphics = System.Drawing.Graphics.FromImage(Bitmap);
            Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        }

        /// <summary>Uploads whatever is on the bitmap and places the window at (x,y).</summary>
        protected void Present(int x, int y)
        {
            if (Surface == null || Bitmap == null) return;
            BitmapData data = Bitmap.LockBits(new Rectangle(0, 0, SurfaceWidth, SurfaceHeight),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { Surface.UploadRows(data.Scan0, data.Stride, SurfaceWidth * 4, SurfaceHeight); }
            finally { Bitmap.UnlockBits(data); }
            Surface.Present(Hwnd, x, y);
        }

        /// <summary>
        /// Uploads only a horizontal band of the bitmap and re-presents the window.
        ///
        /// UpdateLayeredWindow always takes a whole surface, so the band is copied into a
        /// small staging surface of the same width and presented at the band's offset. That
        /// keeps a hover repaint proportional to the two rows that changed rather than to
        /// the size of the whole menu.
        /// </summary>
        protected void PresentBand(int windowX, int windowY, Rectangle band)
        {
            if (Bitmap == null || band.Width <= 0 || band.Height <= 0) return;
            if (BandSurface == null || BandSurface.Width != band.Width || BandSurface.Height != band.Height)
            {
                if (BandSurface != null) BandSurface.Dispose();
                BandSurface = new Native.LayeredSurface(band.Width, band.Height);
            }
            BitmapData data = Bitmap.LockBits(band, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try { BandSurface.UploadRows(data.Scan0, data.Stride, band.Width * 4, band.Height); }
            finally { Bitmap.UnlockBits(data); }
            BandSurface.Present(Hwnd, windowX, windowY + band.Top);
        }

        /// <summary>Reuse buffer for <see cref="PresentBand"/>; resized only when the band changes.</summary>
        protected Native.LayeredSurface BandSurface;

        /// <summary>Rounded rectangle path, with the radius clamped to the box.</summary>
        protected static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Fills and outlines a rounded panel in the current theme's colours.</summary>
        protected void DrawPanel(Rectangle box, int radius, SurfaceTheme theme)
        {
            using (var path = RoundedRect(box, radius))
            using (var fill = new SolidBrush(theme.Surface))
            using (var border = new Pen(theme.Border, 1f))
            {
                Graphics.FillPath(fill, path);
                Graphics.DrawPath(border, path);
            }
        }

        public void Dispose()
        {
            if (BandSurface != null) { BandSurface.Dispose(); BandSurface = null; }
            if (Hwnd != IntPtr.Zero)
            {
                lock (Live) Live.Remove(Hwnd);
                Native.DestroyWindow(Hwnd);
                Hwnd = IntPtr.Zero;
            }
            if (Surface != null) { Surface.Dispose(); Surface = null; }
            if (Graphics != null) { Graphics.Dispose(); Graphics = null; }
            if (Bitmap != null) { Bitmap.Dispose(); Bitmap = null; }
        }
    }
}
