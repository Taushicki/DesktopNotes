using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopNotes.Infrastructure;

// Active only while a group selection is pending. Does not consume, record or alter input.
public sealed class DesktopClickObserver : IDisposable
{
    private delegate nint HookProcedure(int code, nint message, nint data);
    private readonly HookProcedure _callback;
    private nint _hook;
    public event Action? UnmodifiedClick;
    public DesktopClickObserver() => _callback = Observe;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookExW(int type, HookProcedure callback, nint module, uint thread);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    public void Start()
    {
        if (_hook != 0) return;
        _hook = SetWindowsHookExW(14, _callback, GetModuleHandleW(null), 0);
        if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Stop() { if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; } }

    private nint Observe(int code, nint message, nint data)
    {
        if (code >= 0 && message.ToInt64() is 0x0201 or 0x0204 or 0x0207 && (GetAsyncKeyState(0x11) & 0x8000) == 0)
            UnmodifiedClick?.Invoke();
        return CallNextHookEx(_hook, code, message, data);
    }
    public void Dispose() => Stop();
}
