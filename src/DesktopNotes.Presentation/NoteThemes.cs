using System.Windows.Media;
using DesktopNotes.Domain;

namespace DesktopNotes.Presentation;

// Theme colours live at the presentation boundary; stored enum values remain stable.
internal sealed record NoteTheme(string Name, bool IsDark, SolidColorBrush Background, SolidColorBrush Foreground,
    SolidColorBrush CodeFill, Pen CodeOutline, SolidColorBrush InlineCode, SolidColorBrush Highlight,
    SolidColorBrush Link, SolidColorBrush ScrollThumb, SolidColorBrush Hover, SolidColorBrush Pressed);

internal static class NoteThemes
{
    private static readonly NoteTheme[] Themes =
    [
        Light("Жёлтый", "#FFF1A8"), Light("Персиковый", "#FFD9BC"), Light("Мятный", "#CFF0D6"),
        Light("Голубой", "#D3E9FC"), Light("Лавандовый", "#E3D9FA"), Light("Розовый", "#FAD8E4"),
        Dark("GitHub", "#0D1117", "#E6EDF3", "#161B22", "#30363D", "#79C0FF", "#484F58", "#21262D", "#30363D"),
        Dark("Material", "#263238", "#EEFFFF", "#1E272C", "#455A64", "#80CBC4", "#546E7A", "#37474F", "#455A64")
    ];

    internal static NoteTheme Get(NoteColor color) => Themes[(int)color];

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze(); return brush;
    }

    private static NoteTheme Light(string name, string background)
    {
        var bg = Brush(background);
        var c = bg.Color;
        var thumb = new SolidColorBrush(Color.FromRgb((byte)(c.R * .7), (byte)(c.G * .7), (byte)(c.B * .7)));
        thumb.Freeze();
        return Create(name, false, bg, Brush("#232C34"), Brush("#121E2837"), Brush("#41374150"),
            Brush("#10000000"), Brush("#FAFAD2"), Brush("#4682B4"), thumb, Brush("#E9ECEF"), Brush("#D4D7DA"));
    }

    private static NoteTheme Dark(string name, string background, string foreground, string code, string border,
        string link, string scroll, string hover, string pressed) =>
        Create(name, true, Brush(background), Brush(foreground), Brush(code), Brush(border), Brush(code),
            Brush("#574B22"), Brush(link), Brush(scroll), Brush(hover), Brush(pressed));

    private static NoteTheme Create(string name, bool dark, SolidColorBrush background, SolidColorBrush foreground,
        SolidColorBrush code, SolidColorBrush border, SolidColorBrush inlineCode, SolidColorBrush highlight,
        SolidColorBrush link, SolidColorBrush scroll, SolidColorBrush hover, SolidColorBrush pressed)
    {
        var outline = new Pen(border, 1); outline.Freeze();
        return new(name, dark, background, foreground, code, outline, inlineCode, highlight, link, scroll, hover, pressed);
    }
}
