using System.Runtime.InteropServices;

namespace DesktopNotes.Presentation;

internal static class StickerWindowStyle
{
    private const int ExtendedStyle = -20;
    private const long ToolWindow = 0x80;
    private const long AppWindow = 0x40000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint window, int index, int value);

    public static void HideFromWindowSwitcher(nint window)
    {
        var style = nint.Size == 8 ? GetWindowLongPtr(window, ExtendedStyle).ToInt64() : GetWindowLong(window, ExtendedStyle);
        style = (style | ToolWindow) & ~AppWindow;
        if (nint.Size == 8) SetWindowLongPtr(window, ExtendedStyle, (nint)style);
        else SetWindowLong(window, ExtendedStyle, (int)style);
    }
}
