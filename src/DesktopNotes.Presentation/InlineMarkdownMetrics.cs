using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace DesktopNotes.Presentation;

internal static class InlineMarkdownMetrics
{
    private static readonly ConditionalWeakTable<LiveMarkdownEditor, Cache> Caches = new();
    private readonly record struct Key(InlineMarkdownLayout Layout, FontFamily Family, FontStyle Style,
        FontWeight Weight, FontStretch Stretch, double Size, string Culture);
    private static Cache Current(LiveMarkdownEditor editor)
    {
        var cache = Caches.GetValue(editor, _ => new Cache());
        var key = new Key(editor.Layout, editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch, editor.FontSize, CultureInfo.CurrentCulture.Name);
        if (cache.Revision != key) cache.Reset(key, editor.Text);
        return cache;
    }
    internal static long MeasurementCount(LiveMarkdownEditor editor) => Current(editor).Measurements;
    internal static double Measure(LiveMarkdownEditor editor, InlineMarkdownLayout.Cell cell) => Current(editor).Measure(editor, cell);
    internal static double MeasureColumn(LiveMarkdownEditor editor, IReadOnlyList<InlineMarkdownLayout.Cell> column)
    {
        var cache = Current(editor);
        if (!cache.Columns.TryGetValue(column, out var width))
        {
            width = Math.Max(24 * editor.FontSize / 14, column.Select(cell => cache.Measure(editor, cell)).DefaultIfEmpty().Max());
            cache.Columns.Add(column, width);
        }
        return width;
    }

    private sealed class RangeIndex<T>(IEnumerable<T> values, Func<T, int> start, Func<T, int> length)
    {
        private readonly (T Value, int Start, int End, int Order)[] _ranges = values.Select((value, order) =>
            (Value: value, Start: start(value), End: start(value) + length(value), Order: order)).OrderBy(item => item.Start).ToArray();
        private int[]? _maxEnds;
        internal IEnumerable<T> Overlapping(int start, int end)
        {
            if (_maxEnds is null)
            {
                _maxEnds = new int[_ranges.Length]; int maximum = 0;
                for (var i = 0; i < _ranges.Length; i++) _maxEnds[i] = maximum = Math.Max(maximum, _ranges[i].End);
            }
            int low = 0, high = _ranges.Length;
            while (low < high) { var mid = (low + high) / 2; if (_maxEnds[mid] <= start) low = mid + 1; else high = mid; }
            var matches = new List<(T Value, int Order)>();
            for (var i = low; i < _ranges.Length && _ranges[i].Start < end; i++)
                if (_ranges[i].End > start) matches.Add((_ranges[i].Value, _ranges[i].Order));
            return matches.OrderBy(item => item.Order).Select(item => item.Value);
        }
    }

    private sealed class Cache
    {
        internal Key Revision;
        internal long Measurements;
        internal readonly Dictionary<IReadOnlyList<InlineMarkdownLayout.Cell>, double> Columns = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<InlineMarkdownLayout.Cell, double> _cells = [];
        private string _source = "";
        private RangeIndex<InlineMarkdownLayout.Decoration> _hidden = new([], part => part.Start, part => part.Length);
        private RangeIndex<InlineMarkdownLayout.StyledRange> _styles = new([], part => part.Start, part => part.Length);
        internal void Reset(Key revision, string source)
        {
            Revision = revision; Measurements = 0; _source = source; _cells.Clear(); Columns.Clear();
            _hidden = new(revision.Layout.Decorations.Where(part => part.Kind == InlineMarkdownLayout.DecorationKind.Hidden), part => part.Start, part => part.Length);
            _styles = new(revision.Layout.Styles, part => part.Start, part => part.Length);
        }
        internal double Measure(LiveMarkdownEditor editor, InlineMarkdownLayout.Cell cell)
        {
            if (_cells.TryGetValue(cell, out var width)) return width;
            Measurements++;
            var content = new StringBuilder(); var offsets = new List<int>();
            var hidden = new bool[cell.Length]; var end = cell.Start + cell.Length;
            foreach (var part in _hidden.Overlapping(cell.Start, end))
                Array.Fill(hidden, true, Math.Max(cell.Start, part.Start) - cell.Start, Math.Min(end, part.Start + part.Length) - Math.Max(cell.Start, part.Start));
            for (var i = 0; i < cell.Length; i++) if (!hidden[i]) { content.Append(_source[cell.Start + i]); offsets.Add(cell.Start + i); }
            if (content.Length == 0) { _cells[cell] = 0; return 0; }
            var text = new FormattedText(content.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch), editor.FontSize, editor.Foreground, 1);
            foreach (var range in _styles.Overlapping(cell.Start, end))
            {
                int Lower(int value) { var index = offsets.BinarySearch(value); return index < 0 ? ~index : index; }
                var first = Lower(range.Start); var count = Lower(range.Start + range.Length) - first;
                if (count <= 0) continue;
                if (range.HeadingLevel > 0) text.SetFontWeight(range.HeadingLevel == 1 ? FontWeights.Bold : FontWeights.SemiBold, first, count);
                else if (range.Style.HasFlag(InlineMarkdownLayout.Style.Bold)) text.SetFontWeight(FontWeights.Bold, first, count);
                if (range.Style.HasFlag(InlineMarkdownLayout.Style.Italic)) text.SetFontStyle(FontStyles.Italic, first, count);
                if (range.Style.HasFlag(InlineMarkdownLayout.Style.Code)) text.SetFontFamily("Consolas", first, count);
                if (range.Style.HasFlag(InlineMarkdownLayout.Style.BlockCode)) { text.SetFontWeight(FontWeights.Normal, first, count); text.SetFontStyle(FontStyles.Normal, first, count); }
                if (range.Scale != 1) text.SetFontSize(editor.FontSize * range.Scale, first, count);
            }
            return _cells[cell] = text.WidthIncludingTrailingWhitespace;
        }
    }
}
