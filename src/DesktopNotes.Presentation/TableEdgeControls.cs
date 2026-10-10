using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopNotes.Application;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;

namespace DesktopNotes.Presentation;

// An input layer over the existing Markdown table; it never owns document text.
internal sealed class TableEdgeControls : Canvas, IDisposable
{
    private const double Size = 18, Reach = 8;
    private readonly LiveMarkdownEditor _editor;
    private readonly TextView _view;
    private readonly DependencyPropertyDescriptor _readOnly;
    private readonly System.Windows.Shapes.Path _icon;
    internal Button AddButton { get; }
    private int? _tableStart;
    private TableExpansion _expansion;

    internal TableEdgeControls(LiveMarkdownEditor editor)
    {
        _editor = editor; _view = editor.TextArea.TextView;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DesktopNotes;component/ToolbarStyles.xaml", UriKind.Relative) });
        _icon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,4 L 8,4 M 4,0 L 4,8"),
            Width = 8, Height = 8, Stretch = Stretch.Uniform, StrokeThickness = 1.5 };
        AddButton = new Button { Width = Size, Height = Size, Padding = new Thickness(0), Focusable = false,
            Style = (Style)Resources["ToolbarButton"], Content = _icon, Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand, UseLayoutRounding = true };
        Children.Add(AddButton);
        AddButton.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; Expand(); };
        AddButton.Click += (_, _) => Expand();
        _view.InsertLayer(this, KnownLayer.Selection, LayerInsertionPosition.Above);
        _view.MouseMove += OnMouseMove;
        _view.MouseLeave += OnMouseLeave;
        _view.VisualLinesChanged += OnGeometryChanged;
        _editor.TextChanged += OnTextChanged;
        _readOnly = DependencyPropertyDescriptor.FromProperty(TextEditor.IsReadOnlyProperty, typeof(LiveMarkdownEditor));
        _readOnly.AddValueChanged(editor, OnReadOnlyChanged);
    }

    internal Rect? Bounds(LiveMarkdownEditor.PreviewBlock table)
    {
        if (!_view.VisualLinesValid) return null;
        var lines = _view.VisualLines.Where(line => line.FirstDocumentLine.Offset < table.Start + table.Length &&
            line.LastDocumentLine.EndOffset >= table.Start).ToArray();
        if (lines.Length == 0) return null;
        var separator = _editor.Layout.Decorations.FirstOrDefault(part => part.Start >= table.Start &&
            part.Start < table.Start + table.Length && part.Kind == InlineMarkdownLayout.DecorationKind.TableRule);
        if (separator?.Columns is not { } columns) return null;
        var width = columns.Sum(column => InlineMarkdownMetrics.MeasureColumn(_editor, column) + 12 * _editor.FontSize / 14);
        var first = _editor.Document.GetLineByOffset(table.Start);
        var last = _editor.Document.GetLineByOffset(table.Start + table.Length - 1);
        var top = _view.GetVisualTopByDocumentLine(first.LineNumber) - _view.ScrollOffset.Y;
        var bottom = _view.GetVisualTopByDocumentLine(last.LineNumber) +
            (lines.LastOrDefault(line => line.LastDocumentLine.LineNumber == last.LineNumber)?.Height ?? _view.DefaultLineHeight) - _view.ScrollOffset.Y;
        return new Rect(1 - _view.ScrollOffset.X, top, Math.Max(1, width - 1), Math.Max(1, bottom - top));
    }

    internal void UpdatePointer(Point? pointer)
    {
        if (_editor.IsReadOnly || pointer is null || !_view.VisualLinesValid) { Hide(); return; }
        var point = pointer.Value;
        if (AddButton.Visibility == Visibility.Visible && new Rect(GetLeft(AddButton), GetTop(AddButton), Size, Size).Contains(point)) return;
        foreach (var table in _editor.PreviewBlocks.Where(block => block.IsTable))
        {
            if (Bounds(table) is not { } bounds || point.Y < bounds.Top || point.Y > bounds.Bottom + Reach) continue;
            var bottom = Math.Abs(point.Y - bounds.Bottom);
            var left = Math.Abs(point.X - bounds.Left);
            var right = Math.Abs(point.X - bounds.Right);
            var edge = Math.Min(bottom, Math.Min(left, right));
            if (edge > Reach || point.X < bounds.Left - Reach || point.X > bounds.Right + Reach) continue;
            // A clipped edge is not the edge of the table.
            if (edge == bottom && (bounds.Bottom < 0 || bounds.Bottom > _view.ActualHeight) ||
                edge == right && bounds.Right > _view.ActualWidth || edge == left && bounds.Left < 0) continue;
            _tableStart = table.Start;
            _expansion = edge == bottom ? TableExpansion.Row : edge == left ? TableExpansion.ColumnLeft : TableExpansion.ColumnRight;
            var x = _expansion == TableExpansion.Row ? point.X : _expansion == TableExpansion.ColumnLeft ? bounds.Left - Size / 2 - 2 : bounds.Right + Size / 2 + 2;
            var y = _expansion == TableExpansion.Row ? bounds.Bottom + Size / 2 + 2 : point.Y;
            SetLeft(AddButton, Math.Clamp(x - Size / 2, 0, Math.Max(0, _view.ActualWidth - Size)));
            SetTop(AddButton, Math.Clamp(y - Size / 2, 0, Math.Max(0, _view.ActualHeight - Size)));
            AddButton.Background = _editor.Theme.Hover;
            _icon.Stroke = _editor.Theme.Foreground;
            Resources["ToolbarHoverBrush"] = _editor.Theme.Pressed;
            AutomationProperties.SetName(AddButton, _expansion == TableExpansion.Row ? "Добавить строку" : "Добавить столбец");
            AddButton.Visibility = Visibility.Visible;
            return;
        }
        Hide();
    }

    private void Expand()
    {
        if (_editor.IsReadOnly || _tableStart is not { } start) return;
        var edit = MarkdownTableEditing.Expand(_editor.Text, start, _expansion);
        Hide();
        if (edit is null) return;
        _editor.ApplyEdit(edit);
        _editor.TextArea.Focus();
    }

    private void Hide() { _tableStart = null; AddButton.Visibility = Visibility.Collapsed; }
    private void OnMouseMove(object sender, MouseEventArgs args) => UpdatePointer(args.GetPosition(_view));
    private void OnMouseLeave(object sender, MouseEventArgs args) => Hide();
    private void OnGeometryChanged(object? sender, EventArgs args) { Hide(); UpdatePointer(_view.IsMouseOver ? Mouse.GetPosition(_view) : null); }
    private void OnTextChanged(object? sender, EventArgs args) => Hide();
    private void OnReadOnlyChanged(object? sender, EventArgs args) { if (_editor.IsReadOnly) Hide(); }

    public void Dispose()
    {
        _view.MouseMove -= OnMouseMove; _view.MouseLeave -= OnMouseLeave; _view.VisualLinesChanged -= OnGeometryChanged;
        _editor.TextChanged -= OnTextChanged;
        _readOnly.RemoveValueChanged(_editor, OnReadOnlyChanged);
        _view.Layers.Remove(this);
    }
}
