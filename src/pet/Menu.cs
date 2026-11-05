// Native popup menu: 动作 ▸ (category ▸ animation), 查余额, 关闭桌宠.
using System;
using System.Collections.Generic;

namespace DshLitePet
{
    internal sealed class PetMenu
    {
        public const int CommandBalance = 1;
        public const int CommandQuit = 2;
        private const int AnimationBase = 100;

        private readonly Catalog _catalog;

        public PetMenu(Catalog catalog)
        {
            _catalog = catalog;
        }

        /// <summary>Shows the menu and returns the chosen CatalogEntry (when an animation was picked) plus a command.</summary>
        public int Show(IntPtr hwnd, out CatalogEntry chosen)
        {
            chosen = null;
            IntPtr root = Native.CreatePopupMenu();
            var groups = new List<IntPtr>();
            try
            {
                IntPtr actions = Native.CreatePopupMenu();
                groups.Add(actions);
                foreach (string group in _catalog.Groups())
                {
                    IntPtr sub = Native.CreatePopupMenu();
                    groups.Add(sub);
                    int index = 0;
                    foreach (CatalogEntry entry in _catalog.ByGroup(group))
                    {
                        Native.AppendMenu(sub, Native.MF_STRING, (UIntPtr)(AnimationBase + index), entry.Name);
                        _commandMap[AnimationBase + index] = entry;
                        index++;
                    }
                    Native.AppendMenu(actions, Native.MF_POPUP | Native.MF_STRING, unchecked((UIntPtr)sub.ToInt64()), group);
                }
                Native.AppendMenu(root, Native.MF_POPUP | Native.MF_STRING, unchecked((UIntPtr)actions.ToInt64()), "动作");
                Native.AppendMenu(root, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                Native.AppendMenu(root, Native.MF_STRING, (UIntPtr)CommandBalance, "查余额");
                Native.AppendMenu(root, Native.MF_STRING, (UIntPtr)CommandQuit, "关闭桌宠");

                Native.POINT pt;
                Native.GetCursorPos(out pt);
                Native.SetForegroundWindow(hwnd);
                int command = Native.TrackPopupMenuEx(root, Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NONOTIFY,
                    pt.X, pt.Y, hwnd, IntPtr.Zero);
                if (command >= AnimationBase && _commandMap.ContainsKey(command))
                    chosen = _commandMap[command];
                return command;
            }
            finally
            {
                Native.DestroyMenu(root);
                _commandMap.Clear();
            }
        }

        private readonly Dictionary<int, CatalogEntry> _commandMap = new Dictionary<int, CatalogEntry>();
    }
}

