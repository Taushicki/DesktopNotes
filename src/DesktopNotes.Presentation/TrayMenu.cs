using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using DesktopNotes.Domain;

namespace DesktopNotes.Presentation;

/// <summary>The tray and notes share the same native WPF menu templates.</summary>
internal sealed class TrayMenu
{
    internal ContextMenu Menu { get; } = new() { Placement = PlacementMode.MousePoint };
    private readonly MenuItem _retry;

    internal TrayMenu(Action create, Action show, Action settings, Func<Task> retry, Func<Task> exit)
    {
        NoteMenuAppearance.Initialize(Menu);
        Add("Создать заметку", create);
        Add("Показать все", show);
        Add("Настройки", settings);
        _retry = Add("Повторить сохранение", async () => await retry());
        Menu.Items.Add(new Separator());
        Add("Выход", async () => await exit());
        Menu.Opened += (_, _) =>
        {
            // NotifyIcon has no WPF owner. Activate the popup so outside clicks and
            // keyboard navigation behave like an ordinary foreground context menu.
            if (PresentationSource.FromVisual(Menu) is HwndSource source)
                NativeWindows.Activate(source.Handle);
            Menu.Focus();
        };
    }

    private MenuItem Add(string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => { Close(); action(); };
        Menu.Items.Add(item);
        return item;
    }

    internal void Open(bool dark, bool saveError)
    {
        Close();
        _retry.Visibility = saveError ? Visibility.Visible : Visibility.Collapsed;
        NoteMenuAppearance.Apply(Menu, NoteThemes.Get(dark ? NoteColor.GitHubDark : NoteColor.Yellow));
        Menu.IsOpen = true;
    }

    internal void Close() => Menu.IsOpen = false;
}
