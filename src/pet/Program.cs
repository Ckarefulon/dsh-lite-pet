// dsh-lite-pet desktop helper: one layered, click-through window that plays
// H.264 color + alpha tracks through Media Foundation and draws its own bubble.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DshLitePet
{
    internal static class Program
    {
        private const int TimerId = 1;
        private const int TimerPeriodMs = 12;
        private const int WindowMargin = 24;
        private const string WindowClass = "DshLitePetWindow";


        private static Catalog _catalog;
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
        private static int _windowX;
        private static int _windowY;

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
                _catalog = Catalog.Load(mediaDir);
                _canvas = new PetCanvas(_catalog.Width, _catalog.Height);
                _surface = new Native.LayeredSurface(_canvas.Width, _canvas.Height);
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
                if (_canvas != null) _canvas.Dispose();
                if (_ipc != null) _ipc.Dispose();
                Mf.Stop();
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

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
            Native.RegisterClassEx(ref wc);

            Native.RECT work = new Native.RECT();
            Native.SystemParametersInfo(Native.SPI_GETWORKAREA, 0, ref work, 0);
            _windowX = work.Right - _canvas.Width - WindowMargin;
            _windowY = work.Bottom - _canvas.Height - WindowMargin;

            _hwnd = Native.CreateWindowEx(
                Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST,
                WindowClass, "dsh-lite-pet", Native.WS_POPUP,
                _windowX, _windowY, _canvas.Width, _canvas.Height,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            Native.SetWindowPos(_hwnd, (IntPtr)(-1), 0, 0, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
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

        private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_TIMER:
                    Tick();
                    return IntPtr.Zero;
                case Native.WM_NCHITTEST:
                    return (IntPtr)HitTest(lParam);
                case Native.WM_LBUTTONUP:
                    _ipc.Send("{\"t\":\"open\"}");
                    return IntPtr.Zero;
                case Native.WM_RBUTTONUP:
                    OpenMenu();
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
            return _canvas != null && _canvas.HitTest(local.X, local.Y) ? Native.HTCLIENT : Native.HTTRANSPARENT;
        }

        private static void OpenMenu()
        {
            CatalogEntry chosen;
            int command = _menu.Show(_hwnd, out chosen);
            if (command == PetMenu.CommandQuit) { Quit(); return; }
            if (command == PetMenu.CommandBalance) { _ipc.Send("{\"t\":\"balance\"}"); return; }
            if (chosen != null) PlayClip(chosen, false);
        }

        private static void Quit()
        {
            _ipc.Send("{\"t\":\"closed\"}");
            Native.DestroyWindow(_hwnd);
        }

        private static void Tick()
        {
            if (_ipc.Closed) { Quit(); return; }

            string line;
            while (_ipc.TryRead(out line)) HandleCommand(line);

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
                _canvas.ComposePet(_clip.Color, _clip.Alpha, _clip.Width, _clip.Height);
                Present();
            }
            else if (_bubbleDirty)
            {
                Present();
            }
        }

        private static void Present()
        {
            _canvas.DrawBubble(_bubbleText, _bubbleDot);
            _bubbleDirty = false;
            _surface.Upload(_canvas.Pixels);
            _surface.Present(_hwnd, _windowX, _windowY);
        }

        private static void SetBubble(string text, BubbleDot dot)
        {
            if (text == _bubbleText && dot == _bubbleDot) return;
            _bubbleText = text;
            _bubbleDot = dot;
            _bubbleDirty = true;
        }

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

        private static void PlayKind(string kind, bool loop, int levelIndex)
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
                if (kind != "idle") { PlayKind("idle", true, 0); return; }
                if (_catalog.Animations.Count == 0) return;
                entry = _catalog.Animations[0];
            }
            PlayClip(entry, loop);
        }

        private static void PlayRandomAction()
        {
            var pool = new List<CatalogEntry>();
            foreach (string kind in new[] { "action", "click", "whisper", "balance" })
                pool.AddRange(_catalog.ByKind(kind));
            if (pool.Count == 0) return;
            PlayClip(pool[Rng.Next(pool.Count)], false);
        }

        private static void PlayClip(CatalogEntry entry, bool loop)
        {
            if (_clip != null)
            {
                if (_clip.Entry == entry && loop && !_oneShot) return;
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



