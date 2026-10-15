using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using DesktopNotes.Presentation;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class TableEdgeControlsTests
{
    private const string Source = "# Title\n\n| A | B |\n| --- | --- |\n| one | two |\n\nafter";

    [Theory]
    [InlineData(TableExpansion.Row)]
    [InlineData(TableExpansion.ColumnRight)]
    public Task EdgeButtonExpandsMarkdownAndUndoRestoresTheWholeTable(TableExpansion expansion) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = Source, Margin = new Thickness(20) };
        var host = new Window { Content = editor, Width = 500, Height = 300, Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            host.Show(); host.UpdateLayout();
            editor.TextArea.TextView.EnsureVisualLines();
            var table = Assert.Single(editor.PreviewBlocks, block => block.IsTable);
            var bounds = editor.TableControls.Bounds(table)!.Value;
            var pointer = expansion switch
            {
                TableExpansion.Row => new Point(bounds.Left + bounds.Width / 2, bounds.Bottom),
                TableExpansion.ColumnLeft => new Point(bounds.Left, bounds.Top + bounds.Height / 2),
                _ => new Point(bounds.Right, bounds.Top + bounds.Height / 2)
            };
            editor.TableControls.UpdatePointer(pointer);
            Assert.Equal(Visibility.Visible, editor.TableControls.AddButton.Visibility);
            Assert.Null(editor.TableControls.AddButton.ToolTip);
            editor.TableControls.AddButton.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
            Assert.NotEqual(Source, editor.Text);
            Assert.StartsWith("# Title\n\n", editor.Text); Assert.EndsWith("\n\nafter", editor.Text);
            Assert.Equal(' ', editor.Text[editor.CaretOffset]);
            editor.Document.Insert(editor.CaretOffset, "new"); host.UpdateLayout();
            Assert.Contains("new", editor.Text);
            editor.Undo(); editor.Undo();
            Assert.Equal(Source, editor.Text);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task TopAndInteriorHaveNoButtonAndReadOnlyModeHidesItImmediately() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = Source, Margin = new Thickness(20) };
        var host = new Window { Content = editor, Width = 500, Height = 300, Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            host.Show(); host.UpdateLayout(); editor.TextArea.TextView.EnsureVisualLines();
            var table = Assert.Single(editor.PreviewBlocks, block => block.IsTable);
            var bounds = editor.TableControls.Bounds(table)!.Value;
            editor.TableControls.UpdatePointer(new(bounds.Left + bounds.Width / 2, bounds.Top));
            Assert.Equal(Visibility.Collapsed, editor.TableControls.AddButton.Visibility);
            editor.TableControls.UpdatePointer(new(bounds.Left, bounds.Top + bounds.Height / 2));
            Assert.Equal(Visibility.Collapsed, editor.TableControls.AddButton.Visibility);
            editor.TableControls.UpdatePointer(new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
            Assert.Equal(Visibility.Collapsed, editor.TableControls.AddButton.Visibility);
            foreach (var color in new[] { NoteColor.Yellow, NoteColor.GitHubDark })
            {
                editor.SetAppearance(new Note { Color = color }); host.Background = editor.Theme.Background; host.UpdateLayout();
                editor.TableControls.UpdatePointer(new(bounds.Left + bounds.Width / 2, bounds.Bottom));
                Assert.Equal(Visibility.Visible, editor.TableControls.AddButton.Visibility);
                if (Environment.GetEnvironmentVariable("DESKTOPNOTES_TEST_RENDER_DIR") is { } directory)
                {
                    host.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(500, 300, 96, 96, PixelFormats.Pbgra32); bitmap.Render(host);
                    Directory.CreateDirectory(directory); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, $"table-add-{color}.png")); encoder.Save(file);
                }
            }
            editor.IsReadOnly = true;
            Assert.Equal(Visibility.Collapsed, editor.TableControls.AddButton.Visibility);
            editor.TableControls.UpdatePointer(new(bounds.Left, bounds.Top + bounds.Height / 2));
            Assert.Equal(Visibility.Collapsed, editor.TableControls.AddButton.Visibility);
            Assert.Equal(Source, editor.Text);
        }
        finally { host.Close(); }
    });

    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
