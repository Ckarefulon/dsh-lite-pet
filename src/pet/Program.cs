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
        /// <summary>
        /// Retry budget for reopening a hidden Harness window. Measured behaviour is
        /// inconsistent — the shell sometimes repaints within a few hundred milliseconds and
        /// sometimes takes seconds, and the first UI Automation call in a cold process is
        /// slow — so the two routes alternate and the click is only given up on after both
        /// have been tried twice. Every round is bounded, and the whole sequence runs off
        /// the message thread, so a slow window never stalls the pet.
        /// </summary>
        private const int OpenRounds = 4;
        private const int OpenWaitMs = 1500;
        private const int DragThreshold = 3;
        private const string WindowClass = "DshLitePetWindow";

        private static Catalog _catalog;
        private static PetSettings _settings;
        private static PetCanvas _canvas;
        private static Native.LayeredSurface _surface;
        private static PetMenu _menu;
        private static Ipc _ipc;
        private static WebpAnimation _webp;
        private static byte[] _webpFrame;
        private static int _webpTimestamp;
        private static long _frameDue;
        private static long _playBase;
        private static IntPtr _hwnd;
        private static Native.WndProcDelegate _wndProc; // keeps the thunk alive
        private static readonly Stopwatch Uptime = Stopwatch.StartNew();
        private static readonly Random Rng = new Random();

        private static bool _oneShot;
        private static string _mode = "idle";
        private static string _bubbleText;
        private static BubbleDot _bubbleDot;
        private static bool _bubbleDirty = true;
        private static long _bubbleHideAt = -1;
        private static BubbleWindow _bubble;
        private static long _nextActionAt;
        private static string _currentKind = "";
        // Ambient clips are one-shots that end by returning to idle, so the state push
        // that arrives while one is playing must not cut it off. This remembers that the
        // clip on screen is an ambient action rather than a reaction to the mode.
        private static bool _playingAmbient;
        // A shuffled bag instead of a fresh Rng.Next per pick: every clip comes up once
        // per cycle instead of leaving repeats to chance.
        private static readonly List<CatalogEntry> ActionBag = new List<CatalogEntry>();
        private static int _actionBagIndex;
        private static double _scale = 1.0;
        private static int _windowX;
        private static int _windowY;
        private static int _windowWidth;
        private static int _windowHeight;

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
        private static int[] _scaleSrcX, _scaleFracX, _scaleSrcY, _scaleFracY;
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
            try
            {
                _settings = PetSettings.Load();
                _scale = _settings.Scale;
                _catalog = Catalog.Load(mediaDir);
                _canvas = new PetCanvas(_catalog.Width, _catalog.Height);
                _bubble = new BubbleWindow();
                // A drag used to be able to leave a huge offset behind while the bubble
                // was clamped back into the pet bitmaps, so the saved value is not
                // trustworthy on its own: keep the bubble on or near the screen.
                // Taken as saved: the travel limits depend on where the pet is on
                // screen, which is not known yet. Update() clamps it once it is.
                _bubble.OffsetY = _settings.BubbleOffsetY;
                _bubble.OnOffsetChanged(PersistBubbleOffset);
                _menu = new PetMenu(_catalog);
                // The menu treats the pet as part of itself for dismissal: moving from a
                // row onto the pet must not close it.
                // The menu treats the pet as part of itself for dismissal. This MUST go
                // through the same mapping the pet uses for its own clicks: the window is
                // a scaled rendering of the canvas, so a screen point has to be divided by
                // _scale before it means anything to the hit mask. Without that the mask
                // was read at the wrong scale and the menu closed while the pointer was
                // still over the pet.
                PopupMenu.PetHit = PetHitAtScreen;
                // The drag handoff uses the window rectangle, not the pixel mask: the
                // pet animates, so a pixel test makes the drag start only sometimes.
                PopupMenu.PetRectContains = (x, y) =>
                {
                    Native.RECT petRect = default(Native.RECT);
                    if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, ref petRect)) return false;
                    return x >= petRect.Left && x < petRect.Right &&
                           y >= petRect.Top && y < petRect.Bottom;
                };

                // The menu captures the mouse while it is open, so a press on the pet never
                // reaches this window and a drag could not start. The menu hands that press
                // back through here; the drag loop is tick-driven once _dragging is set, so
                // beginning it is all that is needed.
                PopupMenu.PetDragBegin = (screenX, screenY) =>
                {
                    Native.POINT start;
                    if (!Native.GetCursorPos(out start)) return;
                    Native.RECT petRect = default(Native.RECT);
                    if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, ref petRect)) return;
                    _dragging = true;
                    _dragPet = true;
                    _dragMoved = false;
                    _dragStartAt = Uptime.ElapsedMilliseconds;
                    _dragStartX = start.X;
                    _dragStartY = start.Y;
                    _dragStartWindowX = petRect.Left;
                    _dragStartWindowY = petRect.Top;
                    // The menu released its capture on close; take it so the pet keeps
                    // receiving the movement for the rest of the gesture.
                    Native.SetCapture(_hwnd);
                };
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
                if (_webp != null) _webp.Dispose();
                if (_surface != null) _surface.Dispose();
                if (_bubble != null) _bubble.Dispose();
                if (_canvas != null) _canvas.Dispose();
                if (_ipc != null) _ipc.Dispose();
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
            _bubble.Create();
            PositionBubble();
        }

        /// <summary>
        /// Reports the pet's screen rectangle to the bubble, which owns all of the
        /// bubble's geometry. GetWindowRect is authoritative and already in physical
        /// pixels, whereas _windowX/_windowY are the logical values the window was asked
        /// to use; under DPI scaling they differ, so they must never be mixed.
        /// </summary>
        private static void PositionBubble()
        {
            if (_bubble == null || _hwnd == IntPtr.Zero) return;
            Native.RECT petRect = new Native.RECT();
            Native.GetWindowRect(_hwnd, ref petRect);
            _bubble.SetPetRect(petRect);
            _bubbleDirty = true;
            SyncBubble();
        }

        /// <summary>Persists a bubble offset after the user finishes dragging it.</summary>
        private static void PersistBubbleOffset()
        {
            _settings.BubbleOffsetY = _bubble.OffsetY;
            _settings.Save();
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
        }

        private static void ApplyScale(double scale)
        {
            if (Math.Abs(scale - _scale) < 0.001) return;
            _scale = scale;
            _scaleSrcX = null;
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
            // After the pet is raised, not before: PositionBubble lifts the bubble
            // above it, and doing this the other way round buries the bubble.
            PositionBubble();
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
        /// <summary>
        /// Whether a screen point is on the pet's opaque pixels. Shares the scaling
        /// rules with <see cref="HitTest"/>: the window draws the canvas at _scale, so a
        /// screen point must be divided by _scale before it indexes the hit mask.
        /// </summary>
        internal static bool PetHitAtScreen(int screenX, int screenY)
        {
            if (_canvas == null || _hwnd == IntPtr.Zero) return false;
            var local = new Native.POINT { X = screenX, Y = screenY };
            Native.ScreenToClient(_hwnd, ref local);
            if (_scale <= 0) return false;
            int x = (int)Math.Round(local.X / _scale);
            int y = (int)Math.Round(local.Y / _scale);
            return _canvas.HitTest(x, y);
        }

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
            // The bubble is a separate window with its own mouse handling; a click that
            // reaches this window can only be a click on the pet itself.
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
                // The bubble has to follow the pet it belongs to.
                PositionBubble();
                return;
            }
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
                        _settings.BubbleOffsetY = _bubble.OffsetY;
                    }
                    _settings.Save();
                    return;
                }
            }
            int x = unchecked((short)((long)lParam & 0xFFFF));
            int y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
            int localX = (int)Math.Round(x / _scale);
            int localY = (int)Math.Round(y / _scale);
            if (!_canvas.HitTest(localX, localY)) return;

            // Deal with the window here if this process can. Everything needed for the
            // visible and minimised cases is a plain query — EnumWindows, IsWindowVisible,
            // IsIconic, and WM_SYSCOMMAND to the window — none of which the sandbox's low
            // integrity restricts. Only a window the shell has hidden has to go back through
            // the plugin, because reopening it means reaching the taskbar's XAML automation
            // tree, which low integrity cannot read.
            RaiseResult raised = TryRaiseLocally();
            if (Trace.Enabled) Trace.Note("click: local raise " + raised);

            // The plugin always hears about the click: it is what clears the "done" bubble,
            // and that must happen whether or not this process could handle the window. The
            // "shell" flag is what asks for the extra work, so the tray click only happens
            // when the window really is out of reach here.
            bool needShell = raised == RaiseResult.NeedsShell || raised == RaiseResult.NoWindow;
            _ipc.Send(needShell ? "{\"t\":\"open\",\"shell\":1}" : "{\"t\":\"open\"}");
        }

        private enum RaiseResult
        {
            /// <summary>No Harness window found at all; the plugin is the only one who can ask.</summary>
            NoWindow,
            /// <summary>The window is up and has the foreground.</summary>
            Shown,
            /// <summary>The window was minimised and has been restored.</summary>
            Restored,
            /// <summary>The window exists but is hidden; only the shell can bring it back.</summary>
            NeedsShell,
        }

        /// <summary>
        /// Brings the Harness window forward when this process is able to. Runs on the
        /// message thread because it is bounded: a query, at most one posted command, and a
        /// short wait for the restore. Nothing here can block for seconds the way reaching
        /// the tray does.
        /// </summary>
        private static RaiseResult TryRaiseLocally()
        {
            IntPtr target = FindHarnessWindow();
            if (target == IntPtr.Zero) return RaiseResult.NoWindow;

            bool restored = false;
            if (Native.IsIconic(target))
            {
                // The taskbar's own command. ShowWindow(SW_RESTORE) from a background process
                // is ignored by this window, but the system menu command is handled by the
                // window itself.
                Native.PostMessage(target, Native.WM_SYSCOMMAND, (IntPtr)Native.SC_RESTORE, IntPtr.Zero);
                WaitUntil(900, delegate { return !Native.IsIconic(target); });
                target = FindHarnessWindow();
                if (target == IntPtr.Zero) return RaiseResult.NoWindow;
                if (Native.IsIconic(target)) return RaiseResult.NeedsShell;
                restored = true;
            }

            if (!Native.IsWindowVisible(target)) return RaiseResult.NeedsShell;
            RaiseForeground(target);
            return restored ? RaiseResult.Restored : RaiseResult.Shown;
        }

        private static void OpenMenu()
        {
            CatalogEntry chosen;
            double chosenScale;
            int command = _menu.Show(_hwnd, _scale, out chosen, out chosenScale);
            if (command == PetMenu.CommandQuit) { Quit(); return; }
            if (command == PetMenu.CommandBalance) { _ipc.Send("{\"t\":\"balance\"}"); return; }
            if (chosenScale > 0) { ApplyScale(chosenScale); return; }
            if (chosen != null)
            {
                // A menu pick is a one-shot the user asked for; it must not be mistaken
                // for an ambient clip, but it does need to survive the next state push.
                _playingAmbient = true;
                PlayClip(chosen, false);
            }
        }

        private static void Quit()
        {
            _ipc.Send("{\"t\":\"closed\"}");
            Native.DestroyWindow(_hwnd);
        }

        /// <summary>Keep the EnumWindows thunk alive; a collected delegate crashes the callback.</summary>
        private static readonly Native.EnumWindowsProc RaiseEnumProc = RaiseEnumCallback;
        private static IntPtr _raiseTarget;
        private static int _raiseBest;
        private static int _raiseSelf;

        private static bool RaiseEnumCallback(IntPtr hwnd, IntPtr lParam)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            // Never touch our own windows.
            if ((int)pid == _raiseSelf) return true;
            // An owned window is a dialog; the main window has no owner.
            if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return true;
            int length = Native.GetWindowTextLength(hwnd);
            if (length <= 0) return true;
            var title = new System.Text.StringBuilder(length + 1);
            Native.GetWindowText(hwnd, title, title.Capacity);
            string text = title.ToString();
            // Only the Electron shell itself ends with "DeepSeek Harness". A browser tab
            // showing this project's page has a title like
            //   "DSH … — DeepSeek Harness 和另外 2 个页面 - 个人 - Microsoft Edge"
            // which contains the name but is not the app, so matching on "contains" would
            // restore the browser instead of the app (observed).
            if (!text.EndsWith("DeepSeek Harness", StringComparison.Ordinal)) return true;
            int score = 0;
            if (text.Contains("\u2014") || text.Contains(" - ")) score += 20;
            if (Native.IsWindowVisible(hwnd)) score += 10;
            if (!Native.IsIconic(hwnd)) score += 5;
            if (score > _raiseBest) { _raiseBest = score; _raiseTarget = hwnd; }
            return true;
        }

        /// <summary>Picks the best Harness window, whether it is visible, minimised or hidden.</summary>
        private static IntPtr FindHarnessWindow()
        {
            _raiseTarget = IntPtr.Zero;
            _raiseBest = -1;
            _raiseSelf = (int)Native.GetCurrentProcessId();
            Native.EnumWindows(RaiseEnumProc, IntPtr.Zero);
            if (Trace.Enabled) Trace.Note("window: target=" + _raiseTarget.ToString("X") + " best=" + _raiseBest);
            return _raiseTarget;
        }

        /// <summary>Polls until the predicate holds or the budget is spent.</summary>
        private static bool WaitUntil(int timeoutMs, Func<bool> predicate)
        {
            long deadline = Uptime.ElapsedMilliseconds + timeoutMs;
            while (Uptime.ElapsedMilliseconds < deadline)
            {
                if (predicate()) return true;
                Thread.Sleep(60);
            }
            return predicate();
        }

        /// <summary>
        /// True when any Harness window is up. Deliberately not the scored best window:
        /// during a restore the enumeration can momentarily rank a different instance first,
        /// and "is the app back on screen" is the question that matters here.
        /// </summary>
        private static bool HarnessIsShown()
        {
            _shownCount = 0;
            Native.EnumWindows(ShownEnumProc, IntPtr.Zero);
            return _shownCount > 0;
        }

        private static readonly Native.EnumWindowsProc ShownEnumProc = ShownEnumCallback;
        private static int _shownCount;

        private static bool ShownEnumCallback(IntPtr hwnd, IntPtr lParam)
        {
            int length = Native.GetWindowTextLength(hwnd);
            if (length <= 0) return true;
            var title = new System.Text.StringBuilder(length + 1);
            Native.GetWindowText(hwnd, title, title.Capacity);
            if (!title.ToString().EndsWith("DeepSeek Harness", StringComparison.Ordinal)) return true;
            if (Native.IsWindowVisible(hwnd) && !Native.IsIconic(hwnd)) _shownCount++;
            return true;
        }

        /// <summary>Gives the foreground to a window owned by another process.</summary>
        private static void RaiseForeground(IntPtr target)
        {
            // Windows only lets the foreground thread hand focus over, so borrow its
            // input state for the duration of the SetForegroundWindow call.
            IntPtr foreground = Native.GetForegroundWindow();
            uint foregroundPid;
            uint foregroundThread = Native.GetWindowThreadProcessId(foreground, out foregroundPid);
            uint selfThread = Native.GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != selfThread &&
                            Native.AttachThreadInput(selfThread, foregroundThread, true);
            try
            {
                Native.SetForegroundWindow(target);
            }
            finally
            {
                if (attached) Native.AttachThreadInput(selfThread, foregroundThread, false);
            }
        }
        /// <summary>
        /// Asks the desktop shell to bring its own window forward via the `dsh:` protocol.
        ///
        /// This has to be ShellExecuteEx. Measured on this machine:
        ///   cmd /c start "" dsh://open             -> nothing happened
        ///   rundll32 url.dll,FileProtocolHandler   -> window stayed minimised
        ///   ShellExecuteEx("dsh://open", nShow 1)  -> the running instance restored it
        /// The shell's own focusPrimaryWindow() handles both minimised and hidden windows,
        /// so reaching it is all that is needed.
        /// </summary>
        private static void OpenHarnessViaProtocol()
        {
            var info = new Native.SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf(typeof(Native.SHELLEXECUTEINFO)),
                fMask = Native.SEE_MASK_NOCLOSEPROCESS,
                lpVerb = "open",
                lpFile = "dsh://open",
                nShow = Native.SW_SHOWNORMAL,
            };
            bool ok = false;
            try { ok = Native.ShellExecuteEx(ref info); }
            catch (Exception) { ok = false; }
            if (info.hProcess != IntPtr.Zero) Native.CloseHandle(info.hProcess);
            if (Trace.Enabled) Trace.Note("protocol: ShellExecuteEx ok=" + ok);
        }

        /// <summary>Guards against starting a second raise while one is still resolving.</summary>
        private static int _raisePending;

        /// <summary>
        /// Runs the raise when the plugin asks for it through the dsh:// path, or when the
        /// pet itself decided the window is hidden.
        ///
        /// The local attempt comes first: if the window turns out to be visible or minimised
        /// after all, this process can handle it and the shell is never bothered. Only a
        /// genuinely hidden window reaches further, and reaching the tray from here cannot
        /// work (low integrity, see TryRaiseLocally), so the plugin is told to click it.
        /// </summary>
        private static void RequestRaise()
        {
            if (System.Threading.Interlocked.Exchange(ref _raisePending, 1) != 0) return;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try { OpenHarness(); }
                catch (Exception error)
                {
                    if (Trace.Enabled) Trace.Note("open: failed " + error.GetType().Name + ": " + error.Message);
                }
                finally { System.Threading.Interlocked.Exchange(ref _raisePending, 0); }
            });
        }

        /// <summary>
        /// The fallback path used once the pet has established that the window is hidden and
        /// out of its reach. dsh:// is tried first because it costs one ShellExecuteEx; it
        /// restores a minimised window in builds whose protocol handler runs synchronously,
        /// though it does not reopen a hidden one on this machine. The tray click, which the
        /// plugin performs at medium integrity, is the route that actually works for hidden
        /// windows and is requested by sending the click to the plugin.
        /// </summary>
        private static void OpenHarness()
        {
            // Cheap first: if the window is already up, or only minimised, this process settles
            // it without spawning anything. That is the steady state, and it costs one window
            // query rather than a shell round trip.
            RaiseResult local = TryRaiseLocally();
            if (local == RaiseResult.Shown || local == RaiseResult.Restored)
            {
                if (Trace.Enabled) Trace.Note("open: settled locally " + local);
                return;
            }

            if (local == RaiseResult.NoWindow)
            {
                // Nothing to look at. The shell may still be able to create it.
                OpenHarnessViaProtocol();
                WaitUntil(OpenWaitMs * 2, HarnessIsShown);
                return;
            }

            // Hidden, and this process cannot undo that. dsh:// is tried once because it is
            // cheap and does restore a minimised window in builds whose handler runs
            // synchronously; when it does not bring the window back, the plugin's tray click
            // is the route that works.
            OpenHarnessViaProtocol();
            if (WaitUntil(OpenWaitMs * 2, HarnessIsShown))
            {
                ShowHarnessFront();
                if (Trace.Enabled) Trace.Note("open: restored via dsh://");
                return;
            }

            if (Trace.Enabled) Trace.Note("open: hidden and out of reach locally");
        }


        /// <summary>Brings the best Harness window forward once it is known to exist.</summary>
        private static void ShowHarnessFront()
        {
            IntPtr shown = FindHarnessWindow();
            if (shown != IntPtr.Zero) RaiseForeground(shown);
        }
        // ——— rendering ————————————————————————————————————————————

        private static void Tick()
        {
            if (_ipc.Closed) { Quit(); return; }

            string line;
            while (_ipc.TryRead(out line)) HandleCommand(line);

            UpdateHitState();
            UpdateDrag();

            // The bubble is its own window: repaint it only when its text, dot or
            // place changed, so an unchanged frame costs nothing here.
            if (_bubbleDirty) SyncBubble();

            long now = Uptime.ElapsedMilliseconds;
            if (_bubbleHideAt >= 0 && now >= _bubbleHideAt)
            {
                _bubbleHideAt = -1;
                SetBubble(null, BubbleDot.None);
            }

            if (_webp == null) { PlayKind("idle", true, 0); return; }

            // Ambient playback is driven from the tick, not from the end of the idle loop.
            // Waiting for an idle loop to finish meant the grace period was measured in
            // whole loop lengths, so the pet sat on the idle clip far longer than intended.
            //
            // The gap is short on purpose. Clips measure 5-10 s, and a 20-45 s gap left the
            // idle clip on screen for roughly three quarters of the time — the pet read as
            // "always idle" even though the bag was being drawn from. Idle is now a short
            // breath between ambient clips, which is what "待机就是随机播放" means.
            if (_mode == "idle" && !_playingAmbient && _currentKind == "idle" && now >= _nextActionAt)
            {
                PlayRandomAction();
                _nextActionAt = now + AmbientGapMs();
            }

            if (now >= _frameDue && _webpFrame != null)
            {
                Present();
                if (Trace.Enabled) Trace.Frame();
                long t0 = Trace.Enabled ? Stopwatch.GetTimestamp() : 0;
                if (!LoadNextFrame())
                {
                    OnClipEnd(now);
                    return;
                }
                if (Trace.Enabled) Trace.Add(Trace.Decode, Stopwatch.GetTimestamp() - t0);
            }
            else if (_bubbleDirty)
            {
                Present();
            }
            if (Trace.Enabled) { Trace.TickRun(); Trace.MaybeFlush(); }
        }

        private static bool LoadNextFrame()
        {
            IntPtr buffer;
            int timestamp;
            if (!_webp.TryNextFrame(out buffer, out timestamp)) return false;
            int bytes = _webp.Width * _webp.Height * 4;
            if (_webpFrame == null || _webpFrame.Length != bytes) _webpFrame = new byte[bytes];
            Marshal.Copy(buffer, _webpFrame, 0, bytes);
            _webpTimestamp = timestamp;
            _frameDue = _playBase + timestamp;
            return true;
        }

        private static void Present()
        {
            long t0 = Trace.Enabled ? Stopwatch.GetTimestamp() : 0;
            if (_scale == 1.0)
            {
                if (_webpFrame != null)
                    _canvas.ComposePet(_webpFrame, _webp.Width, _webp.Height);
                if (Trace.Enabled) { Trace.Add(Trace.Compose, Stopwatch.GetTimestamp() - t0); t0 = Stopwatch.GetTimestamp(); }
                if (_fadeStart >= 0 && _fadeSnapshot != null)
                {
                    long elapsed = Uptime.ElapsedMilliseconds - _fadeStart;
                    if (elapsed >= FadeMs) _fadeStart = -1;
                    else _canvas.BlendFrom(_fadeSnapshot, elapsed / (double)FadeMs);
                }
                _surface.Upload(_canvas.Pixels);
            }
            else
            {
                // Fused path: one pass from the decoded frame into the scaled DIB.
                BuildScaleTables();
                if (_webpFrame != null)
                    _canvas.RenderScaled(_webpFrame, _webp.Width, _webp.Height, _surface.Bits,
                        _windowWidth, _windowHeight, _scaleSrcX, _scaleFracX, _scaleSrcY, _scaleFracY);
                if (Trace.Enabled) { Trace.Add(Trace.Compose, Stopwatch.GetTimestamp() - t0); t0 = Stopwatch.GetTimestamp(); }
                if (_fadeStart >= 0 && _fadeSnapshot != null)
                {
                    long elapsed = Uptime.ElapsedMilliseconds - _fadeStart;
                    if (elapsed >= FadeMs) _fadeStart = -1;
                    else _canvas.BlendScaledFrom(_fadeSnapshot, elapsed / (double)FadeMs, _surface.Bits, _windowWidth, _windowHeight);
                }
            }
            if (Trace.Enabled) { Trace.Add(Trace.Scale, Stopwatch.GetTimestamp() - t0); t0 = Stopwatch.GetTimestamp(); }
            _surface.Present(_hwnd, _windowX, _windowY);
            if (Trace.Enabled) Trace.Add(Trace.Present, Stopwatch.GetTimestamp() - t0);
        }

        /// <summary>
        /// Repaints the bubble window, which lives outside the pet's bitmap and therefore
        /// costs the pet nothing while there is no text. Returns true when it had to draw.
        /// </summary>
        private static bool SyncBubble()
        {
            if (_bubble == null) return false;
            string text = _bubbleText;
            long t0 = Trace.Enabled ? Stopwatch.GetTimestamp() : 0;
            if (text == null || text.Length == 0)
            {
                _bubble.Hide();
                _bubbleDirty = false;
                return false;
            }
            // The bubble owns its geometry; the pet only supplies its own rectangle, which
            // SetPetRect has already been given by PositionBubble.
            _bubble.Update(text, _bubbleDot);
            _bubbleDirty = false;
            if (Trace.Enabled) Trace.Add(Trace.Bubble, Stopwatch.GetTimestamp() - t0);
            return true;
        }

        /// <summary>Source index and 8-bit fraction for every destination column/row (no divisions in the loop).</summary>
        private static void BuildScaleTables()
        {
            if (_scaleSrcX != null && _scaleSrcX.Length == _windowWidth &&
                _scaleSrcY != null && _scaleSrcY.Length == _windowHeight) return;
            int sw = _canvas.Width;
            int sh = _canvas.Height;
            _scaleSrcX = new int[_windowWidth];
            _scaleFracX = new int[_windowWidth];
            for (int dx = 0; dx < _windowWidth; dx++)
            {
                int fixedValue = (dx * sw << 8) / _windowWidth;
                _scaleSrcX[dx] = fixedValue >> 8;
                _scaleFracX[dx] = fixedValue & 0xFF;
            }
            _scaleSrcY = new int[_windowHeight];
            _scaleFracY = new int[_windowHeight];
            for (int dy = 0; dy < _windowHeight; dy++)
            {
                int fixedValue = (dy * sh << 8) / _windowHeight;
                _scaleSrcY[dy] = fixedValue >> 8;
                _scaleFracY[dy] = fixedValue & 0xFF;
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
            if (_oneShot || !_webp.Entry.IsLoopingKind())
            {
                // Whatever just finished, the screen is no longer showing an ambient clip.
                _playingAmbient = false;
                if (_mode == "working") { PlayKind("work-busy", true, 0); return; }
                if (_mode == "waiting") { PlayKind("work-waiting", true, 0); return; }
                PlayKind("idle", true, 0);
                return;
            }
            // Animated WebP can restart in place; no decoder reopen needed.
            _webp.Reset();
            _playBase = now;
            if (!LoadNextFrame())
            {
                PlayKind("idle", true, 0);
                return;
            }
        }

        private static void HandleCommand(string line)
        {
            Dictionary<string, object> message;
            try { message = (Dictionary<string, object>)Json.Parse(line); }
            catch (Exception) { return; }

            string type = Json.String(message, "t");
            if (Trace.Enabled) Trace.Note("cmd type=" + type + " text=" + Json.String(message, "text"));
            if (type == "quit") { Quit(); return; }
            // Raising the Harness window is a series of blocking Win32 waits — up to
            // several seconds when the window has to be found, restored and confirmed.
            // Running it inline would freeze the animation for that whole time, so it
            // goes to the thread pool; the pet keeps playing while the shell catches up.
            if (type == "raise") { RequestRaise(); return; }
            if (type != "state") return;

            _mode = Json.String(message, "mode") ?? "idle";
            string text = Normalize(Json.String(message, "text"));
            double level = Json.Number(message, "level", -1);

            if (_mode == "idle")
            {
                _bubbleHideAt = -1;
                SetBubble(null, BubbleDot.None);
                // The host pushes state every 250 ms, so forcing idle whenever the current
                // clip is not idle would restart the idle loop four times a second and cut
                // every ambient action off the moment it began. An ambient action is
                // allowed to finish; it returns to idle by itself in OnClipEnd.
                if (_currentKind != "idle" && !_playingAmbient) PlayKind("idle", true, 0);
                return;
            }
            if (_mode == "working")
            {
                _bubbleHideAt = -1;
                SetBubble(text, BubbleDot.None);
                // Work always outranks an ambient clip: the user needs to see the state.
                _playingAmbient = false;
                if (_currentKind != "work-busy") PlayKind("work-busy", true, 0);
                return;
            }
            if (_mode == "waiting")
            {
                _bubbleHideAt = -1;
                SetBubble(text, BubbleDot.Waiting);
                _playingAmbient = false;
                if (_currentKind != "work-waiting") PlayKind("work-waiting", true, 0);
                return;
            }
            if (_mode == "done")
            {
                _bubbleHideAt = -1;
                SetBubble(text, BubbleDot.Done);
                _playingAmbient = false;
                if (_currentKind != "work-done") PlayKind("work-done", false, 0);
                return;
            }
            if (_mode == "balance")
            {
                _playingAmbient = false;
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

        /// <summary>
        /// The pause between ambient clips. Short enough that the pet visibly cycles through
        /// its library, long enough that the cuts do not feel frantic.
        /// </summary>
        private static long AmbientGapMs()
        {
            return 2500 + Rng.Next(3500);
        }

        private static void PlayRandomAction()
        {
            var pool = new List<CatalogEntry>();
            foreach (string kind in new[] { "action", "click", "whisper", "balance" })
                pool.AddRange(_catalog.ByKind(kind));
            if (pool.Count == 0) return;
            // Refill the bag when it empties, and whenever the catalog changed underneath
            // us (a different media tier), so a stale bag never indexes past the end.
            if (_actionBagIndex >= ActionBag.Count || ActionBag.Count != pool.Count)
            {
                ActionBag.Clear();
                ActionBag.AddRange(pool);
                _actionBagIndex = 0;
                // Fisher-Yates: every clip is guaranteed to appear once per cycle, and no
                // clip can repeat until all the others have played. Redrawing a random
                // index each time is what made the short, early clips dominate.
                for (int i = ActionBag.Count - 1; i > 0; i--)
                {
                    int j = Rng.Next(i + 1);
                    CatalogEntry swap = ActionBag[i];
                    ActionBag[i] = ActionBag[j];
                    ActionBag[j] = swap;
                }
                // Avoid replaying the clip that just finished at the head of a new cycle.
                if (_webp != null && ActionBag[0] == _webp.Entry && ActionBag.Count > 1)
                {
                    CatalogEntry swap = ActionBag[0];
                    ActionBag[0] = ActionBag[ActionBag.Count - 1];
                    ActionBag[ActionBag.Count - 1] = swap;
                }
            }
            CatalogEntry next = ActionBag[_actionBagIndex++];
            _playingAmbient = true;
            PlayClip(next, false);
        }

        private static void PlayClip(CatalogEntry entry, bool loop, bool force = false)
        {
            if (_webp != null)
            {
                if (!force && _webp.Entry == entry && loop && !_oneShot) return;
                // Cross-fade from the pixels that are on screen right now. The scaled path
                // renders straight into the DIB, so the snapshot has to come from there.
                if (_scale == 1.0)
                {
                    if (_canvas.Pixels != null)
                    {
                        if (_fadeSnapshot == null || _fadeSnapshot.Length != _canvas.Pixels.Length)
                            _fadeSnapshot = new byte[_canvas.Pixels.Length];
                        Buffer.BlockCopy(_canvas.Pixels, 0, _fadeSnapshot, 0, _fadeSnapshot.Length);
                        _fadeStart = Uptime.ElapsedMilliseconds;
                    }
                }
                else
                {
                    int bytes = _windowWidth * _windowHeight * 4;
                    if (_fadeSnapshot == null || _fadeSnapshot.Length != bytes) _fadeSnapshot = new byte[bytes];
                    Marshal.Copy(_surface.Bits, _fadeSnapshot, 0, bytes);
                    _fadeStart = Uptime.ElapsedMilliseconds;
                }
                _webp.Dispose();
                _webp = null;
            }
            try
            {
                _webp = new WebpAnimation(entry);
            }
            catch (Exception)
            {
                _webp = null;
                CatalogEntry fallback = _catalog.First("idle");
                if (fallback != null && fallback != entry)
                {
                    try { _webp = new WebpAnimation(fallback); } catch (Exception) { _webp = null; }
                }
                if (_webp == null) return;
            }
            if (_webp.Width != _canvas.Width || _webp.Height != _canvas.Height)
            {
                // A clip with an unexpected canvas size would skew every pixel; skip it.
                _webp.Dispose();
                _webp = null;
                return;
            }
            _currentKind = _webp.Entry.Kind;
            _oneShot = !loop;
            _bubbleDirty = true;
            _playBase = Uptime.ElapsedMilliseconds;
            _frameDue = _playBase;
            if (!LoadNextFrame())
            {
                _webp.Dispose();
                _webp = null;
            }
        }
    }
}


















