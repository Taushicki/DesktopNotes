using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DesktopNotes.Presentation;

internal static class NoteMenuAppearance
{
    internal static void Initialize(ContextMenu menu)
    {
        menu.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/DesktopNotes;component/NoteMenuStyles.xaml", UriKind.Relative) });
        menu.Style = (Style)menu.Resources[typeof(ContextMenu)];
        TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
    }

    internal static void Apply(ContextMenu menu, NoteTheme theme)
    {
        var background = theme.IsDark ? theme.CodeFill : Brushes.White;
        var foreground = theme.Foreground;
        var border = theme.IsDark ? theme.CodeOutline.Brush : Brushes.Gainsboro;
        var hover = theme.IsDark ? theme.Hover : Brushes.WhiteSmoke;
        var muted = theme.IsDark ? Brushes.DarkGray : Brushes.SlateGray;
        void Colours(ResourceDictionary resources)
        {
            resources["NoteMenuBackground"] = background; resources["NoteMenuForeground"] = foreground;
            resources["NoteMenuBorder"] = border; resources["NoteMenuHover"] = hover; resources["NoteMenuMuted"] = muted;
        }
        Colours(menu.Resources);
        void StyleItems(ItemCollection items)
        {
            foreach (var item in items)
            {
                if (item is MenuItem entry)
                {
                    entry.Style = (Style)menu.Resources[typeof(MenuItem)];
                    Colours(entry.Resources);
                    StyleItems(entry.Items);
                }
                else if (item is Separator separator)
                {
                    separator.Style = (Style)menu.Resources[typeof(Separator)];
                    Colours(separator.Resources);
                }
            }
        }
        StyleItems(menu.Items);
    }
}
