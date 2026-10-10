using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DesktopNotes.Infrastructure;
using DesktopNotes.Domain;

namespace DesktopNotes.Presentation;

internal partial class SettingsWindow : Window
{
    internal SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        MaxHeight = Math.Max(200, SystemParameters.WorkArea.Height - 40);
        ApplyTheme(); Activated += (_, _) => ApplyTheme();
        Closed += (_, _) => viewModel.Dispose();
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }
    private void ApplyTheme()
    {
        var theme = NoteThemes.Get(WindowsAppTheme.IsDark ? NoteColor.GitHubDark : NoteColor.Yellow);
        NoteMenuAppearance.ApplyResources(Resources, theme);
        Resources["DesktopNotes.EditorScrollThumb"] = theme.IsDark ? theme.ScrollThumb : Brushes.SlateGray;
        Resources["ToolbarHoverBrush"] = theme.IsDark ? theme.Hover : Brushes.WhiteSmoke;
        Resources["ToolbarPressedBrush"] = theme.IsDark ? theme.Pressed : Brushes.Gainsboro;
    }
    private void DragHeader(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TextBlock or System.Windows.Controls.Grid && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
}
