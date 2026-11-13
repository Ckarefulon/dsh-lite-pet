// Verifies the pet's window stacking order against a real desktop.
//
// The pet, its bubble and its menu are three separate topmost windows. Windows orders the
// topmost band by whichever window was raised last, so a single band-wide raise silently
// demotes the others — the reported "气泡上下跳 / 被宠物压着".
//
// This program proves the two halves of the fix on a real desktop:
//
//   * a band-wide raise of the pet DOES bury a sibling bubble, so the guard tests something
//     that genuinely regresses (expected-outcome assertions, not just "no failures");
//   * an OWNED bubble is never buried, no matter how often the pet is raised or moved —
//     ownership is the actual mechanism, not a per-tick repair.
//   * relative placement (`hWndInsertAfter` = a specific window) keeps all three ordered,
//     including the bubble/menu sibling pair that ownership cannot express.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

internal static class StackProbe
{
    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TOOLWINDOW = 0x80;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOPMOST = 0x08;
    const int ULW_ALPHA = 2;
    const int SW_SHOWNOACTIVATE = 4;
    const int SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 4, SWP_NOOWNERZORDER = 0x200;
    const int GW_OWNER = 4;

    delegate IntPtr WndProcD(IntPtr h, uint m, IntPtr w, IntPtr l);
    delegate bool EnumD(IntPtr h, IntPtr l);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX, CY; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte a, b, c, d; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int e, string c, string n, int s, int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr pa);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, int f);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumD d, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr d);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, int c);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr d);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr d, ref BITMAPINFOHEADER b, uint u, out IntPtr bits, IntPtr s, uint o);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr d, IntPtr o);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dd, ref POINT dst, ref SIZE size, IntPtr sd, ref POINT src, int ck, ref BLENDFUNCTION bf, int f);

    static WndProcD proc = WndProc;
    static IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l) { return DefWindowProc(h, m, w, l); }

    static IntPtr dc, bits;
    static void Surface()
    {
        if (dc != IntPtr.Zero) return;
        IntPtr screen = GetDC(IntPtr.Zero);
        dc = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);
        var bmi = new BITMAPINFOHEADER();
        bmi.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        bmi.biWidth = 1024; bmi.biHeight = -1024; bmi.biPlanes = 1; bmi.biBitCount = 32;
        IntPtr dib = CreateDIBSection(dc, ref bmi, 0, out bits, IntPtr.Zero, 0);
        SelectObject(dc, dib);
    }

    static void Paint(IntPtr h, int x, int y, int w, int hh)
    {
        Surface();
        var dst = new POINT(); dst.X = x; dst.Y = y;
        var size = new SIZE(); size.CX = w; size.CY = hh;
        var src = new POINT();
        var bf = new BLENDFUNCTION(); bf.c = 255; bf.d = 1;
        UpdateLayeredWindow(h, IntPtr.Zero, ref dst, ref size, dc, ref src, 0, ref bf, ULW_ALPHA);
    }

    static readonly List<IntPtr> Ours = new List<IntPtr>();
    static IntPtr Make(string cls, IntPtr inst, IntPtr owner)
    {
        var wc = new WNDCLASSEX();
        wc.cbSize = Marshal.SizeOf(typeof(WNDCLASSEX));
        wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc);
        wc.hInstance = inst;
        wc.lpszClassName = cls;
        RegisterClassEx(ref wc);
        IntPtr h = CreateWindowEx(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            cls, cls, WS_POPUP, 300, 300, 300, 160, owner, IntPtr.Zero, inst, IntPtr.Zero);
        Ours.Add(h);
        ShowWindow(h, SW_SHOWNOACTIVATE);
        return h;
    }

    /// <summary>Index among our windows in the real desktop z-order; lower = more in front.</summary>
    static Dictionary<IntPtr, int> Order()
    {
        var map = new Dictionary<IntPtr, int>();
        int idx = 0;
        EnumWindows((h, l) =>
        {
            if (Ours.Contains(h)) map[h] = idx;
            idx++;
            return true;
        }, IntPtr.Zero);
        return map;
    }

    static string Verdict(IntPtr pet, IntPtr bubble, IntPtr menu, bool menuOpen)
    {
        var order = Order();
        int p = order[pet], b = order[bubble];
        if (p <= b) return "pet covers bubble";
        if (menuOpen)
        {
            int m = order[menu];
            if (m >= b) return "menu behind bubble";
            if (m >= p) return "menu behind pet";
        }
        return "ok";
    }

    static void Raise(IntPtr h)
    {
        SetWindowPos(h, (IntPtr)(-1), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    /// <summary>Relative placement: put `behind`'s window directly in front of `above`.</summary>
    static void PlaceInFront(IntPtr behind, IntPtr above)
    {
        SetWindowPos(behind, above, 0, 0, 0, 0,
            SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>Exactly what Program.Restack does now: relative placement, no band raise.</summary>
    static void Restack(IntPtr pet, IntPtr bubble, IntPtr menu, bool menuOpen, bool bubbleOpen)
    {
        if (menuOpen) Raise(menu);          // front-most: the one place a band raise is correct
        if (bubbleOpen) PlaceInFront(pet, bubble);
        if (bubbleOpen && menuOpen) PlaceInFront(bubble, menu);
    }

    static int _failures;
    static int _checks;

    static void Check(string label, string got, bool shouldBeOk)
    {
        _checks++;
        bool ok = got == "ok";
        bool pass = ok == shouldBeOk;
        Console.WriteLine((pass ? "[stack] pass  " : "[stack] FAIL  ") + label + " -> " + got);
        if (!pass) _failures++;
    }

    static int Main()
    {
        IntPtr inst = GetModuleHandle(null);
        IntPtr pet = Make("StackProbePet", inst, IntPtr.Zero);
        IntPtr bubble = Make("StackProbeBubble", inst, IntPtr.Zero);
        IntPtr menu = Make("StackProbeMenu", inst, IntPtr.Zero);
        if (pet == IntPtr.Zero || bubble == IntPtr.Zero || menu == IntPtr.Zero)
        {
            Console.WriteLine("[stack] FAIL could not create the probe windows");
            return 1;
        }
        Paint(pet, 300, 300, 300, 160);
        Paint(bubble, 320, 240, 260, 40);
        Paint(menu, 340, 320, 200, 120);

        // ——— the old, sibling arrangement: the bug must still reproduce ———
        Restack(pet, bubble, menu, false, true);
        Raise(pet);
        Check("drag raises the pet alone (must reproduce)", Verdict(pet, bubble, menu, false), false);

        Restack(pet, bubble, menu, false, true);
        Check("drag then relative Restack", Verdict(pet, bubble, menu, false), true);

        // A new message repaints the bubble, which raises it while the menu is open.
        Paint(bubble, 320, 240, 260, 40);
        Raise(bubble);
        Check("bubble repaint alone (must reproduce)", Verdict(pet, bubble, menu, true), false);

        Restack(pet, bubble, menu, true, true);
        Check("bubble repaint then relative Restack", Verdict(pet, bubble, menu, true), true);

        // A scale change moves AND resizes the pet.
        SetWindowPos(pet, (IntPtr)(-1), 260, 260, 360, 200, SWP_NOACTIVATE);
        Check("scale change raises the pet alone (must reproduce)", Verdict(pet, bubble, menu, true), false);

        Restack(pet, bubble, menu, true, true);
        Check("scale change then relative Restack", Verdict(pet, bubble, menu, true), true);

        // ——— the new arrangement: the bubble is OWNED by the pet ———
        // Ownership is what makes the fix immune to any number of raises, so it is measured
        // directly rather than inferred from the absence of a failure.
        IntPtr ownedBubble = Make("StackProbeOwnedBubble", inst, pet);
        IntPtr ownedMenu = Make("StackProbeOwnedMenu", inst, pet);
        Paint(ownedBubble, 322, 236, 260, 40);
        Paint(ownedMenu, 342, 318, 200, 120);
        Check("the probe recorded the owner", GetWindow(ownedBubble, GW_OWNER) == pet ? "ok" : "no owner", true);

        bool buried = false;
        for (int i = 0; i < 20 && !buried; i++)
        {
            // The exact call a drag used to make on every tick.
            SetWindowPos(pet, (IntPtr)(-1), 300 + i, 300, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
            if (Verdict(pet, ownedBubble, ownedMenu, false) != "ok") buried = true;
        }
        Check("owned bubble survives 20 band raises of the pet", buried ? "pet covers bubble" : "ok", true);

        // Ownership cannot order the two siblings against each other; relative placement can.
        Restack(pet, ownedBubble, ownedMenu, true, true);
        Check("owned menu in front of the owned bubble", Verdict(pet, ownedBubble, ownedMenu, true), true);
        Raise(ownedBubble);
        Check("bubble raised above the menu (must reproduce)", Verdict(pet, ownedBubble, ownedMenu, true), false);
        Restack(pet, ownedBubble, ownedMenu, true, true);
        Check("relative Restack repairs the sibling order", Verdict(pet, ownedBubble, ownedMenu, true), true);

        // With the menu closed the bubble still has to sit above the pet.
        Restack(pet, ownedBubble, ownedMenu, false, true);
        Check("menu closed, relative Restack", Verdict(pet, ownedBubble, ownedMenu, false), true);

        Console.WriteLine(_failures == 0
            ? "[stack] ok — " + _checks + " checks; a band raise still buries a sibling, ownership and relative placement do not"
            : "[stack] " + _failures + " of " + _checks + " checks failed");
        return _failures == 0 ? 0 : 1;
    }
}
