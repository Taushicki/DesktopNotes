using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace DesktopNotes.Presentation;

internal static class InlineMarkdownMetrics
{
    internal static double Measure(LiveMarkdownEditor editor, InlineMarkdownLayout.Cell cell)
    {
        var content = new StringBuilder(); var offsets = new List<int>();
        for (var index = cell.Start; index < cell.Start + cell.Length; index++)
        {
            if (editor.Layout.Decorations.Any(part => part.Kind == InlineMarkdownLayout.DecorationKind.Hidden && index >= part.Start && index < part.Start + part.Length)) continue;
            content.Append(editor.Text[index]); offsets.Add(index);
        }
        if (content.Length == 0) return 0;
        var text = new FormattedText(content.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch), editor.FontSize, editor.Foreground, 1);
        foreach (var range in editor.Layout.Styles)
        {
            var first = offsets.FindIndex(offset => offset >= range.Start && offset < range.Start + range.Length);
            if (first < 0) continue;
            var last = offsets.FindLastIndex(offset => offset >= range.Start && offset < range.Start + range.Length);
            var count = last - first + 1;
            if (range.Style.HasFlag(InlineMarkdownLayout.Style.Bold)) text.SetFontWeight(FontWeights.Bold, first, count);
            if (range.Style.HasFlag(InlineMarkdownLayout.Style.Italic)) text.SetFontStyle(FontStyles.Italic, first, count);
            if (range.Style.HasFlag(InlineMarkdownLayout.Style.Code)) text.SetFontFamily("Consolas", first, count);
            if (range.Scale != 1) text.SetFontSize(editor.FontSize * range.Scale, first, count);
        }
        return text.WidthIncludingTrailingWhitespace;
    }
}
