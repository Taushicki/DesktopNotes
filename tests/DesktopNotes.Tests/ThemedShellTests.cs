using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopNotes.Domain;
using DesktopNotes.Presentation;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class ThemedShellTests
{
    [Fact]
    public Task TrayActionsAndRetryVisibilitySurviveThemeChanges() => OnSta(() =>
    {
        var calls = new int[5];
        var tray = new TrayMenu(() => calls[0]++, () => calls[1]++, () => calls[2]++,
            () => { calls[3]++; return Task.CompletedTask; }, () => { calls[4]++; return Task.CompletedTask; });
        tray.Menu.Placement = PlacementMode.AbsolutePoint;
        tray.Menu.HorizontalOffset = -10000; tray.Menu.VerticalOffset = -10000;
        try
        {
            tray.Open(true, true);
            Assert.Same(NoteThemes.Get(NoteColor.GitHubDark).CodeFill, tray.Menu.Background);
            Assert.Equal(Visibility.Visible, ((MenuItem)tray.Menu.Items[3]).Visibility);
            tray.Close(); tray.Open(false, false);
            Assert.Same(Brushes.White, tray.Menu.Background);
            Assert.Equal(Visibility.Collapsed, ((MenuItem)tray.Menu.Items[3]).Visibility);
            foreach (var item in tray.Menu.Items.OfType<MenuItem>())
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(new[] { 1, 1, 1, 1, 1 }, calls);
            Assert.False(tray.Menu.IsOpen);
        }
        finally { tray.Close(); }
    });

    [Fact]
    public Task ConfirmationUsesNoteThemeAndSafeDefault() => OnSta(() =>
    {
        foreach (var color in new[] { NoteColor.Yellow, NoteColor.GitHubDark, NoteColor.MaterialDark })
        {
            var theme = NoteThemes.Get(color);
            var dialog = new DeleteConfirmationWindow(theme);
            try
            {
                var cancel = Assert.IsType<Button>(dialog.FindName("CancelButton"));
                var delete = Assert.IsType<Button>(dialog.FindName("DeleteButton"));
                Assert.True(cancel.IsCancel); Assert.True(cancel.IsDefault); Assert.False(delete.IsDefault);
                var surface = Assert.IsType<Border>(dialog.FindName("Surface"));
                surface.Measure(new Size(430, double.PositiveInfinity));
                surface.Arrange(new Rect(new Point(), surface.DesiredSize)); surface.UpdateLayout();
                Assert.Equal(new CornerRadius(8), surface.CornerRadius);
                Assert.Same(theme.IsDark ? theme.CodeFill : Brushes.White, surface.Background);
                Assert.Same(theme.Foreground, cancel.Foreground);
                if (Environment.GetEnvironmentVariable("DESKTOPNOTES_TEST_RENDER_DIR") is { } directory)
                {
                    Directory.CreateDirectory(directory);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth), (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, $"delete-dialog-{color}.png")); encoder.Save(file);
                }
            }
            finally { dialog.Close(); }
        }
    });

    [Fact]
    public Task ConfirmationReturnsTrueOnlyForExplicitDeletion() => OnSta(() =>
    {
        foreach (var confirm in new[] { false, true })
        {
            var dialog = new DeleteConfirmationWindow(NoteThemes.Get(NoteColor.GitHubDark))
                { WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
            bool cancelFocused = false;
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
            {
                cancelFocused = Keyboard.FocusedElement == dialog.FindName("CancelButton");
                ((Button)dialog.FindName(confirm ? "DeleteButton" : "CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, DispatcherPriority.Background);
            Assert.Equal(confirm, dialog.ShowDialog());
            Assert.True(cancelFocused);
        }
    });

    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
