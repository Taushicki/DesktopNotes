using System.Windows;
using System.Windows.Media;

namespace DesktopNotes.Presentation;

internal partial class DeleteConfirmationWindow : Window
{
    internal DeleteConfirmationWindow(NoteTheme theme)
    {
        InitializeComponent();
        NoteMenuAppearance.ApplyResources(Resources, theme);
        Resources["ToolbarHoverBrush"] = theme.IsDark ? theme.Hover : Brushes.WhiteSmoke;
        Resources["ToolbarPressedBrush"] = theme.IsDark ? theme.Pressed : Brushes.Gainsboro;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => DialogResult = false;
    private void DeleteClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
