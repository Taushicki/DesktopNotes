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
    private const double Size = 12, Reach = 8;
    private readonly LiveMarkdownEditor _editor;
    private readonly TextView _view;
    private readonly DependencyPropertyDescriptor _readOnly;
    private readonly System.Windows.Shapes.Path _icon;
    internal Button AddButton { get; }
    private int? _tableStart;
    private TableExpansion _expansion;

    internal TableEdgeControls(LiveMarkdownEditor editor, bool enabled = true)
    {
        _editor = editor; _view = editor.TextArea.TextView;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DesktopNotes;component/ToolbarStyles.xaml", UriKind.Relative) });
        _icon = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 0,4 L 8,4 M 4,0 L 4,8"),
            Width = 8, Height = 8, Stretch = Stretch.Uniform, StrokeThickness = 1.5 };
        AddButton = new Button { Width = Size, Height = Size, Padding = new Thickness(0), Focusable = false,
            Style = (Style)Resources["TableExpandButton"], Content = _icon, Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand, UseLayoutRounding = true };
        Children.Add(AddButton);
        AddButton.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; Expand(); };
        AddButton.Click += (_, _) => Expand();
        if (!enabled) { _readOnly = null!; return; }
        _view.InsertLayer(this, KnownLayer.Caret, LayerInsertionPosition.Above);
        _view.MouseMove += OnMouseMove;
        _view.MouseLeave += OnMouseLeave;
        _view.VisualLinesChanged += OnGeometryChanged;
        _editor.TextChanged += OnTextChanged;
        _readOnly = DependencyPropertyDescriptor.FromProperty(TextEditor.IsReadOnlyProperty, typeof(LiveMarkdownEditor));
        _readOnly.AddValueChanged(editor, OnReadOnlyChanged);
    }

    internal Rect? Bounds(LiveMarkdownEditor.PreviewBlock table)
    {
        return _editor.Tables.Get(table.Start)?.BodyBounds();
    }

    internal void UpdatePointer(Point? pointer)
    {
        if (_editor.IsReadOnly || pointer is null || !_view.VisualLinesValid) { Hide(); return; }
        var point = pointer.Value;
        if (AddButton.Visibility == Visibility.Visible && new Rect(GetLeft(AddButton), GetTop(AddButton), AddButton.Width, AddButton.Height).Contains(point)) return;
        foreach (var table in _editor.PreviewBlocks.Where(block => block.IsTable))
        {
            if (Bounds(table) is not { } bounds || point.Y < bounds.Top || point.Y > bounds.Bottom + Reach) continue;
            var bottom = Math.Abs(point.Y - bounds.Bottom);
            var right = Math.Abs(point.X - bounds.Right);
            var edge = Math.Min(bottom, right);
            if (edge > Reach || point.X < bounds.Left - Reach || point.X > bounds.Right + Reach) continue;
            // A clipped edge is not the edge of the table.
            if (edge == bottom && (bounds.Bottom < 0 || bounds.Bottom > _view.ActualHeight) ||
                edge == right && bounds.Right > _view.ActualWidth) continue;
            _tableStart = table.Start;
            _expansion = edge == bottom ? TableExpansion.Row : TableExpansion.ColumnRight;
            AddButton.Width = _expansion == TableExpansion.Row ? bounds.Width : Size;
            AddButton.Height = _expansion == TableExpansion.Row ? Size : bounds.Height;
            SetLeft(AddButton, _expansion == TableExpansion.Row ? bounds.Left : bounds.Right);
            SetTop(AddButton, _expansion == TableExpansion.Row ? bounds.Bottom : bounds.Top);
            AddButton.Background = _editor.Theme.Hover;
            _icon.Stroke = _editor.Theme.Foreground; AddButton.Foreground = _editor.Theme.CodeOutline.Brush;
            Resources["ToolbarHoverBrush"] = _editor.Theme.Pressed;
            AutomationProperties.SetName(AddButton, _expansion == TableExpansion.Row ? UiText.Get("Добавить строку") : UiText.Get("Добавить столбец"));
            AddButton.Visibility = Visibility.Visible;
            return;
        }
        Hide();
    }

    private void Expand()
    {
        if (_editor.IsReadOnly || _tableStart is not { } start) return;
        _editor.Tables.Expand(start, _expansion);
        Hide();
    }

    private void Hide() { _tableStart = null; AddButton.Visibility = Visibility.Collapsed; }
    private void OnMouseMove(object sender, MouseEventArgs args) => UpdatePointer(args.GetPosition(_view));
    private void OnMouseLeave(object sender, MouseEventArgs args) => Hide();
    private void OnGeometryChanged(object? sender, EventArgs args) { Hide(); UpdatePointer(_view.IsMouseOver ? Mouse.GetPosition(_view) : null); }
    private void OnTextChanged(object? sender, EventArgs args) => Hide();
    private void OnReadOnlyChanged(object? sender, EventArgs args) { _editor.TextArea.ReadOnlySectionProvider = _editor.Tables; _editor.Tables.UpdateEditingState(); if (_editor.IsReadOnly) Hide(); }

    public void Dispose()
    {
        _view.MouseMove -= OnMouseMove; _view.MouseLeave -= OnMouseLeave; _view.VisualLinesChanged -= OnGeometryChanged;
        _editor.TextChanged -= OnTextChanged;
        _readOnly?.RemoveValueChanged(_editor, OnReadOnlyChanged);
        _view.Layers.Remove(this);
    }
}
