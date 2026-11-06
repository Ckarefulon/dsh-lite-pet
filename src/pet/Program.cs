// dsh-lite-pet desktop helper: one layered, click-through window that plays
// H.264 color + alpha tracks through Media Foundation and draws its own bubble.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace DshLitePet
{
    internal static class Program
    {
        private const int TimerId = 1;
        private const int TimerPeriodMs = 12;
        private const int WindowMargin = 24;
        private const int DragThreshold = 3;
        private const string WindowClass = "DshLitePetWindow";

        private static Catalog _catalog;
        private static PetSettings _settings;
        private static PetCanvas _canvas;
        private static Native.LayeredSurface _surface;
        private static PetMenu _menu;
        private static Ipc _ipc;
        private static AnimationClip _clip;
        private static IntPtr _hwnd;
        private static Native.WndProcDelegate _wndProc; // keeps the thunk alive
        private static readonly Stopwatch FrameClock = new Stopwatch();
        private static readonly Stopwatch Uptime = Stopwatch.StartNew();
        private static readonly Random Rng = new Random();

        private static int _frameIndex;
        private static double _frameMs = 1000.0 / 24.0;
        private static bool _oneShot;
        private static string _mode = "idle";
        private static string _bubbleText;
        private static BubbleDot _bubbleDot;
        private static bool _bubbleDirty = true;
        private static long _bubbleHideAt = -1;
        private static long _nextActionAt;
        private static string _currentKind = "";
        private static double _scale = 1.0;
        private static int _windowX;
        private static int _windowY;
        private static int _windowWidth;
        private static int _windowHeight;
        private static Bitmap _frameBitmap;
        private static Bitmap _scaledBitmap;

        private static bool _dragging;
        private static bool _dragPet;
        private static bool _dragMoved;
        private static long _lastHitCheck;
        private static long _dragStartAt;
        private static int _dragStartX;
        private static int _dragStartY;
        private static int _dragStartOffset;
        private static int _dragStartWindowX;
        private static int _dragStartWindowY;
        private static IntPtr _handCursor;
        private static byte[] _fadeSnapshot;
        private static long _fadeStart = -1;
        private const long FadeMs = 260;

        private static void Main(string[] args)
        {
            string mediaDir = null;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--media" && i + 1 < args.Length) mediaDir = args[i + 1];
            if (mediaDir == null)
            {
                Console.Error.WriteLine("dsh-lite-pet: --media <dir> is required");
                Environment.Exit(2);
            }

            SetProcessDPIAware();
            Mf.Start();
            try
            {
                _settings = PetSettings.Load();
                _scale = _settings.Scale;
                _catalog = Catalog.Load(mediaDir);
                _canvas = new PetCanvas(_catalog.Width, _catalog.Height);
                _canvas.BubbleOffsetY = _settings.BubbleOffsetY;
                _menu = new PetMenu(_catalog);
                _ipc = new Ipc();
                _ipc.Start();

                CreateWindow();
                _nextActionAt = 15000 + Rng.Next(20000);
                PlayKind("idle", true, 0);

                _ipc.Send("{\"t\":\"ready\"}");
                Native.SetTimer(_hwnd, (IntPtr)TimerId, TimerPeriodMs, IntPtr.Zero);
                RunLoop();
            }
            finally
            {
                if (_clip != null) _clip.Dispose();
                if (_surface != null) _surface.Dispose();
                if (_frameBitmap != null) _frameBitmap.Dispose();
                if (_scaledBitmap != null) _scaledBitmap.Dispose();
                if (_canvas != null) _canvas.Dispose();
                if (_ipc != null) _ipc.Dispose();
                Mf.Stop();
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        // ——— window ————————————————————————————————————————————————

        private static void CreateWindow()
        {
            IntPtr instance = Native.GetModuleHandle(null);
            _wndProc = WndProc;
            var wc = new Native.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX)),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                lpszClassName = WindowClass,
                hCursor = Native.LoadCursor(IntPtr.Zero, (IntPtr)32512),
            };
            _handCursor = Native.LoadCursor(IntPtr.Zero, (IntPtr)32649);
            Native.RegisterClassEx(ref wc);

            MeasureWindow();
            Native.RECT work = new Native.RECT();
            Native.SystemParametersInfo(Native.SPI_GETWORKAREA, 0, ref work, 0);
            if (_settings.WindowX != int.MinValue && _settings.WindowY != int.MinValue)
            {
                _windowX = _settings.WindowX;
                _windowY = _settings.WindowY;
            }
            else
            {
                _windowX = work.Right - _windowWidth - WindowMargin;
                _windowY = work.Bottom - _windowHeight - WindowMargin;
            }
            ClampToWorkArea(work);

            _hwnd = Native.CreateWindowEx(
                Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
                WindowClass, "dsh-lite-pet", Native.WS_POPUP,
                _windowX, _windowY, _windowWidth, _windowHeight,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            Native.SetWindowPos(_hwnd, (IntPtr)(-1), 0, 0, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
        }

        /// <summary>Keeps at least a sliver of the window inside the work area (a saved position can go stale).</summary>
        private static void ClampToWorkArea(Native.RECT work)
        {
            int minVisible = 60;
            if (_windowX + _windowWidth < work.Left + minVisible) _windowX = work.Left;
            if (_windowX > work.Right - minVisible) _windowX = work.Right - minVisible;
            if (_windowY + _windowHeight < work.Top + minVisible) _windowY = work.Top;
            if (_windowY > work.Bottom - minVisible) _windowY = work.Bottom - minVisible;
        }

        /// <summary>Sizes the window, the DIB and the scaling intermediates for the current scale.</summary>
        private static void MeasureWindow()
        {
            _windowWidth = (int)Math.Round(_canvas.Width * _scale);
            _windowHeight = (int)Math.Round(_canvas.Height * _scale);
            if (_surface != null) _surface.Dispose();
            _surface = new Native.LayeredSurface(_windowWidth, _windowHeight);
            if (_scaledBitmap != null) _scaledBitmap.Dispose();
            if (_frameBitmap != null) _frameBitmap.Dispose();
            _frameBitmap = null;
            _scaledBitmap = null;
            if (_scale != 1.0)
            {
                _frameBitmap = new Bitmap(_canvas.Width, _canvas.Height, PixelFormat.Format32bppPArgb);
                _scaledBitmap = new Bitmap(_windowWidth, _windowHeight, PixelFormat.Format32bppPArgb);
            }
        }

        private static void ApplyScale(double scale)
        {
            if (Math.Abs(scale - _scale) < 0.001) return;
            _scale = scale;
            _settings.Scale = scale;
            int oldWidth = _windowWidth;
            int oldHeight = _windowHeight;
            MeasureWindow();
            // Keep the bottom-right corner (where the pet's feet are) in place.
            _windowX += oldWidth - _windowWidth;
            _windowY += oldHeight - _windowHeight;
            Native.RECT work = new Native.RECT();
            Native.SystemParametersInfo(Native.SPI_GETWORKAREA, 0, ref work, 0);
            ClampToWorkArea(work);
            _settings.WindowX = _windowX;
            _settings.WindowY = _windowY;
            _settings.Save();
            Native.SetWindowPos(_hwnd, (IntPtr)(-1), _windowX, _windowY, _windowWidth, _windowHeight,
                Native.SWP_NOACTIVATE);
            _bubbleDirty = true;
            Present();
        }

        private static void RunLoop()
        {
            while (true)
            {
                Native.MSG msg;
                if (!Native.GetMessage(out msg, IntPtr.Zero, 0, 0)) break;
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }

        // ——— messages ————————————————————————————————————————————————

        private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_TIMER:
                    Tick();
                    return IntPtr.Zero;
                case Native.WM_NCHITTEST:
                    return (IntPtr)HitTest(lParam);
                case Native.WM_SETCURSOR:
                    if (((long)lParam & 0xFFFF) == Native.HTCLIENT)
                    {
                        Native.SetCursor(_handCursor);
                        return (IntPtr)1;
                    }
                    return Native.DefWindowProc(hwnd, msg, wParam, lParam);
                case Native.WM_LBUTTONDOWN:
                    OnLeftDown(lParam);
                    return IntPtr.Zero;
                case Native.WM_MOUSEMOVE:
                    OnMouseMove(lParam);
                    return IntPtr.Zero;
                case Native.WM_LBUTTONUP:
                    OnLeftUp(lParam);
                    return IntPtr.Zero;
                case Native.WM_RBUTTONUP:
                    OpenMenu();
                    return IntPtr.Zero;
                case Native.WM_CAPTURECHANGED:
                    _dragging = false;
                    return IntPtr.Zero;
                case Native.WM_DESTROY:
                    Native.KillTimer(hwnd, (IntPtr)TimerId);
                    Native.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        /// <summary>Mouse hit testing: HTTRANSPARENT lets clicks fall through to whatever is underneath.</summary>
        private static int HitTest(IntPtr lParam)
        {
            int screenX = unchecked((short)((long)lParam & 0xFFFF));
            int screenY = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
            var local = new Native.POINT { X = screenX, Y = screenY };
            Native.ScreenToClient(_hwnd, ref local);
            int x = (int)Math.Round(local.X / _scale);
            int y = (int)Math.Round(local.Y / _scale);
            return _canvas != null && _canvas.HitTest(x, y) ? Native.HTCLIENT : Native.HTTRANSPARENT;
        }

        /// <summary>
        /// Windows treats every pixel with alpha &gt; 0 as solid for a layered window, so
        /// WM_NCHITTEST alone cannot open the eroded band. Poll the cursor and drop
        /// WS_EX_TRANSPARENT on the pet / add it everywhere else.
        /// </summary>
        private static void UpdateHitState()
        {
            if (_canvas == null || _hwnd == IntPtr.Zero) return;
            long now = Uptime.ElapsedMilliseconds;
            if (_dragging)
            {
                // Backstop against a lost WM_LBUTTONUP; a capture change also ends the drag.
                if (now - _dragStartAt > 10000)
                {
                    _dragging = false;
                    Native.ReleaseCapture();
                }
                return;
            }
            if (now - _lastHitCheck < 30) return;
            _lastHitCheck = now;
            Native.POINT cursor;
            if (!Native.GetCursorPos(out cursor)) return;
            var local = new Native.POINT { X = cursor.X, Y = cursor.Y };
            Native.ScreenToClient(_hwnd, ref local);
            int x = (int)Math.Round(local.X / _scale);
            int y = (int)Math.Round(local.Y / _scale);
            bool hit = _canvas.HitTest(x, y);
            // The window's real style is the source of truth; a cached flag can drift.
            int style = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            bool transparent = (style & Native.WS_EX_TRANSPARENT) != 0;
            if (hit != transparent) return;
            int next = hit ? (style & ~Native.WS_EX_TRANSPARENT) : (style | Native.WS_EX_TRANSPARENT);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, next);
        }

        private static void OnLeftDown(IntPtr lParam)
        {
            int x = unchecked((short)((long)lParam & 0xFFFF));
            int y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
            int localX = (int)Math.Round(x / _scale);
            int localY = (int)Math.Round(y / _scale);
            Native.POINT cursor;
            Native.GetCursorPos(out cursor);
            if (_canvas.HitTestBubble(localX, localY))
            {
                _dragging = true;
                _dragPet = false;
                _dragMoved = false;
                _dragStartAt = Uptime.ElapsedMilliseconds;
                _dragStartY = cursor.Y;
                _dragStartOffset = _canvas.BubbleOffsetY;
                Native.SetCapture(_hwnd);
                return;
            }
            if (_canvas.HitTest(localX, localY))
            {
                _dragging = true;
                _dragPet = true;
                _dragMoved = false;
                _dragStartAt = Uptime.ElapsedMilliseconds;
                _dragStartX = cursor.X;
                _dragStartY = cursor.Y;
                _dragStartWindowX = _windowX;
                _dragStartWindowY = _windowY;
                Native.SetCapture(_hwnd);
            }
        }

        private static void OnMouseMove(IntPtr lParam)
        {
            UpdateDrag();
        }

        /// <summary>
        /// Drags are polled from the timer as well as driven by WM_MOUSEMOVE: a programmatic
        /// cursor move (and some pointer devices) delivers no move messages, which used to
        /// make the pet impossible to drag.
        /// </summary>
        private static void UpdateDrag()
        {
            if (!_dragging) return;
            Native.POINT cursor;
            if (!Native.GetCursorPos(out cursor)) return;
            if (_dragPet)
            {
                int dx = cursor.X - _dragStartX;
                int dy = cursor.Y - _dragStartY;
                if (Math.Abs(dx) >= DragThreshold || Math.Abs(dy) >= DragThreshold) _dragMoved = true;
                if (!_dragMoved) return;
                int nx = _dragStartWindowX + dx;
                int ny = _dragStartWindowY + dy;
                if (nx == _windowX && ny == _windowY) return;
                _windowX = nx;
                _windowY = ny;
                Native.SetWindowPos(_hwnd, (IntPtr)(-1), _windowX, _windowY, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                return;
            }
            int delta = (int)Math.Round((cursor.Y - _dragStartY) / _scale);
            if (Math.Abs(delta) >= DragThreshold) _dragMoved = true;
            int offset = _dragStartOffset + delta;
            int min = -(PetCanvas.BubbleArea - 12);
            int max = PetCanvas.BubbleOverlapLimit;
            if (offset < min) offset = min;
            if (offset > max) offset = max;
            if (offset == _canvas.BubbleOffsetY) return;
            _canvas.BubbleOffsetY = offset;
            _bubbleDirty = true;
            Present();
        }

        private static void OnLeftUp(IntPtr lParam)
        {
            if (_dragging)
            {
                _dragging = false;
                Native.ReleaseCapture();
                if (_dragMoved)
                {
                    if (_dragPet)
                    {
                        _settings.WindowX = _windowX;
                        _settings.WindowY = _windowY;
                    }
                    else
                    {
                        _settings.BubbleOffsetY = _canvas.BubbleOffsetY;
                    }
                    _settings.Save();
                    return;
                }
            }
            int x = unchecked((short)((long)lParam & 0xFFFF));
            int y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
            int localX = (int)Math.Round(x / _scale);
            int localY = (int)Math.Round(y / _scale);
            if (_canvas.HitTest(localX, localY)) _ipc.Send("{\"t\":\"open\"}");
        }

        private static void OpenMenu()
        {
            CatalogEntry chosen;
            double chosenScale;
            int command = _menu.Show(_hwnd, _scale, out chosen, out chosenScale);
            if (command == PetMenu.CommandQuit) { Quit(); return; }
            if (command == PetMenu.CommandBalance) { _ipc.Send("{\"t\":\"balance\"}"); return; }
            if (chosenScale > 0) { ApplyScale(chosenScale); return; }
            if (chosen != null) PlayClip(chosen, false);
        }

        private static void Quit()
        {
            _ipc.Send("{\"t\":\"closed\"}");
            Native.DestroyWindow(_hwnd);
        }

        // ——— rendering ————————————————————————————————————————————

        private static void Tick()
        {
            if (_ipc.Closed) { Quit(); return; }

            string line;
            while (_ipc.TryRead(out line)) HandleCommand(line);

            UpdateHitState();
            UpdateDrag();

            long now = Uptime.ElapsedMilliseconds;
            if (_bubbleHideAt >= 0 && now >= _bubbleHideAt)
            {
                _bubbleHideAt = -1;
                SetBubble(null, BubbleDot.None);
            }

            if (_clip == null) { PlayKind("idle", true, 0); return; }

            if (FrameClock.Elapsed.TotalMilliseconds >= _frameIndex * _frameMs)
            {
                if (!_clip.TryReadFrame())
                {
                    OnClipEnd(now);
                    return;
                }
                _frameIndex++;
                Present();
            }
            else if (_bubbleDirty)
            {
                Present();
            }
        }

        private static void Present()
        {
            // Recompose from the last decoded frame every time: a bubble-only repaint
            // must not leave the previous bubble behind.
            if (_clip != null)
                _canvas.ComposePet(_clip.Frame, _clip.Width, _clip.BodyHeight);
            if (_fadeStart >= 0 && _fadeSnapshot != null)
            {
                long elapsed = Uptime.ElapsedMilliseconds - _fadeStart;
                if (elapsed >= FadeMs)
                {
                    _fadeStart = -1;
                }
                else
                {
                    _canvas.BlendFrom(_fadeSnapshot, elapsed / (double)FadeMs);
                }
            }
            _canvas.DrawBubble(_bubbleText, _bubbleDot);
            _bubbleDirty = false;
            if (_scale == 1.0)
            {
                _surface.Upload(_canvas.Pixels);
            }
            else
            {
                UploadScaled();
            }
            _surface.Present(_hwnd, _windowX, _windowY);
        }

        private static void UploadScaled()
        {
            BitmapData frame = _frameBitmap.LockBits(
                new Rectangle(0, 0, _canvas.Width, _canvas.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < _canvas.Height; y++)
                    Marshal.Copy(_canvas.Pixels, y * _canvas.Width * 4, frame.Scan0 + y * frame.Stride, _canvas.Width * 4);
            }
            finally
            {
                _frameBitmap.UnlockBits(frame);
            }
            using (Graphics g = Graphics.FromImage(_scaledBitmap))
            {
                // SourceCopy replaces the destination pixels (alpha included). Without this
                // each scaled frame would be blended over the previous one and the animation
                // would trail.
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(_frameBitmap, new Rectangle(0, 0, _windowWidth, _windowHeight));
            }
            BitmapData scaled = _scaledBitmap.LockBits(
                new Rectangle(0, 0, _windowWidth, _windowHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                _surface.UploadRows(scaled.Scan0, scaled.Stride, _windowWidth * 4, _windowHeight);
            }
            finally
            {
                _scaledBitmap.UnlockBits(scaled);
            }
        }

        private static void SetBubble(string text, BubbleDot dot)
        {
            if (text == _bubbleText && dot == _bubbleDot) return;
            _bubbleText = text;
            _bubbleDot = dot;
            _bubbleDirty = true;
        }

        // ——— playback state ————————————————————————————————————————

        private static void OnClipEnd(long now)
        {
            if (_oneShot || !_clip.Entry.IsLoopingKind())
            {
                if (_mode == "working") { PlayKind("work-busy", true, 0); return; }
                if (_mode == "waiting") { PlayKind("work-waiting", true, 0); return; }
                PlayKind("idle", true, 0);
                return;
            }
            _clip.Rewind();
            _frameIndex = 0;
            FrameClock.Restart();
            if (_currentKind == "idle" && now >= _nextActionAt)
            {
                PlayRandomAction();
                _nextActionAt = now + 20000 + Rng.Next(25000);
            }
        }

        private static void HandleCommand(string line)
        {
            Dictionary<string, object> message;
            try { message = (Dictionary<string, object>)Json.Parse(line); }
            catch (Exception) { return; }

            string type = Json.String(message, "t");
            if (type == "quit") { Quit(); return; }
            if (type != "state") return;

            _mode = Json.String(message, "mode") ?? "idle";
            string text = Normalize(Json.String(message, "text"));
            double level = Json.Number(message, "level", -1);

            if (_mode == "idle")
            {
                _bubbleHideAt = -1;
                SetBubble(null, BubbleDot.None);
                if (_currentKind != "idle") PlayKind("idle", true, 0);
                return;
            }
            if (_mode == "working")
            {
                _bubbleHideAt = -1;
                SetBubble(text, BubbleDot.None);
                if (_currentKind != "work-busy") PlayKind("work-busy", true, 0);
                return;
            }
            if (_mode == "waiting")
            {
                _bubbleHideAt = -1;
                SetBubble(text, BubbleDot.Waiting);
                if (_currentKind != "work-waiting") PlayKind("work-waiting", true, 0);
                return;
            }
            if (_mode == "done")
            {
                _bubbleHideAt = -1;
                SetBubble(text, BubbleDot.Done);
                if (_currentKind != "work-done") PlayKind("work-done", false, 0);
                return;
            }
            if (_mode == "balance")
            {
                SetBubble(text, BubbleDot.None);
                _bubbleHideAt = Uptime.ElapsedMilliseconds + 9000;
                PlayKind("balance", false, (int)level);
                return;
            }
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string trimmed = text.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static void PlayKind(string kind, bool loop, int levelIndex, bool force = false)
        {
            CatalogEntry entry = null;
            if (kind == "balance")
            {
                List<CatalogEntry> list = _catalog.ByKind("balance");
                if (list.Count > 0) entry = list[Math.Min(Math.Max(levelIndex, 0), list.Count - 1)];
            }
            else
            {
                entry = _catalog.First(kind);
            }
            if (entry == null)
            {
                if (kind != "idle") { PlayKind("idle", true, 0, force); return; }
                if (_catalog.Animations.Count == 0) return;
                entry = _catalog.Animations[0];
            }
            PlayClip(entry, loop, force);
        }

        private static void PlayRandomAction()
        {
            var pool = new List<CatalogEntry>();
            foreach (string kind in new[] { "action", "click", "whisper", "balance" })
                pool.AddRange(_catalog.ByKind(kind));
            if (pool.Count == 0) return;
            PlayClip(pool[Rng.Next(pool.Count)], false);
        }

        private static void PlayClip(CatalogEntry entry, bool loop, bool force = false)
        {
            if (_clip != null)
            {
                if (!force && _clip.Entry == entry && loop && !_oneShot) return;
                // Cross-fade from the frame that is on screen right now.
                if (_canvas.Pixels != null)
                {
                    if (_fadeSnapshot == null || _fadeSnapshot.Length != _canvas.Pixels.Length)
                        _fadeSnapshot = new byte[_canvas.Pixels.Length];
                    Buffer.BlockCopy(_canvas.Pixels, 0, _fadeSnapshot, 0, _fadeSnapshot.Length);
                    _fadeStart = Uptime.ElapsedMilliseconds;
                }
                _clip.Dispose();
                _clip = null;
            }
            try
            {
                _clip = new AnimationClip(entry);
            }
            catch (Exception)
            {
                _clip = null;
                CatalogEntry fallback = _catalog.First("idle");
                if (fallback != null && fallback != entry)
                {
                    try { _clip = new AnimationClip(fallback); } catch (Exception) { _clip = null; }
                }
                if (_clip == null) return;
            }
            _currentKind = _clip.Entry.Kind;
            _oneShot = !loop;
            _frameIndex = 0;
            _bubbleDirty = true;
            _frameMs = _clip.Entry.Fps > 0 ? 1000.0 / _clip.Entry.Fps : 1000.0 / 24.0;
            FrameClock.Restart();
        }
    }
}









