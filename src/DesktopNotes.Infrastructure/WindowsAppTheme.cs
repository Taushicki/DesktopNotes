using Microsoft.Win32;

namespace DesktopNotes.Infrastructure;

/// <summary>Reads the current user preference each time a system-themed surface opens.</summary>
public static class WindowsAppTheme
{
    public static bool IsDark => ReadDarkPreference("AppsUseLightTheme", false);
    public static bool IsTaskbarDark => ReadDarkPreference("SystemUsesLightTheme", true);

    private static bool ReadDarkPreference(string valueName, bool fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(valueName) is int value ? value == 0 : fallback;
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return fallback;
        }
    }
}
