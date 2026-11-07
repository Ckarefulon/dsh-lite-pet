// Native popup menu: 动作 ▸ (category ▸ animation), 调整大小 ▸ presets, 查余额, 关闭桌宠.
using System;
using System.Collections.Generic;

namespace DshLitePet
{
    internal sealed class PetMenu
    {
        public const int CommandBalance = 1;
        public const int CommandQuit = 2;
        private const int ScaleBase = 10;
        private const int AnimationBase = 100;

        // Three sizes, anchored on the user's current setting as the middle (中).
        // 0.75 is what the pet shipped with, so 中 keeps the familiar look.
        public static readonly double[] Scales = { 0.55, 0.75, 1.0 };
        private static readonly string[] ScaleLabels = { "小", "中", "大" };

        private readonly Catalog _catalog;
        private readonly Dictionary<int, CatalogEntry> _commands = new Dictionary<int, CatalogEntry>();

        public PetMenu(Catalog catalog)
        {
            _catalog = catalog;
        }

        /// <summary>Shows the menu. Returns the chosen command and, for 调整大小, the requested scale.</summary>
        public int Show(IntPtr hwnd, double currentScale, out CatalogEntry chosen, out double chosenScale)
        {
            chosen = null;
            chosenScale = 0;
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
                        _commands[AnimationBase + index] = entry;
                        index++;
                    }
                    Native.AppendMenu(actions, Native.MF_POPUP | Native.MF_STRING, unchecked((UIntPtr)sub.ToInt64()), group);
                }
                Native.AppendMenu(root, Native.MF_POPUP | Native.MF_STRING, unchecked((UIntPtr)actions.ToInt64()), "动作");

                IntPtr sizes = Native.CreatePopupMenu();
                groups.Add(sizes);
                for (int i = 0; i < Scales.Length; i++)
                {
                    uint flags = Native.MF_STRING;
                    if (Math.Abs(Scales[i] - currentScale) < 0.01) flags |= Native.MF_CHECKED;
                    Native.AppendMenu(sizes, flags, (UIntPtr)(ScaleBase + i), ScaleLabels[i]);
                }
                Native.AppendMenu(root, Native.MF_POPUP | Native.MF_STRING, unchecked((UIntPtr)sizes.ToInt64()), "调整大小");

                Native.AppendMenu(root, Native.MF_SEPARATOR, UIntPtr.Zero, null);
                Native.AppendMenu(root, Native.MF_STRING, (UIntPtr)CommandBalance, "查余额");
                Native.AppendMenu(root, Native.MF_STRING, (UIntPtr)CommandQuit, "关闭桌宠");

                Native.POINT point;
                Native.GetCursorPos(out point);
                Native.SetForegroundWindow(hwnd);
                int command = Native.TrackPopupMenuEx(
                    root,
                    Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD | Native.TPM_NONOTIFY,
                    point.X, point.Y, hwnd, IntPtr.Zero);

                if (command >= AnimationBase && _commands.ContainsKey(command)) chosen = _commands[command];
                if (command >= ScaleBase && command < ScaleBase + Scales.Length) chosenScale = Scales[command - ScaleBase];
                return command;
            }
            finally
            {
                Native.DestroyMenu(root);
                _commands.Clear();
            }
        }
    }
}

