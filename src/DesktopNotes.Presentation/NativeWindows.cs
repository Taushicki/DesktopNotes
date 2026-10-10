using System.Runtime.InteropServices;
using System.Text;
using DesktopNotes.Application;

namespace DesktopNotes.Presentation;

[StructLayout(LayoutKind.Sequential)]
internal struct ScreenRect
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public readonly NoteBounds Bounds => new(Left, Top, Width, Height);
    public readonly ScreenRect Offset(int x, int y) => new() { Left = Left + x, Top = Top + y, Right = Right + x, Bottom = Bottom + y };
}

internal static class NativeWindows
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out ScreenRect rectangle);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flag);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int count);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    public static ScreenRect Rect(nint window) { GetWindowRect(window, out var rectangle); return rectangle; }
    public static (int X, int Y) Cursor() { GetCursorPos(out var point); return (point.X, point.Y); }
    public static nint ForegroundOwner() => GetAncestor(GetForegroundWindow(), 3);
    public static nint ForegroundWindow() => GetForegroundWindow();
    public static nint RootOwner(nint window) => GetAncestor(window, 3);
    public static nint RootOwnerUnderCursor() { GetCursorPos(out var point); return GetAncestor(WindowFromPoint(point), 3); }
    public static bool IsDesktopSurface(nint window)
    {
        var name = new StringBuilder(128);
        GetClassName(window, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW";
    }
    public static bool IsOwnWindow(nint window)
    {
        GetWindowThreadProcessId(window, out var processId);
        return processId == (uint)Environment.ProcessId;
    }
    public static nint RootUnderCursor() { GetCursorPos(out var point); return GetAncestor(WindowFromPoint(point), 2); }
    public static double Scale(nint window) { var dpi = GetDpiForWindow(window); return dpi == 0 ? 1 : dpi / 96.0; }
    public static bool IsShown(nint window) => window != 0 && IsWindowVisible(window) && !IsIconic(window);
    public static void Move(nint window, int x, int y) => SetWindowPos(window, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
    public static void SetBounds(nint window, ScreenRect rectangle)
    {
        var current = Rect(window);
        if (current.Left == rectangle.Left && current.Top == rectangle.Top && current.Right == rectangle.Right && current.Bottom == rectangle.Bottom) return;
        SetWindowPos(window, 0, rectangle.Left, rectangle.Top, rectangle.Width, rectangle.Height, 0x0004 | 0x0010);
    }
    public static void PutBehind(nint overlay, nint first, nint second)
    {
        var lower = first;
        var current = first;
        for (var i = 0; current != 0 && i < 4096; i++, current = GetWindow(current, 2))
            if (current == second) { lower = second; break; }
        if (GetWindow(overlay, 3) == lower) return;
        SetWindowPos(overlay, lower, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetLong(nint window, int index, int value);
    public static void MakeOverlay(nint window, bool interactive = false)
    {
        var style = nint.Size == 8 ? GetLongPtr(window, -20).ToInt64() : GetLong(window, -20);
        style = (style | 0x80) & ~0x40000L;
        style = interactive ? style & ~(0x20 | 0x08000000) : style | 0x20 | 0x08000000;
        if (nint.Size == 8) SetLongPtr(window, -20, (nint)style); else SetLong(window, -20, (int)style);
    }
}
