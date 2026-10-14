using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopNotes.Application;
using DesktopNotes.Domain;
using DesktopNotes.Presentation;
using ICSharpCode.AvalonEdit.Document;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class AtomicTableTests
{
    private const string Source = "# Title\n\n| Header | Second |\n| --- | --- |\n| one | two |\n\nafter";

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
            Assert.True(table.ActualWidth <= editor.TextArea.TextView.ActualWidth);
            table.Scroll.ScrollToHorizontalOffset(100); host.UpdateLayout();
            Assert.True(table.Scroll.HorizontalOffset > 0);
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
