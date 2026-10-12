using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopNotes.Domain;
using DesktopNotes.Presentation;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class InlineSelectionTests
{
    [Fact]
    public Task UnderlineShortcutAndMenuComposeWithOtherStylesAndUndo() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = "~~***word***~~" };
        var host = Host(editor);
        try
        {
            host.Show(); editor.Select(5, 4);
            Assert.True(editor.HandleShortcut(Key.U, ModifierKeys.Control)); host.UpdateLayout();
            var offset = editor.Text.IndexOf("word", StringComparison.Ordinal);
            var line = editor.TextArea.TextView.VisualLines.Single();
            var properties = line.Elements.First(item => item.DocumentLength > 0 && item.RelativeTextOffset <= offset && item.RelativeTextOffset + item.DocumentLength > offset).TextRunProperties;
            Assert.Equal(FontWeights.Bold, properties.Typeface.Weight); Assert.Equal(FontStyles.Italic, properties.Typeface.Style);
            Assert.Contains(properties.TextDecorations, decoration => decoration.Location == TextDecorationLocation.Underline);
            Assert.Contains(properties.TextDecorations, decoration => decoration.Location == TextDecorationLocation.Strikethrough);
            var original = editor.Text;
            var menu = new ContextMenu(); editor.AppendFormattingMenu(menu);
            var formatting = (MenuItem)menu.Items[0];
            var underline = formatting.Items.OfType<MenuItem>().Single(item => item.InputGestureText == "Ctrl+U");
            underline.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.DoesNotContain("++", editor.Text);
            editor.Undo(); Assert.Equal(original, editor.Text);
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData(NoteColor.Yellow)]
    [InlineData(NoteColor.GitHubDark)]
    public Task BlueSelectionRendersAboveMarkerAndClearingSelectionRestoresMarker(NoteColor color) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = "==Highlighted words with enough width==", FontSize = 20 };
        editor.SetTheme(NoteThemes.Get(color));
        var host = Host(editor); host.Background = NoteThemes.Get(color).Background;
        try
        {
            host.Show(); Keyboard.Focus(editor.TextArea); host.UpdateLayout();
            var view = editor.TextArea.TextView;
            byte[] Capture(string name)
            {
                host.UpdateLayout(); view.InvalidateVisual();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                if (Environment.GetEnvironmentVariable("DESKTOPNOTES_TEST_RENDER_DIR") is { } directory)
                {
                    Directory.CreateDirectory(directory);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, $"selection-{color}-{name}.png")); encoder.Save(file);
                }
                return pixels;
            }
            static int BluePixels(byte[] pixels)
            {
                var count = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                    if (pixels[i] > pixels[i + 1] + 3 && pixels[i] > pixels[i + 2] + 15 && pixels[i + 3] > 80) count++;
                return count;
            }
            editor.Select(2, 0);
            var normal = Capture("normal");
            editor.Select(2, 11); // The same selection model used by mouse dragging.
            var selected = Capture("selected");
            Assert.True(BluePixels(selected) > BluePixels(normal) + 50);
            editor.Select(2, 0); Capture("cleared");
            for (var i = 0; i < 11; i++) EditingCommands.SelectRightByCharacter.Execute(null, editor.TextArea);
            Assert.True(editor.SelectionLength > 0);
            var shift = Capture("shift");
            Assert.True(BluePixels(shift) > BluePixels(normal) + 50);
            editor.Select(2, 0); var cleared = Capture("restored");
            Assert.Equal(BluePixels(normal), BluePixels(cleared));
            Assert.Equal("==Highlighted words with enough width==", editor.Text);
        }
        finally { host.Close(); }
    });

    private static Window Host(object content) => new() { Content = content, Left = -10000, Top = -10000, Width = 340, Height = 220, ShowInTaskbar = false };
    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
