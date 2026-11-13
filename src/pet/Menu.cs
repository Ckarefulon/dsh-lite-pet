// The pet's right-click menu: 动作 ▸ (category ▸ animation), 调整大小 ▸ presets,
// 查余额, 关闭桌宠.
//
// This used to be a native TrackPopupMenuEx, which cannot both avoid taking focus and
// dismiss itself when the user clicks elsewhere — the two requirements are mutually
// exclusive on that API. See PopupMenu.cs for the measurements. The building and lookup
// logic lives here; the drawing and the dismissal live there.
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
        private PopupMenu _popup;

        public PetMenu(Catalog catalog)
        {
            _catalog = catalog;
        }

        /// <summary>
        /// The open menu's window handle, or zero when it is closed. The pet needs it to place
        /// the menu in front of the bubble: both are owned by the pet, so ownership alone cannot
        /// order them against each other.
        /// </summary>
        internal IntPtr HandleWhenOpen { get { return _popup == null ? IntPtr.Zero : _popup.HandleWhenOpen; } }

        /// <summary>Gives the popup the pet as its owner the first time it is shown.</summary>
        internal void SetOwner(IntPtr pet)
        {
            if (_popup == null) _popup = new PopupMenu();
            _popup.AfterTarget = pet;
        }

        /// <summary>Shows the menu. Returns the chosen command and, for 调整大小, the requested scale.</summary>
        public int Show(IntPtr owner, double currentScale, out CatalogEntry chosen, out double chosenScale)
        {
            chosen = null;
            chosenScale = 0;

            if (_popup == null) _popup = new PopupMenu();
            else _popup.Reset();
            _commands.Clear();

            // Command ids must be unique across the WHOLE menu, not per submenu. A per-group
            // index made every group's first entry id 100 and the dictionary kept only the
            // last one, so the wrong clip played.
            int nextCommand = AnimationBase;

            PopupMenu.Panel actions = _popup.AddSubmenu("动作");
            foreach (string group in _catalog.Groups())
            {
                PopupMenu.Panel sub = _popup.AddSubmenuTo(actions, group);
                foreach (CatalogEntry entry in _catalog.ByGroup(group))
                {
                    int command = nextCommand++;
                    _popup.AddActionTo(sub, entry.Name, command);
                    _commands[command] = entry;
                }
            }

            PopupMenu.Panel sizes = _popup.AddSubmenu("调整大小");
            for (int i = 0; i < Scales.Length; i++)
            {
                _popup.AddCheckableTo(sizes, ScaleLabels[i], ScaleBase + i,
                    Math.Abs(Scales[i] - currentScale) < 0.01);
            }

            _popup.AddSeparator();
            _popup.AddAction("查余额", CommandBalance);
            _popup.AddAction("关闭桌宠", CommandQuit);

            int command2 = _popup.Show();

            if (command2 >= AnimationBase && _commands.ContainsKey(command2)) chosen = _commands[command2];
            if (command2 >= ScaleBase && command2 < ScaleBase + Scales.Length) chosenScale = Scales[command2 - ScaleBase];
            _commands.Clear();
            return command2;
        }
    }
}
