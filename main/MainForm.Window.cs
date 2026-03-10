#if WINDOWS
using System.Runtime.InteropServices;
#endif

namespace VRCNext;

public partial class MainForm
{
#if WINDOWS
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int SendMessage(nint hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll")] private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint Msg, nint wParam, nint lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MARGINS { public int leftWidth, rightWidth, topHeight, bottomHeight; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);
    private static WndProcDelegate? _subclassProc; // must stay rooted — prevents GC collection
    private static nint _origWndProc;

    private static nint SubclassWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        const uint WM_NCCALCSIZE = 0x0083;
        const uint WM_NCHITTEST  = 0x0084;

        if (msg == WM_NCCALCSIZE && wParam == 1)
            return 0; // client area = full window rect; removes visible NC border/black strip; snap still works

        if (msg == WM_NCHITTEST)
        {
            var hit = CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam);
            if (hit == 1 /*HTCLIENT*/)
            {
                const int border = 8;
                int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
                int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                GetWindowRect(hWnd, out var rc);
                bool l = x < rc.Left   + border, r = x > rc.Right  - border;
                bool t = y < rc.Top    + border, b = y > rc.Bottom - border;
                if (t && l) return 13; if (t && r) return 14;
                if (b && l) return 16; if (b && r) return 17;
                if (t) return 12; if (b) return 15;
                if (l) return 10; if (r) return 11;
            }
            return hit;
        }

        return CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam);
    }

    private void InstallWndProcSubclass(nint hWnd)
    {
        if (_origWndProc != 0) return; // already installed — don't re-hook or _origWndProc points to itself
        _subclassProc = SubclassWndProc;
        _origWndProc  = SetWindowLongPtr(hWnd, -4 /*GWLP_WNDPROC*/,
            System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_subclassProc));
    }
#endif

    /// <summary>Returns x,y to center a window of the given size on the primary screen.</summary>
    private static (int x, int y) GetCenteredLocation(int w, int h)
    {
#if WINDOWS
        int sw = GetSystemMetrics(0); // SM_CXSCREEN
        int sh = GetSystemMetrics(1); // SM_CYSCREEN
        return (Math.Max(0, (sw - w) / 2), Math.Max(0, (sh - h) / 2));
#else
        return (100, 50);
#endif
    }
}
