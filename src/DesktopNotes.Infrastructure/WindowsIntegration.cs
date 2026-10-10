using System.ComponentModel;
using System.Runtime.InteropServices;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using Microsoft.Win32;

namespace DesktopNotes.Infrastructure;

public sealed class WindowLayerController : IWindowLayerController
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    public void Apply(nint handle, WindowLayer layer)
    {
        if (handle == 0) return;
        // TOPMOST=-1, NOTOPMOST=-2, BOTTOM=1. Preserve position, size and focus.
        var after = layer switch { WindowLayer.Topmost => (nint)(-1), WindowLayer.Background => (nint)1, _ => (nint)(-2) };
        if (!SetWindowPos(handle, after, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}

public sealed class DesktopMenuRegistration(string executable, Func<string>? caption = null) : IDesktopMenuRegistration
{
    private const string Key = @"Software\Classes\DesktopBackground\shell\DesktopNotes";
    public bool IsRegistered { get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key is not null; } }
    public void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        key.SetValue("", caption?.Invoke() ?? "Создать заметку");
        key.SetValue("Icon", $"\"{executable}\",0");
        using var command = key.CreateSubKey("command");
        command.SetValue("", $"\"{executable}\" --new-note");
    }
    public void Unregister() => Registry.CurrentUser.DeleteSubKeyTree(Key, false);
}
