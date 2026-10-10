using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using DesktopNotes.Presentation;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class NoteMenuTests
{
    private sealed class Repository : INoteRepository
    {
        public Task<LoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LoadResult([]));
        public Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Layers : IWindowLayerController { public void Apply(nint handle, WindowLayer layer) { } }

    [Fact]
    public Task MenuAndNestedPopupsFollowNoteThemeAndFormattingCommandsStillWork() => OnSta(() =>
    {
        var notes = new NoteService(new Repository());
        var note = notes.Create(-10000, -10000);
        using var vm = new NoteViewModel(note, notes, () => { }, () => { });
        var host = new NoteWindow(vm, new Layers());
        try
        {
            var menu = host.ContextMenu;
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            var formatting = Assert.IsType<MenuItem>(menu.Items[0]);
            var bold = Assert.IsType<MenuItem>(formatting.Items[0]);
            Assert.Equal("Жирный", bold.Header); Assert.Equal("Ctrl+B", bold.InputGestureText);
            Assert.NotNull(formatting.Style); Assert.NotNull(bold.Style);
            foreach (var color in new[] { NoteColor.Yellow, NoteColor.GitHubDark, NoteColor.MaterialDark, NoteColor.MidnightDark, NoteColor.Mint })
            {
                vm.Color = color;
                menu.ApplyTemplate(); menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                menu.Arrange(new Rect(new Point(), menu.DesiredSize)); menu.UpdateLayout();
                var surface = Assert.IsType<Border>(menu.Template.FindName("Surface", menu));
                var theme = NoteThemes.Get(color);
                Assert.Equal(new CornerRadius(8), surface.CornerRadius);
                Assert.Same(theme.IsDark ? theme.CodeFill : Brushes.White, surface.Background);
                Assert.Same(theme.Foreground, bold.Foreground);
                Assert.Same(theme.IsDark ? theme.Hover : Brushes.WhiteSmoke, bold.Resources["NoteMenuHover"]);
                formatting.ApplyTemplate();
                var popup = Assert.IsType<Popup>(formatting.Template.FindName("PART_Popup", formatting));
                var submenu = Assert.IsType<Border>(popup.Child);
                Assert.Same(surface.Background, submenu.Background);
                var headings = Assert.Single(formatting.Items.OfType<MenuItem>(), entry => (string?)entry.Header == "Заголовок");
                headings.ApplyTemplate();
                var nested = Assert.IsType<Popup>(headings.Template.FindName("PART_Popup", headings));
                Assert.Same(surface.Background, Assert.IsType<Border>(nested.Child).Background);
                if (Environment.GetEnvironmentVariable("DESKTOPNOTES_TEST_RENDER_DIR") is { } directory)
                {
                    Directory.CreateDirectory(directory);
                    void Render(FrameworkElement element, string name)
                    {
                        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        element.Arrange(new Rect(new Point(), element.DesiredSize)); element.UpdateLayout();
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
                    }
                    Render(surface, $"note-menu-{color}.png");
                }
            }
            host.MarkdownEditor.Text = "# Title\n\nword";
            host.MarkdownEditor.Select(host.MarkdownEditor.Text.IndexOf("word", StringComparison.Ordinal), 4);
            bold.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal("# Title\n\n**word**", vm.Markdown);
            host.MarkdownEditor.Undo(); Assert.Equal("# Title\n\nword", vm.Markdown);
        }
        finally { host.ClosePermanently(); }
    });

    [Fact]
    public Task SubmenuOpensAndNestedItemsUseTheSameTheme() => OnSta(() =>
    {
        var menu = new ContextMenu { Placement = PlacementMode.AbsolutePoint, HorizontalOffset = -10000, VerticalOffset = -10000 };
        NoteMenuAppearance.Initialize(menu);
        var parent = new MenuItem { Header = "Оформление текста" };
        var child = new MenuItem { Header = "Жирный", InputGestureText = "Ctrl+B" };
        parent.Items.Add(child); menu.Items.Add(parent);
        NoteMenuAppearance.Apply(menu, NoteThemes.Get(NoteColor.GitHubDark));
        try
        {
            menu.IsOpen = true; menu.UpdateLayout();
            parent.IsSubmenuOpen = true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            var popup = Assert.IsType<Popup>(parent.Template.FindName("PART_Popup", parent));
            Assert.True(popup.IsOpen); Assert.True(popup.Child.IsVisible);
            Assert.True(child.ActualHeight >= 30);
            Assert.Same(NoteThemes.Get(NoteColor.GitHubDark).Foreground, child.Foreground);
            NoteMenuAppearance.Apply(menu, NoteThemes.Get(NoteColor.Yellow));
            Assert.Same(Brushes.White, Assert.IsType<Border>(popup.Child).Background);
        }
        finally { parent.IsSubmenuOpen = false; menu.IsOpen = false; }
    });

    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
