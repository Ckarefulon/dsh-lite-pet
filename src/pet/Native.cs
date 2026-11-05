// Win32 interop for the desktop pet window. Only what the pet actually uses:
// a layered popup window, GDI memory DC + DIB section, hit testing, and popup menus.
using System;
using System.Runtime.InteropServices;

namespace DshLitePet
{
    internal static class Native
    {
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int ULW_ALPHA = 0x00000002;
        public const byte AC_SRC_ALPHA = 0x01;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int HTTRANSPARENT = -1;
        public const int HTCLIENT = 1;
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_TIMER = 0x0113;
        public const int WM_DESTROY = 0x0002;

        public const int HWND_TOPMOST = -1;
        public const int SWP_NOSIZE = 0x0001;
        public const int SWP_NOMOVE = 0x0002;
        public const int SWP_NOACTIVATE = 0x0010;

        public const uint MF_STRING = 0x00000000;
        public const uint MF_POPUP = 0x00000010;
        public const uint MF_SEPARATOR = 0x00000800;
        public const uint TPM_RIGHTBUTTON = 0x0002;
        public const uint TPM_NONOTIFY = 0x0080;
        public const uint TPM_RETURNCMD = 0x0100;

        public const uint SPI_GETWORKAREA = 0x0030;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int CX, CY; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public int cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam, lParam;
            public uint time;
            public POINT pt;
        }

        public delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);


        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int code);

        [DllImport("user32.dll")]
        public static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint elapse, IntPtr proc);

        [DllImport("user32.dll")]
        public static extern bool KillTimer(IntPtr hwnd, IntPtr id);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);



        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT pt);



        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);

        [DllImport("user32.dll")]
        public static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint winIni);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size,
            IntPtr srcDc, ref POINT src, int colorKey, ref BLENDFUNCTION blend, int flags);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, int usage, out IntPtr bits, IntPtr section, int offset);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr dc);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string item);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr paramsPtr);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);


        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

        /// <summary>Creates a top-down 32bpp DIB and its memory DC. The bits are BGRA, premultiplied on present.</summary>
        public sealed class LayeredSurface : IDisposable
        {
            public readonly int Width;
            public readonly int Height;
            public readonly IntPtr MemoryDc;
            public readonly IntPtr Bits;
            private readonly IntPtr _dib;

            public LayeredSurface(int width, int height)
            {
                Width = width;
                Height = height;
                IntPtr screen = GetDC(IntPtr.Zero);
                MemoryDc = CreateCompatibleDC(screen);
                ReleaseDC(IntPtr.Zero, screen);
                var bmi = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                };
                _dib = CreateDIBSection(MemoryDc, ref bmi, 0, out Bits, IntPtr.Zero, 0);
                SelectObject(MemoryDc, _dib);
            }

            public void Present(IntPtr hwnd, int x, int y)
            {
                var dst = new POINT { X = x, Y = y };
                var size = new SIZE { CX = Width, CY = Height };
                var src = new POINT();
                var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
                UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, MemoryDc, ref src, 0, ref blend, ULW_ALPHA);
            }

            /// <summary>Copies a premultiplied BGRA canvas into the DIB bits.</summary>
            public void Upload(byte[] bgra)
            {
                Marshal.Copy(bgra, 0, Bits, bgra.Length);
            }

            public void Dispose()
            {
                DeleteObject(_dib);
                DeleteDC(MemoryDc);
            }
        }
    }
}
