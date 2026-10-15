using System.Drawing;

namespace DesktopNotes.Presentation;

internal static class AppIcons
{
    internal static Icon LoadTray(bool darkTaskbar, int size)
    {
        var name = darkTaskbar ? "TrayLight.ico" : "TrayDark.ico";
        var uri = new Uri($"pack://application:,,,/DesktopNotes;component/Assets/{name}");
        using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream
            ?? throw new InvalidOperationException($"Missing icon resource: {name}");
        using var icon = new Icon(stream, size, size);
        return (Icon)icon.Clone();
    }
}
