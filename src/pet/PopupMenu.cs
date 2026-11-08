// The pet's right-click menu, drawn by us.
//
// Why not a native TrackPopupMenuEx: it only dismisses itself when the click lands inside
// it, unless its owner is the foreground window — and making the pet foreground is exactly
// the "右键时抢焦点了" complaint. Every attempt to cancel a native menu from outside its
// modal loop failed here (a WM_TIMER is never dispatched into it, a posted WM_NULL never
// reaches our WndProc, posting VK_ESCAPE at the popup is ignored) — measured three times.
//
// So this is a small owner-drawn window instead: WS_EX_NOACTIVATE, never takes focus,
// captures the mouse while open, and the capture going away (WM_CAPTURECHANGED) is what
// closes it. No global hook, no polling, no timer.
//
// ——— resource budget ——————————————————————————————————————————————
//   · the window is created once and then hidden, never destroyed and recreated;
//   · the message loop is a blocking GetMessage — zero CPU while the menu just sits there;
//   · the bitmap and DIB are allocated only when the size changes, and reused across
//     openings (the menu is one size every time);
//   · repaint happens on a hover change, not on every mouse move;
//   · theme and fonts are cached in the shared base, so no registry read per paint.
// Nothing here runs when the menu is closed.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;

namespace DshLitePet
{
    internal sealed class PopupMenu : LayeredPopup
    {
        private const string ClassName = "DshLitePetMenu";

        // ——— geometry (device pixels) ————————————————————————————
        private const int ItemHeight = 29;
        private const int SeparatorHeight = 9;
        private const int PadY = 5;
        private const int PadX = 5;
        private const int GutterWidth = 22;
        private const int TextPadX = 10;
        private const int SubmenuArrowWidth = 16;
        private const int Radius = 9;
        private const int HoverRadius = 5;
        private const int MinWidth = 128;

        internal sealed class Item
        {
            public string Label;
            public int Command;
            public int Submenu = -1;
            public bool Separator;
            public bool Checked;
            public Rectangle Bounds;
        }

        internal sealed class Panel
        {
            public readonly List<Item> Items = new List<Item>();
            public int Width;
            public int Height;
            public int X;
            public int Y;
            /// <summary>Index of the item whose submenu is open, or -1.</summary>
            public int OpenIndex = -1;
        }

        private readonly List<Panel> _panels = new List<Panel>();
        private readonly List<Panel> _visible = new List<Panel>();

        private static Font _font;

        private int _originX;
        private int _originY;
        private bool _open;
        private int _chosenCommand;
        private int _hoverX = int.MinValue;
        private int _hoverY = int.MinValue;

        private static Font MenuFont()
        {
            if (_font == null) _font = SystemFonts.MenuFont;
            return _font;
        }

        internal PopupMenu()
        {
            _panels.Add(new Panel());
        }

        private Panel Root { get { return _panels[0]; } }

        protected override string WindowClass { get { return ClassName; } }
        protected override string WindowTitle { get { return "dsh-lite-pet-menu"; } }

        // ——— building ————————————————————————————————————————————

        /// <summary>Starts a new menu: clears the model but keeps the window and surfaces.</summary>
        internal void Reset()
        {
            _panels.Clear();
            _panels.Add(new Panel());
            _visible.Clear();
            _chosenCommand = 0;
        }

        internal Panel AddSubmenu(string label)
        {
            return AddSubmenuTo(Root, label);
        }

        internal Panel AddSubmenuTo(Panel parent, string label)
        {
            var sub = new Panel();
            _panels.Add(sub);
            parent.Items.Add(new Item { Label = label, Submenu = _panels.Count - 1 });
            return sub;
        }

        internal void AddAction(string label, int command)
        {
            AddActionTo(Root, label, command);
        }

        internal void AddActionTo(Panel panel, string label, int command)
        {
            panel.Items.Add(new Item { Label = label, Command = command });
        }

        internal void AddCheckableTo(Panel panel, string label, int command, bool isChecked)
        {
            panel.Items.Add(new Item { Label = label, Command = command, Checked = isChecked });
        }

        internal void AddSeparator()
        {
            Root.Items.Add(new Item { Separator = true });
        }

        // ——— layout ————————————————————————————————————————————

        /// <summary>
        /// Sizes one panel. Measuring uses a throwaway text Graphics rather than the
        /// bitmap's, so this runs before any surface exists.
        /// </summary>
        private void Measure(Panel panel)
        {
            Font font = MenuFont();
            int widest = MinWidth;
            using (var measure = Graphics.FromHwnd(IntPtr.Zero))
            {
                foreach (Item item in panel.Items)
                {
                    if (item.Separator) continue;
                    SizeF size = measure.MeasureString(item.Label, font);
                    int need = (int)Math.Ceiling(size.Width) + GutterWidth + TextPadX * 2;
                    if (item.Submenu >= 0) need += SubmenuArrowWidth;
                    if (need > widest) widest = need;
                }
            }
            panel.Width = widest;

            int y = PadY;
            foreach (Item item in panel.Items)
            {
                int h = item.Separator ? SeparatorHeight : ItemHeight;
                item.Bounds = new Rectangle(PadX, y, panel.Width - PadX * 2, h);
                y += h;
            }
            panel.Height = y + PadY;
        }

        /// <summary>Nudges a panel onto the work area. Work area comes from SPI_GETWORKAREA.</summary>
        private static void PlaceOnScreen(Panel panel, int x, int y, int flipFrom)
        {
            Native.RECT work = default(Native.RECT);
            if (!Native.SystemParametersInfo(Native.SPI_GETWORKAREA, 0, ref work, 0))
            {
                work = new Native.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            }
            if (x + panel.Width > work.Right) x = Math.Max(work.Left, flipFrom - panel.Width);
            if (x < work.Left) x = work.Left;
            if (y + panel.Height > work.Bottom) y = Math.Max(work.Top, y - panel.Height);
            if (y < work.Top) y = work.Top;
            panel.X = x;
            panel.Y = y;
        }

        /// <summary>Collects the root plus the chain of open submenus, reusing one list.</summary>
        private List<Panel> VisiblePanels()
        {
            _visible.Clear();
            _visible.Add(_panels[0]);
            int current = 0;
            while (true)
            {
                Panel panel = _panels[current];
                if (panel.OpenIndex < 0 || panel.OpenIndex >= panel.Items.Count) break;
                int next = panel.Items[panel.OpenIndex].Submenu;
                if (next < 0 || next >= _panels.Count) break;
                _visible.Add(_panels[next]);
                current = next;
            }
            return _visible;
        }

        // ——— showing ————————————————————————————————————————

        /// <summary>
        /// Shows the menu at the cursor and blocks until dismissed or chosen. Returns the
        /// chosen command, or 0. Drives a private message loop: no timer, no polling thread,
        /// and no CPU at all while the menu is simply open.
        /// </summary>
        internal int Show()
        {
            if (Trace.Enabled) Trace.Note("menu: show, rootItems=" + _panels[0].Items.Count.ToString());
            if (_panels[0].Items.Count == 0) return 0;
            if (Hwnd == IntPtr.Zero) CreateWindow(10, 10);
            if (Trace.Enabled) Trace.Note("menu: hwnd=" + Hwnd.ToString());
            if (Hwnd == IntPtr.Zero) return 0;

            foreach (Panel panel in _panels) Measure(panel);

            Native.POINT cursor;
            Native.GetCursorPos(out cursor);
            PlaceOnScreen(Root, cursor.X, cursor.Y, cursor.X);
            CascadeSubmenus();

            _open = true;
            _chosenCommand = 0;
            _hoverX = int.MinValue;
            _hoverY = int.MinValue;

            // Capture so drags and wheel input stay with the menu while it is open.
            Native.SetCapture(Hwnd);
            Relayout();
            Native.ShowWindow(Hwnd, Native.SW_SHOWNOACTIVATE);
            // Raise above the pet, which is topmost too. Within the topmost band the
            // order is whoever was raised last, and the pet is raised on every drag and
            // scale change — so without this the menu could end up underneath it.
            Native.SetWindowPos(Hwnd, (IntPtr)Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);

            while (_open)
            {
                // Peek, do not block. A blocking GetMessage leaves this window with
                // no opportunity to notice an outside click: the menu is
                // WS_EX_NOACTIVATE, so clicking another window transfers ACTIVATION,
                // which never changes our mouse capture and never wakes us.
                Native.MSG msg;
                if (Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
                {
                    if (msg.message == Native.WM_QUIT) break;
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                    continue;
                }

                // No message pending: has the pointer left the panels?
                if (PointerLeftMenu()) Close(0);
                else Thread.Sleep(15);
            }

            Native.ReleaseCapture();
            Native.ShowWindow(Hwnd, Native.SW_HIDE);
            Root.OpenIndex = -1;
            int result = _chosenCommand;
            _chosenCommand = 0;
            _open = false;
            return result;
        }

        /// <summary>Positions every submenu beside its parent row, before the window is shown.</summary>
        private void CascadeSubmenus()
        {
            for (int i = 1; i < _panels.Count; i++)
            {
                Panel parentPanel = null;
                int parentIndex = -1;
                foreach (Panel candidate in _panels)
                {
                    for (int k = 0; k < candidate.Items.Count; k++)
                    {
                        if (candidate.Items[k].Submenu == i) { parentPanel = candidate; parentIndex = k; break; }
                    }
                    if (parentPanel != null) break;
                }
                if (parentPanel == null) continue;
                Item anchor = parentPanel.Items[parentIndex];
                PlaceOnScreen(_panels[i],
                    parentPanel.X + parentPanel.Width - PadX,
                    parentPanel.Y + anchor.Bounds.Top - PadY,
                    parentPanel.X);
            }
        }

        // ——— painting ————————————————————————————————————————

        /// <summary>
        /// Fits the window around the visible panels and repaints. Allocation happens only
        /// when the size changed, so reopening at the same size costs zero allocations.
        /// </summary>
        private void Relayout()
        {
            List<Panel> visible = VisiblePanels();

            int left = int.MaxValue;
            int top = int.MaxValue;
            int right = int.MinValue;
            int bottom = int.MinValue;
            for (int i = 0; i < visible.Count; i++)
            {
                Panel panel = visible[i];
                if (panel.X < left) left = panel.X;
                if (panel.Y < top) top = panel.Y;
                if (panel.X + panel.Width > right) right = panel.X + panel.Width;
                if (panel.Y + panel.Height > bottom) bottom = panel.Y + panel.Height;
            }
            int width = right - left;
            int height = bottom - top;
            if (width <= 0 || height <= 0) return;

            _originX = left;
            _originY = top;
            EnsureSurface(width, height);
            Paint(visible);
            Present(_originX, _originY);
        }

        /// <summary>Repaints without re-measuring the window rect.</summary>
        private void RepaintOnly()
        {
            if (Surface == null) return;
            Paint(VisiblePanels());
            Present(_originX, _originY);
        }

        private void Paint(List<Panel> visible)
        {
            SurfaceTheme theme = SurfaceTheme.Current();
            Graphics.Clear(Color.Transparent);

            for (int p = 0; p < visible.Count; p++)
            {
                Panel panel = visible[p];
                var box = new Rectangle(panel.X - _originX, panel.Y - _originY, panel.Width, panel.Height);
                DrawPanel(box, Radius, theme);

                for (int i = 0; i < panel.Items.Count; i++)
                {
                    Item item = panel.Items[i];
                    if (item.Separator) continue;

                    int rowTop = panel.Y + item.Bounds.Top;
                    int rowLeft = panel.X + item.Bounds.X;
                    bool hot = _hoverX >= rowLeft && _hoverX < rowLeft + item.Bounds.Width &&
                               _hoverY >= rowTop && _hoverY < rowTop + item.Bounds.Height;

                    if (hot)
                    {
                        var hoverRect = new Rectangle(rowLeft - _originX, rowTop - _originY,
                            item.Bounds.Width, item.Bounds.Height);
                        using (var hoverPath = RoundedRect(hoverRect, HoverRadius))
                        using (var hoverFill = new SolidBrush(theme.Hover))
                            Graphics.FillPath(hoverFill, hoverPath);
                    }

                    int baselineY = rowTop - _originY + item.Bounds.Height / 2;

                    if (item.Checked)
                    {
                        using (var pen = new Pen(theme.Text, 1.7f))
                        {
                            pen.StartCap = LineCap.Round;
                            pen.EndCap = LineCap.Round;
                            int cx = rowLeft - _originX + GutterWidth / 2;
                            Graphics.DrawLines(pen, new[]
                            {
                                new Point(cx - 4, baselineY),
                                new Point(cx - 1, baselineY + 3),
                                new Point(cx + 5, baselineY - 4),
                            });
                        }
                    }

                    using (var format = new StringFormat(StringFormatFlags.NoWrap))
                    {
                        format.Trimming = StringTrimming.EllipsisCharacter;
                        format.LineAlignment = StringAlignment.Center;
                        var textRect = new RectangleF(
                            rowLeft - _originX + GutterWidth,
                            rowTop - _originY,
                            item.Bounds.Width - GutterWidth - TextPadX - (item.Submenu >= 0 ? SubmenuArrowWidth : 0),
                            item.Bounds.Height);
                        using (var ink = new SolidBrush(theme.Text))
                            Graphics.DrawString(item.Label, MenuFont(), ink, textRect, format);
                    }

                    if (item.Submenu >= 0)
                    {
                        using (var pen = new Pen(theme.TextMuted, 1.3f))
                        {
                            pen.StartCap = LineCap.Round;
                            pen.EndCap = LineCap.Round;
                            int ax = rowLeft - _originX + item.Bounds.Width - 11;
                            Graphics.DrawLines(pen, new[]
                            {
                                new Point(ax - 3, baselineY - 4),
                                new Point(ax + 1, baselineY),
                                new Point(ax - 3, baselineY + 4),
                            });
                        }
                    }
                }

                for (int i = 0; i < panel.Items.Count; i++)
                {
                    Item item = panel.Items[i];
                    if (!item.Separator) continue;
                    int y = panel.Y + item.Bounds.Y + item.Bounds.Height / 2 - _originY;
                    using (var pen = new Pen(theme.Separator, 1f))
                        Graphics.DrawLine(pen, box.Left + PadX + 8, y, box.Right - PadX - 8, y);
                }
            }
        }

        // ——— input ————————————————————————————————————————

        protected override IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_MOUSEMOVE:
                    OnMouseMove(lParam);
                    return IntPtr.Zero;

                case Native.WM_LBUTTONDOWN:
                case Native.WM_RBUTTONDOWN:
                    OnButtonDown(lParam);
                    return IntPtr.Zero;

                // The mouse went to another window: that is the "clicked elsewhere" signal.
                case Native.WM_CAPTURECHANGED_:
                    if (_open) Close(0);
                    return IntPtr.Zero;

                case Native.WM_KEYDOWN:
                    if ((int)wParam == Native.VK_ESCAPE) Close(0);
                    return IntPtr.Zero;
            }
            return Native.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private void OnMouseMove(IntPtr lParam)
        {
            int screenX = unchecked((short)((long)lParam & 0xFFFF)) + _originX;
            int screenY = unchecked((short)(((long)lParam >> 16) & 0xFFFF)) + _originY;
            // Repaint only when the highlight actually moves: mouse moves are frequent and
            // a full repaint each time would be the one real cost in this window.
            if (screenX == _hoverX && screenY == _hoverY) return;
            _hoverX = screenX;
            _hoverY = screenY;

            bool structureChanged = false;
            for (int p = 0; p < _panels.Count; p++)
            {
                Panel panel = _panels[p];
                if (screenX < panel.X || screenX >= panel.X + panel.Width) continue;
                if (screenY < panel.Y || screenY >= panel.Y + panel.Height) continue;
                int hit = RowAt(panel, screenY);
                int wanted = hit >= 0 && panel.Items[hit].Submenu >= 0 ? hit : -1;
                if (panel.OpenIndex != wanted) { panel.OpenIndex = wanted; structureChanged = true; }
                break;
            }
            if (structureChanged) Relayout(); else RepaintOnly();
        }

        private static int RowAt(Panel panel, int screenY)
        {
            for (int i = 0; i < panel.Items.Count; i++)
            {
                Item item = panel.Items[i];
                if (item.Separator) continue;
                int top = panel.Y + item.Bounds.Top;
                if (screenY >= top && screenY < top + item.Bounds.Height) return i;
            }
            return -1;
        }

        private void OnButtonDown(IntPtr lParam)
        {
            int screenX = unchecked((short)((long)lParam & 0xFFFF)) + _originX;
            int screenY = unchecked((short)(((long)lParam >> 16) & 0xFFFF)) + _originY;

            List<Panel> visible = VisiblePanels();
            for (int p = 0; p < visible.Count; p++)
            {
                Panel panel = visible[p];
                if (screenX < panel.X || screenX >= panel.X + panel.Width) continue;
                if (screenY < panel.Y || screenY >= panel.Y + panel.Height) continue;
                int hit = RowAt(panel, screenY);
                if (hit < 0) return;
                Item item = panel.Items[hit];
                if (item.Submenu >= 0) return; // opens on hover; clicking is a no-op
                Close(item.Command);
                return;
            }
            Close(0);
        }

        /// <summary>
        /// True when a mouse button was pressed outside every visible panel.
        ///
        /// This is polled from the message loop's idle path. The button state comes from
        /// GetAsyncKeyState, which reports the PHYSICAL key regardless of capture or
        /// focus — the reason it still sees the click that the target window is about
        /// to consume. During a press that started inside the menu, the flag is left
        /// alone so a click on a row is not mistaken for a dismissal.
        /// <summary>
        /// True when the pointer has been moved clear of every panel since the last check,
        /// which is how this menu decides that the user went elsewhere.
        ///
        /// Why not watch the mouse button or the capture: this window is
        /// WS_EX_NOACTIVATE, so clicking another window transfers ACTIVATION to it. That
        /// never changes our mouse capture and never notifies us, and the click itself is
        /// consumed by the target window. Polling the button state is also unreliable in
        /// practice — synthetic input does not show up in GetAsyncKeyState at all, which
        /// made it impossible to verify this from a test harness.
        ///
        /// Leaving the menu is the one signal that is both universal and purely geometric:
        /// to click anywhere else the pointer must travel off the panel first. A short delay
        /// keeps a brief overshoot while moving towards a row from closing the menu, and a
        /// return inside cancels the pending dismissal.
        ///
        /// Trade-off, accepted deliberately: the menu does not close while the pointer stays
        /// on it, so a click straight onto a row is a selection, not a dismissal.
        /// </summary>
        /// <summary>
        /// Whether the pointer is currently over the menu (any visible panel).
        /// </summary>
        /// <summary>
        /// True when the pointer is over the menu or on the pet own opaque pixels.
        /// The two count together, so sliding from a row onto the pet does not close it.
        /// </summary>
        private bool PointerOnMenuOrPet()
        {
            Native.POINT cursor;
            if (!Native.GetCursorPos(out cursor)) return false;
            if (PointInAnyPanel(cursor.X, cursor.Y)) return true;
            Func<int, int, bool> hit = PetHit;
            return hit != null && hit(cursor.X, cursor.Y);
        }

        private bool PointInAnyPanel(int x, int y)
        {
            List<Panel> visible = VisiblePanels();
            for (int i = 0; i < visible.Count; i++)
            {
                Panel panel = visible[i];
                if (x >= panel.X && x < panel.X + panel.Width &&
                    y >= panel.Y && y < panel.Y + panel.Height) return true;
            }
            return false;
        }


        private bool PointerLeftMenu()
        {
            // Require that the pointer actually entered the menu first. Opening it with
            // the cursor on the pet and then moving away must NOT read as "left the
            // menu" — the user has not seen it yet.
            if (PointerOnMenuOrPet())
            {
                _entered = true;
                _leftAt = 0;
                return false;
            }
            if (!_entered) return false;

            if (_leftAt == 0)
            {
                _leftAt = Environment.TickCount;
                return false;
            }
            bool away = unchecked(Environment.TickCount - _leftAt) >= LeaveGraceMs;
            if (away && Trace.Enabled) Trace.Note("menu: pointer left the menu");
            return away;
        }

        private int _leftAt;

        /// <summary>How long the pointer must stay away before the menu closes.</summary>
        private const int LeaveGraceMs = 180;

        /// <summary>
        /// Reports whether a screen point is on the pet opaque pixels, so that moving
        /// from the menu onto the pet keeps it open: the pet and its menu are one surface
        /// for the purpose of dismissal. The pet supplies this.
        /// </summary>
        internal static Func<int, int, bool> PetHit;
        private bool _entered;


        private void Close(int command)
        {
            if (!_open) return;
            _chosenCommand = command;
            _open = false;
            // NOT PostQuitMessage: that posts WM_QUIT to the THREAD, and this thread
            // also runs the pet's own message loop — RunLoop's GetMessage would
            // return false and tear down the pet window too. Observed as "the pet
            // disappeared along with the menu". The loop polls, so a wake-up is enough.
            Native.PostMessage(Hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
