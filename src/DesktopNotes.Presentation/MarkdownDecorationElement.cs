using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Rendering;

namespace DesktopNotes.Presentation;

// One visual caret step maps to the original Markdown marker. Text remains native,
// selectable and editable; unlike a block preview, this never replaces a paragraph.
internal sealed class MarkdownDecorationElement(InlineMarkdownLayout.Decoration part, Action activate, Func<InlineMarkdownLayout.Cell, double>? measure = null, Func<IReadOnlyList<InlineMarkdownLayout.Cell>, double>? measureColumn = null, double bottomSpacing = 0)
    : VisualLineElement(1, part.Length)
{
    internal InlineMarkdownLayout.Decoration Decoration => part;
    public override bool IsWhitespace(int visualColumn) => part.Kind is not (InlineMarkdownLayout.DecorationKind.Rule or InlineMarkdownLayout.DecorationKind.TableEdge or InlineMarkdownLayout.DecorationKind.TableGap or InlineMarkdownLayout.DecorationKind.TableRule);
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) => new DecorationRun(this, part, measure, measureColumn, bottomSpacing);
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (part.Kind != InlineMarkdownLayout.DecorationKind.Task || e.ChangedButton != MouseButton.Left) return;
        activate(); e.Handled = true;
    }
    protected override void OnQueryCursor(QueryCursorEventArgs e)
    {
        if (part.Kind != InlineMarkdownLayout.DecorationKind.Task) return;
        e.Cursor = Cursors.Hand; e.Handled = true;
    }

    private sealed class DecorationRun : TextEmbeddedObject
    {
        private readonly MarkdownDecorationElement _element;
        private readonly InlineMarkdownLayout.Decoration _part;
        private readonly FormattedText _sample;
        private readonly FormattedText _label;
        private readonly double _width;
        private readonly double _bottomSpacing;
        internal DecorationRun(MarkdownDecorationElement element, InlineMarkdownLayout.Decoration part, Func<InlineMarkdownLayout.Cell, double>? measure, Func<IReadOnlyList<InlineMarkdownLayout.Cell>, double>? measureColumn, double bottomSpacing)
        {
            _element = element; _part = part; _bottomSpacing = bottomSpacing;
            var properties = element.TextRunProperties;
            var size = properties.FontRenderingEmSize;
            _sample = new FormattedText("Ag", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                properties.Typeface, size, properties.ForegroundBrush, 1);
            _label = new FormattedText(part.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                properties.Typeface, size, properties.ForegroundBrush, 1);
            double Width(IReadOnlyList<InlineMarkdownLayout.Cell> column) => measureColumn?.Invoke(column) ?? Math.Max(24 * size / 14, column.Select(cell => measure?.Invoke(cell) ?? 0).DefaultIfEmpty().Max());
            _width = part.Kind switch
            {
                InlineMarkdownLayout.DecorationKind.Hidden => 0,
                InlineMarkdownLayout.DecorationKind.Indent => part.Indent * size / 14,
                InlineMarkdownLayout.DecorationKind.Spacing => part.Indent * size / 14,
                InlineMarkdownLayout.DecorationKind.Number => part.Indent * size / 14 + Math.Max(16 * size / 14, _label.Width + 6 * size / 14),
                InlineMarkdownLayout.DecorationKind.Rule => 120 * size / 14,
                InlineMarkdownLayout.DecorationKind.Quote => 10 * size / 14,
                InlineMarkdownLayout.DecorationKind.TableEdge => 6 * size / 14,
                InlineMarkdownLayout.DecorationKind.TableGap => Math.Max(0, Width(part.Column!) - (measure?.Invoke(part.Cell!) ?? 0)) + 12 * size / 14,
                InlineMarkdownLayout.DecorationKind.TableRule => part.Columns!.Sum(column => Width(column) + 12 * size / 14) + 6 * size / 14,
                _ => (part.Indent + 16) * size / 14
            };
        }
        public override CharacterBufferReference CharacterBufferReference => new();
        public override int Length => 1;
        public override TextRunProperties Properties => _element.TextRunProperties;
        public override LineBreakCondition BreakBefore => LineBreakCondition.BreakRestrained;
        public override LineBreakCondition BreakAfter => _part.Kind == InlineMarkdownLayout.DecorationKind.Spacing ? LineBreakCondition.BreakPossible : LineBreakCondition.BreakRestrained;
        public override bool HasFixedSize => true;
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) => new(_width, _sample.Height + _bottomSpacing, _sample.Baseline);
        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) => new(0, -_sample.Baseline, _width, _sample.Height);
        public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways)
        {
            var size = Properties.FontRenderingEmSize; var brush = Properties.ForegroundBrush;
            var x = origin.X + _part.Indent * size / 14; var top = origin.Y - _sample.Baseline;
            switch (_part.Kind)
            {
                case InlineMarkdownLayout.DecorationKind.Bullet:
                    drawingContext.DrawEllipse(brush, null, new Point(x + size * .4, top + _sample.Height * .55), size * .12, size * .12); break;
                case InlineMarkdownLayout.DecorationKind.Number:
                    drawingContext.DrawText(_label, new Point(x, top)); break;
                case InlineMarkdownLayout.DecorationKind.Task:
                    var box = new Rect(x + 1, top + (_sample.Height - size * .72) / 2, size * .72, size * .72);
                    drawingContext.DrawRoundedRectangle(null, new Pen(brush, 1), box, 2, 2);
                    if (_part.Label.Contains('x') || _part.Label.Contains('X'))
                    {
                        var pen = new Pen(brush, 1.4);
                        drawingContext.DrawLine(pen, new Point(box.Left + 2, box.Top + box.Height * .5), new Point(box.Left + box.Width * .45, box.Bottom - 2));
                        drawingContext.DrawLine(pen, new Point(box.Left + box.Width * .45, box.Bottom - 2), new Point(box.Right - 1, box.Top + 2));
                    }
                    break;
                case InlineMarkdownLayout.DecorationKind.Quote:
                    drawingContext.DrawLine(new Pen(Brushes.SlateGray, 2), new Point(origin.X + 2, top + 1), new Point(origin.X + 2, top + _sample.Height - 1)); break;
                case InlineMarkdownLayout.DecorationKind.Rule:
                    drawingContext.DrawLine(new Pen(Brushes.SlateGray, 1), new Point(origin.X, top + _sample.Height / 2), new Point(origin.X + _width, top + _sample.Height / 2)); break;
                case InlineMarkdownLayout.DecorationKind.TableEdge:
                case InlineMarkdownLayout.DecorationKind.TableGap:
                    var borderX = _part.Kind == InlineMarkdownLayout.DecorationKind.TableEdge ? origin.X + 1 : origin.X + _width - 6 * size / 14;
                    drawingContext.DrawLine(new Pen(Brushes.SlateGray, .7), new Point(borderX, top), new Point(borderX, top + _sample.Height)); break;
                case InlineMarkdownLayout.DecorationKind.TableRule:
                    drawingContext.DrawLine(new Pen(Brushes.SlateGray, .7), new Point(origin.X + 1, top + _sample.Height / 2), new Point(origin.X + _width - 6 * size / 14, top + _sample.Height / 2)); break;
            }
        }
    }
}
