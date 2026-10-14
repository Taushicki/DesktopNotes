using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using DesktopNotes.Presentation;
using ICSharpCode.AvalonEdit.Document;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class AtomicTableTests
{
    private const string Source = "# Title\n\n| Header | Second |\n| --- | --- |\n| one | two |\n\nafter";

    [Theory]
    [InlineData(14)]
    [InlineData(17)]
    [InlineData(24)]
    public Task ReadOnlyTablesKeepGeometryAndNeverClipCellText(double size) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = Source };
        editor.SetAppearance(new Note { FontSize = size, Color = NoteColor.MaterialDark });
        var host = Host(editor);
        try
        {
            host.Show(); host.UpdateLayout(); Pump();
            var table = Assert.Single(editor.Tables.Views);
            table.Cells.ElementAt(2).Text = "Значение **жирный** `код`\nвторая строка";
            host.UpdateLayout(); Pump();
            var cells = table.Cells.ToArray();
            editor.SetAppearance(new Note { FontSize = size + 3, Color = NoteColor.MaterialDark });
            host.UpdateLayout(); Pump();
            Assert.All(cells, cell => Assert.True(cell.ViewportHeight + .1 >= cell.TextArea.TextView.DocumentHeight,
                $"after font change: viewport={cell.ViewportHeight}; content={cell.TextArea.TextView.DocumentHeight}"));
            var heights = cells.Select(cell => cell.ActualHeight).ToArray();
            foreach (var readOnly in new[] { true, false, true })
            {
                editor.IsReadOnly = readOnly; host.UpdateLayout(); Pump();
                Assert.Equal(heights, cells.Select(cell => cell.ActualHeight).ToArray());
                Assert.All(cells, cell => Assert.True(cell.ViewportHeight + .1 >= cell.TextArea.TextView.DocumentHeight,
                    $"size={size}; readonly={readOnly}; viewport={cell.ViewportHeight}; content={cell.TextArea.TextView.DocumentHeight}"));
            }
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task SelectingWordsInDifferentCellsLeavesOnlyOneActiveSelection() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = Source + "\n\n| Other | Table |\n| --- | --- |\n| a | b |" };
        var outside = new Button { Content = "toolbar" };
        var panel = new DockPanel(); DockPanel.SetDock(outside, Dock.Bottom); panel.Children.Add(outside); panel.Children.Add(editor);
        var host = Host(panel);
        try
        {
            host.Show(); host.UpdateLayout();
            foreach (var cell in editor.Tables.Views.SelectMany(view => view.Cells).ToArray())
            {
                Keyboard.Focus(cell.TextArea); cell.Select(0, cell.Text.Length);
                Assert.Equal(cell.Text, cell.SelectedText);
                Assert.All(editor.Tables.Views.SelectMany(view => view.Cells).Where(other => other != cell), other => Assert.Equal(0, other.SelectionLength));
                Assert.Equal(0, editor.SelectionLength);
            }
            var selected = editor.EditingTarget;
            Keyboard.Focus(outside);
            Assert.NotEqual(0, selected.SelectionLength);
            Assert.Same(selected, editor.EditingTarget);
            Keyboard.Focus(editor.TextArea);
            Assert.All(editor.Tables.Views.SelectMany(view => view.Cells), cell => Assert.Equal(0, cell.SelectionLength));
        }
        finally { host.Close(); }
    });

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public Task TypingAboveTablePreservesItsParagraphBoundary(string newline) => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = Source.Replace("\n", newline) };
        var original = editor.Text;
        var host = Host(editor);
        try
        {
            host.Show(); host.UpdateLayout();
            var table = Assert.Single(editor.Tables.Views).Model;
            var blank = editor.Document.GetLineByOffset(table.Start).PreviousLine!;
            Keyboard.Focus(editor.TextArea); editor.Select(blank.Offset, 0);
            foreach (var text in new[] { "text", "|", "-" })
            {
                editor.TextArea.PerformTextInput(text); host.UpdateLayout(); Pump();
                var restored = Assert.Single(MarkdownTableEditing.ReadTables(editor.Text));
                Assert.Equal(table.Rows[0], restored.Rows[0]); Assert.Equal(table.Rows[1], restored.Rows[1]);
                Assert.Single(editor.Tables.Views);
            }
            Assert.Contains("text|-" + newline + newline + "| Header", editor.Text);
            while (editor.CanUndo) editor.Undo();
            Assert.Equal(original, editor.Text);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task TypingBelowNewTableKeepsItsVisualBlock() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = "# Title\n\n" };
        var host = Host(editor);
        try
        {
            host.Show(); host.UpdateLayout(); editor.Select(editor.Text.Length, 0); editor.InsertTable();
            host.UpdateLayout(); Pump();
            Assert.True(editor.Tables.Views.Any(), "Before input: " + editor.Text);
            Assert.Same(editor.Tables.Views.Single().Cells.First(), editor.EditingTarget);
            Assert.Same(System.Windows.Media.Brushes.Transparent, editor.TextArea.Caret.CaretBrush);
            Keyboard.Focus(editor.TextArea); editor.Select(editor.Text.Length, 0);
            Assert.Same(editor.Theme.Foreground, editor.TextArea.Caret.CaretBrush);
            foreach (var text in new[] { "x", "|", "-", "more" })
            { editor.TextArea.PerformTextInput(text); host.UpdateLayout(); Pump(); }
            Assert.EndsWith("\n\nx|-more", editor.Text);
            Assert.True(editor.Tables.Views.Any(), "After input: " + editor.Text);
            Assert.Contains(editor.TextArea.TextView.VisualLines.SelectMany(line => line.Elements), element => element is ICSharpCode.AvalonEdit.Rendering.InlineObjectElement);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task CellEditingProtectsSchemaSpacesFormattingAndUndo() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = Source };
        var host = Host(editor);
        try
        {
            host.Show(); host.UpdateLayout();
            var table = Assert.Single(editor.Tables.Views);
            var cell = table.Cells.ElementAt(2);
            foreach (var text in new[] { "-", "---", "|", "one | two", " text ", "**bold** and *italic*", "line\nnext" })
            {
                cell.Text = text; host.UpdateLayout();
                Assert.Same(table, Assert.Single(editor.Tables.Views));
                Assert.Same(cell, table.Cells.ElementAt(2));
                var parsed = Assert.Single(MarkdownTableEditing.ReadTables(editor.Text));
                Assert.Equal(2, parsed.ColumnCount); Assert.Equal(2, parsed.Rows.Length);
                Assert.Equal(text, parsed.Rows[1][0]); Assert.Equal(text, cell.Text);
                Assert.StartsWith("# Title\n\n", editor.Text); Assert.EndsWith("\n\nafter", editor.Text);
            }
            cell.Text = "---";
            Assert.Empty(cell.Layout.CodeFrames);
            Assert.DoesNotContain(cell.Layout.Decorations, part => part.Kind is InlineMarkdownLayout.DecorationKind.Rule or InlineMarkdownLayout.DecorationKind.Bullet);
            cell.Text = "one"; cell.Select(3, 0); cell.Document.Insert(3, "|");
            Assert.Equal(4, cell.CaretOffset);
            editor.Undo(); Assert.Equal("one", cell.Text);
            editor.Redo(); Assert.Equal("one|", cell.Text);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task TableHasFullGridAndOnlyItsContentScrollsHorizontally() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = Source, Padding = new Thickness(0) };
        editor.SetAppearance(new Note { Color = NoteColor.GitHubDark });
        var host = Host(editor);
        try
        {
            host.Show(); host.UpdateLayout();
            var table = Assert.Single(editor.Tables.Views);
            var grid = Assert.IsType<Grid>(table.Scroll.Content);
            var borders = grid.Children.Cast<Border>().ToArray();
            Assert.Equal(new Thickness(1), borders[0].BorderThickness);
            Assert.All(borders, border => { Assert.Equal(1, border.BorderThickness.Right); Assert.Equal(1, border.BorderThickness.Bottom); });
            Assert.All(table.Cells.Take(2), cell => Assert.True(cell.Appearance.IsBold));
            table.Cells.ElementAt(2).Text = new string('-', 160);
            host.UpdateLayout();
            Assert.True(table.Scroll.ScrollableWidth > 300);
            Assert.Equal(0, table.Scroll.TranslatePoint(new Point(), table).X, 1);
            Assert.InRange(table.ActualWidth - table.Scroll.ActualWidth, 0, 12.1);
            Assert.InRange(table.Scroll.ActualHeight - grid.ActualHeight, 0, 4.1);
            var afterLine = editor.Document.GetLineByOffset(editor.Text.IndexOf("after", StringComparison.Ordinal));
            Assert.True(editor.TextArea.TextView.GetOrConstructVisualLine(afterLine.PreviousLine!).FirstDocumentLine.Offset < afterLine.PreviousLine!.Offset);
            Assert.True(table.ActualWidth <= editor.TextArea.TextView.ActualWidth);
            table.Scroll.ScrollToHorizontalOffset(100); host.UpdateLayout();
            Assert.True(table.Scroll.HorizontalOffset > 0);
            var bounds = table.BodyBounds()!.Value;
            var point = new Point(bounds.Left + 10, bounds.Top + 10);
            Assert.True(editor.Tables.HandleWheel(point, -120, ModifierKeys.Shift)); host.UpdateLayout();
            Assert.True(table.Scroll.HorizontalOffset > 100);
            var shifted = table.Scroll.HorizontalOffset;
            Assert.True(editor.Tables.HandleWheel(point, 30, ModifierKeys.None, nativeHorizontal: true)); host.UpdateLayout();
            Assert.True(table.Scroll.HorizontalOffset > shifted);
            var screen = editor.TextArea.TextView.PointToScreen(point);
            var packedPoint = ((long)(ushort)(short)screen.Y << 16) | (ushort)(short)screen.X;
            var beforeNative = table.Scroll.HorizontalOffset;
            SendMessage(new WindowInteropHelper(host).Handle, 0x020E, (nint)(120 << 16), (nint)packedPoint);
            host.UpdateLayout();
            Assert.True(table.Scroll.HorizontalOffset > beforeNative);
            Assert.False(editor.Tables.HandleWheel(point, 120, ModifierKeys.None));
            Assert.False(editor.Tables.HandleWheel(new Point(-100, -100), 120, ModifierKeys.Shift));
            Assert.Equal(0, editor.HorizontalOffset);
            Assert.Equal(ScrollBarVisibility.Disabled, table.Scroll.VerticalScrollBarVisibility);
            editor.IsReadOnly = true;
            Assert.All(table.Cells, cell => Assert.True(cell.IsReadOnly));
            editor.IsReadOnly = false;
            Assert.All(table.Cells, cell => Assert.False(cell.IsReadOnly));
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task PartialDeletionAndStructuralFormattingAreBlockedWholeDeletionIsUndoable() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = Source };
        var table = Assert.Single(editor.Tables.Views).Model;
        editor.Select(table.Start, 0);
        Assert.True(editor.HandleShortcut(Key.Delete, ModifierKeys.None));
        Assert.Equal(table.Length, editor.SelectionLength);
        var provider = editor.TextArea.ReadOnlySectionProvider;
        Assert.False(provider.CanInsert(table.Start + 2));
        Assert.Empty(provider.GetDeletableSegments(new TextSegment { StartOffset = table.Start + 2, Length = 3 }));
        Assert.Single(provider.GetDeletableSegments(new TextSegment { StartOffset = table.Start, Length = table.Length }));
        editor.Select(table.Start + 2, 3);
        editor.HandleShortcut(Key.B, ModifierKeys.Control);
        Assert.Equal(Source, editor.Text);
        editor.Select(table.Start + 2, 3);
        Assert.True(editor.HandleShortcut(Key.Back, ModifierKeys.None));
        Assert.Equal(table.Start, editor.SelectionStart); Assert.Equal(table.Length, editor.SelectionLength);
        editor.TextArea.Selection.ReplaceSelectionWithText("");
        Assert.Empty(editor.Tables.Views); Assert.EndsWith("\n\nafter", editor.Text);
        editor.Undo(); Assert.Equal(Source, editor.Text); Assert.Single(editor.Tables.Views);
        editor.DisablePreview(); Assert.Empty(editor.Tables.Views);
        Assert.True(provider.CanInsert(table.Start + 2));
    });

    [Fact]
    public Task EnterAddsRowInSameColumnAndToolbarTargetsLastFocusedTable() => OnSta(() =>
    {
        using var editor = new LiveMarkdownEditor { Text = Source + "\n\n| Other | Table |\n| --- | --- |\n| a | b |" };
        var host = Host(editor);
        try
        {
            host.Show(); host.UpdateLayout();
            var first = editor.Tables.Views.First();
            first.FocusCell(1, 1); Pump();
            Assert.Same(first.Cells.ElementAt(3), editor.EditingTarget);
            Assert.True(editor.HandleShortcut(Key.Enter, ModifierKeys.None)); Pump(); host.UpdateLayout();
            first = editor.Tables.Views.First();
            Assert.Equal(3, first.Model.Rows.Length);
            Assert.Same(first.Cells.ElementAt(5), editor.EditingTarget);
            Assert.Same(System.Windows.Media.Brushes.Transparent, editor.TextArea.Caret.CaretBrush);
            editor.SetAppearance(new Note { Color = NoteColor.GitHubDark });
            Assert.Same(System.Windows.Media.Brushes.Transparent, editor.TextArea.Caret.CaretBrush);
            var second = editor.Tables.Views.Last(); second.FocusCell(1, 0); Pump();
            var cell = second.Cells.ElementAt(2); Assert.Same(cell, editor.EditingTarget);
            cell.Select(0, 1); editor.ToggleBoldSelection();
            Assert.Equal("**a**", cell.Text); Assert.Equal("two", first.Model.Rows[1][1]);
            editor.HandleShortcut(Key.Z, ModifierKeys.Control); Assert.Equal("a", cell.Text);
            Assert.True(editor.HandleEscape()); Assert.Same(editor, editor.EditingTarget);
            editor.Select(first.Model.Start, 0);
            Assert.True(editor.HandleShortcut(Key.Tab, ModifierKeys.None)); Pump();
            Assert.Same(first.Cells.First(), editor.EditingTarget);
        }
        finally { host.Close(); }
    });

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint handle, int message, nint wParam, nint lParam);

    private static Window Host(object content) => new() { Content = content, Width = 350, Height = 500, Left = -10000, Top = -10000, ShowInTaskbar = false };
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception error) { completion.SetException(error); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
