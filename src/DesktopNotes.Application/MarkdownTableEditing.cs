using Markdig;
using Markdig.Extensions.Tables;

namespace DesktopNotes.Application;

public enum TableExpansion { Row, ColumnLeft, ColumnRight }

public static class MarkdownTableEditing
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UsePreciseSourceLocation().Build();

    public static MarkdownTextEdit? Expand(string text, int tableStart, TableExpansion expansion)
    {
        if (!Enum.IsDefined(expansion)) throw new ArgumentOutOfRangeException(nameof(expansion));
        var table = Markdown.Parse(text, Pipeline).OfType<Table>().FirstOrDefault(table => table.Span.Start == tableStart);
        if (table is null) return null;
        var end = table.Span.End + 1;
        while (end > tableStart && text[end - 1] is '\r' or '\n') end--;
        var original = text[tableStart..end];
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var rows = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var cellRows = rows.Select(Cells).ToArray();
        var columns = cellRows.Max(row => row.Count);
        if (columns == 0 || rows.Length < 2) return null;
        if (expansion == TableExpansion.Row)
        {
            var row = newline + "|" + string.Concat(Enumerable.Repeat("  |", columns));
            return new(end, 0, row, newline.Length + 2, 0);
        }
        var insertion = expansion == TableExpansion.ColumnLeft ? 0 : columns;
        var rewritten = new List<string>();
        var caret = 0;
        for (var index = 0; index < rows.Length; index++)
        {
            var cells = cellRows[index];
            while (cells.Count < columns) cells.Add(index == 1 ? " --- " : "  ");
            cells.Insert(insertion, index == 1 ? " --- " : "  ");
            if (index == 0) caret = 2 + cells.Take(insertion).Sum(cell => cell.Length + 1);
            rewritten.Add("|" + string.Join("|", cells) + "|");
        }
        return new(tableStart, original.Length, string.Join(newline, rewritten), caret, 0);
    }

    // A backslash escapes a pipe only when preceded by an odd number of backslashes.
    private static List<string> Cells(string row)
    {
        var edges = new List<int>(); var slashes = 0;
        for (var index = 0; index < row.Length; index++)
        {
            if (row[index] == '|' && slashes % 2 == 0) edges.Add(index);
            slashes = row[index] == '\\' ? slashes + 1 : 0;
        }
        var result = new List<string>(); var start = 0;
        foreach (var edge in edges) { result.Add(row[start..edge]); start = edge + 1; }
        result.Add(row[start..]);
        if (edges.Count > 0 && string.IsNullOrWhiteSpace(result[^1])) result.RemoveAt(result.Count - 1);
        if (edges.Count > 0 && string.IsNullOrWhiteSpace(result[0])) result.RemoveAt(0);
        return result;
    }
}
