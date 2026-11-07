using System;
using System.Diagnostics;
#if HAS_UIA
using System.Windows.Automation;
#endif

namespace DshLitePet
{
    /// <summary>
    /// Asks the shell to show its window by invoking the Harness tray icon's own command.
    ///
    /// Nothing here synthesises input: there is no cursor movement, no coordinate, and no
    /// mouse or keyboard event. UI Automation exposes the Windows 11 taskbar icon as a
    /// `SystemTray.NormalButton` with an InvokePattern, and invoking it runs the very same
    /// handler a real click runs — the mechanism a screen reader uses. It is also immune to
    /// whatever happens to be covering the taskbar.
    ///
    /// Why the tray route and not the window APIs: the pet cannot re-show a window that the
    /// shell hid with window.hide(). Measured on this machine from the pet process,
    ///     ShowWindow(SW_SHOW) / ShowWindow(SW_RESTORE)
    ///     SetWindowPos(..., SWP_SHOWWINDOW | SWP_FRAMECHANGED)
    ///     SetWindowLong(GWL_STYLE | WS_VISIBLE)
    ///     ShellExecuteEx("dsh://open")   -> only moves the foreground
    /// all leave IsWindowVisible false. The identical SetWindowLong did work from an
    /// interactive console process, so the limitation is about who asks, not which API.
    /// </summary>
    internal static class TrayActivation
    {
        /// <summary>Window title suffix of the Electron shell, also the tray icon's tooltip.</summary>
        private const string ShellTitle = "DeepSeek Harness";

        /// <summary>Own clock: the pet's main one lives in Program and is private.</summary>
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>True when the tray icon was found and its command was invoked.</summary>
        internal static bool TryClick()
        {
#if HAS_UIA
            // UI Automation is documented for STA callers. The click runs on its own STA
            // thread and this method waits for it, so the result is still synchronous.
            bool clicked = false;
            Exception failure = null;
            var worker = new System.Threading.Thread(delegate()
            {
                try { clicked = Click(); }
                catch (Exception error) { failure = error; }
            });
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.IsBackground = true;
            worker.Start();
            bool finished = worker.Join(9000);
            if (Trace.Enabled)
                Trace.Note("tray: finished=" + finished + " clicked=" + clicked +
                    " failure=" + (failure == null ? "none" : failure.GetType().Name + ": " + failure.Message));
            return clicked;
#else
            // Built without UI Automation (see tools/build-pet.mjs); the caller then falls
            // back to the dsh:// protocol.
            return false;
#endif
        }

#if HAS_UIA
        private static bool Click()
        {
            AutomationElementCollection taskbars = Taskbars();
            int count = taskbars == null ? 0 : taskbars.Count;
            AutomationElement icon = FindIcon(taskbars);
            if (Trace.Enabled) Trace.Note("tray: taskbars=" + count + " icon=" + (icon == null ? "null" : Describe(icon)));

            if (icon == null)
            {
                // Windows folds rarely used icons into the overflow flyout, and those
                // elements only exist in the tree while the flyout is open. Open it with
                // its own chevron command, look again, and close it the same way after.
                AutomationElement chevron = FindOverflowChevron(taskbars);
                if (chevron != null && Invoke(chevron))
                {
                    if (Trace.Enabled) Trace.Note("tray: opened the overflow flyout");
                    icon = FindIcon(WaitForOverflow(1500));
                    if (icon == null) icon = FindIcon(OverflowWindow());
                    bool clickedHidden = icon != null && Invoke(icon);
                    if (Trace.Enabled) Trace.Note("tray: overflow icon clicked=" + clickedHidden);
                    AutomationElement close = FindOverflowChevron(taskbars);
                    if (close != null) Invoke(close);
                    if (clickedHidden) return true;
                }
            }

            if (icon == null) return false;
            bool clicked = Invoke(icon);
            if (Trace.Enabled) Trace.Note("tray: clicked=" + clicked);
            return clicked;
        }

        /// <summary>
        /// Every taskbar in the tree. There can be more than one `Shell_TrayWnd`, and only
        /// the one that hosts the icons has children, so all of them are searched.
        /// </summary>
        private static AutomationElementCollection Taskbars()
        {
            AutomationElement root = AutomationElement.RootElement;
            if (root == null) return null;
            var condition = new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd");
            AutomationElementCollection found = root.FindAll(TreeScope.Children, condition);
            if (found.Count == 0) found = root.FindAll(TreeScope.Descendants, condition);
            return found;
        }

        private static AutomationElement OverflowWindow()
        {
            AutomationElement root = AutomationElement.RootElement;
            if (root == null) return null;
            string[] classNames = { "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow" };
            for (int index = 0; index < classNames.Length; index++)
            {
                AutomationElement found = root.FindFirst(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ClassNameProperty, classNames[index]));
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>Finds the icon across every taskbar; it lives on exactly one of them.</summary>
        private static AutomationElement FindIcon(AutomationElementCollection roots)
        {
            if (roots == null) return null;
            foreach (AutomationElement root in roots)
            {
                AutomationElement icon = FindIcon(root);
                if (icon != null) return icon;
            }
            return null;
        }

        /// <summary>
        /// Finds the icon whose accessible name is the app's tooltip. Windows 11 prefixes
        /// the name with a space, so the comparison trims before matching a prefix.
        /// </summary>
        private static AutomationElement FindIcon(AutomationElement root)
        {
            if (root == null) return null;
            AutomationElementCollection all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement element in all)
            {
                string className;
                string name;
                if (!TryRead(element, out className, out name)) continue;
                if (className == null || name == null) continue;
                if (!className.StartsWith("SystemTray.", StringComparison.Ordinal)) continue;
                string text = name.Trim();
                if (text == ShellTitle || text.StartsWith(ShellTitle, StringComparison.Ordinal))
                    return element;
            }
            return null;
        }

        private static AutomationElement FindOverflowChevron(AutomationElementCollection roots)
        {
            if (roots == null) return null;
            foreach (AutomationElement root in roots)
            {
                AutomationElement chevron = FindOverflowChevron(root);
                if (chevron != null) return chevron;
            }
            return null;
        }

        /// <summary>
        /// The "show hidden icons" chevron. Its name is localised, so match the spellings
        /// this machine and an English build use.
        /// </summary>
        private static AutomationElement FindOverflowChevron(AutomationElement root)
        {
            if (root == null) return null;
            string[] hints = { "隐藏的图标", "隐藏图标", "Show hidden icons", "Hidden icons" };
            AutomationElementCollection all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            foreach (AutomationElement element in all)
            {
                string className;
                string name;
                if (!TryRead(element, out className, out name)) continue;
                if (className == null || name == null) continue;
                if (!className.StartsWith("SystemTray.", StringComparison.Ordinal)) continue;
                for (int index = 0; index < hints.Length; index++)
                {
                    if (name.IndexOf(hints[index], StringComparison.OrdinalIgnoreCase) >= 0)
                        return element;
                }
            }
            return null;
        }

        private static AutomationElement WaitForOverflow(int timeoutMs)
        {
            long deadline = Clock.ElapsedMilliseconds + timeoutMs;
            while (true)
            {
                AutomationElement found = OverflowWindow();
                if (found != null) return found;
                if (Clock.ElapsedMilliseconds >= deadline) return null;
                System.Threading.Thread.Sleep(80);
            }
        }

        private static string Describe(AutomationElement element)
        {
            string className;
            string name;
            if (!TryRead(element, out className, out name)) return "unreadable";
            return "'" + name + "'/" + className;
        }

        private static bool TryRead(AutomationElement element, out string className, out string name)
        {
            className = null;
            name = null;
            try
            {
                className = element.Current.ClassName;
                name = element.Current.Name;
                return true;
            }
            catch (Exception)
            {
                // Elements come and go while the taskbar animates.
                return false;
            }
        }

        private static bool Invoke(AutomationElement element)
        {
            object pattern;
            if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) return false;
            ((InvokePattern)pattern).Invoke();
            return true;
        }
#endif
    }
}
