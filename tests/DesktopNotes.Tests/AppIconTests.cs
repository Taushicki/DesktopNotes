using DesktopNotes.Presentation;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class AppIconTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(64)]
    public void TrayIconsRemainUsableAfterResourceStreamClosesAndContrastWithTaskbar(int size)
    {
        using var light = AppIcons.LoadTray(true, size);
        using var dark = AppIcons.LoadTray(false, size);
        using var lightBitmap = light.ToBitmap();
        using var darkBitmap = dark.ToBitmap();
        Assert.Equal(size, light.Width);
        Assert.Equal(size, dark.Width);
        Assert.Equal(0, lightBitmap.GetPixel(size - 1, 0).A);
        Assert.Equal(0, darkBitmap.GetPixel(size - 1, 0).A);
        var foreground = lightBitmap.GetPixel(size / 4, size / 4);
        var inverse = darkBitmap.GetPixel(size / 4, size / 4);
        Assert.Equal(255, foreground.A);
        Assert.Equal(255, inverse.A);
        Assert.True(foreground.R > 240);
        Assert.True(inverse.R < 60);
    }
}
