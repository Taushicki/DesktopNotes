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
        ApplyResources(menu.Resources, theme);
        void StyleItems(ItemCollection items)
        {
            foreach (var item in items)
            {
                if (item is MenuItem entry)
                {
                    entry.Style = (Style)menu.Resources[typeof(MenuItem)];
                    ApplyResources(entry.Resources, theme);
                    StyleItems(entry.Items);
                }
                else if (item is Separator separator)
                {
                    separator.Style = (Style)menu.Resources[typeof(Separator)];
                    ApplyResources(separator.Resources, theme);
                }
            }
        }
        StyleItems(menu.Items);
    }
    internal static void ApplyResources(ResourceDictionary resources, NoteTheme theme)
    {
        resources["NoteMenuBackground"] = theme.IsDark ? theme.CodeFill : Brushes.White;
        resources["NoteMenuForeground"] = theme.Foreground;
        resources["NoteMenuBorder"] = theme.IsDark ? theme.CodeOutline.Brush : Brushes.Gainsboro;
        resources["NoteMenuHover"] = theme.IsDark ? theme.Hover : Brushes.WhiteSmoke;
        resources["NoteMenuMuted"] = theme.IsDark ? Brushes.DarkGray : Brushes.SlateGray;
    }

}
