using Microsoft.Win32;

namespace DesktopNotes.Infrastructure;

/// <summary>Reads the current user preference each time a system-themed surface opens.</summary>
public static class WindowsAppTheme
{
    public static bool IsDark
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }
}
