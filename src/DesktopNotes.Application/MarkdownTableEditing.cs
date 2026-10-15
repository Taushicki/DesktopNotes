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


    public static IReadOnlyList<MarkdownTableModel> ReadTables(string text)
        => ReadTables(text, Markdown.Parse(text, Pipeline));

    public static IReadOnlyList<MarkdownTableModel> ReadTables(string text, Markdig.Syntax.MarkdownDocument document)
    {
        return document.OfType<Table>().Select(table =>
        {
            var end = table.Span.End + 1;
            while (end > table.Span.Start && text[end - 1] is '\r' or '\n') end--;
            var raw = text[table.Span.Start..end];
            var lines = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var cells = lines.Select(line => Cells(line).Select(value => DecodeCell(value.Trim())).ToArray()).ToArray();
            var columns = cells.Max(row => row.Length);
            var rows = cells.Where((_, index) => index != 1).Select(row => row.Concat(Enumerable.Repeat("", columns - row.Length)).ToArray()).ToArray();
            var alignment = cells[1].Concat(Enumerable.Repeat("---", columns - cells[1].Length)).ToArray();
            return new MarkdownTableModel(table.Span.Start, end - table.Span.Start, rows, alignment, raw.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
        }).ToArray();
    }

    public static string EncodeCell(string value)
    {
        var result = new System.Text.StringBuilder();
        var leading = 0; while (leading < value.Length && value[leading] == ' ') leading++;
        var trailing = value.Length; while (trailing > leading && value[trailing - 1] == ' ') trailing--;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                var start = i; while (i < value.Length && value[i] == '\\') i++;
                var count = i - start;
                result.Append('\\', i < value.Length && value[i] == '|' ? count * 2 : count);
                i--; continue;
            }
            if (value[i] == '|') result.Append("\\|");
            else if (value[i] == '\r') { if (i + 1 < value.Length && value[i + 1] == '\n') i++; result.Append("<br>"); }
            else if (value[i] == '\n') result.Append("<br>");
            else if (value[i] == ' ' && (i < leading || i >= trailing)) result.Append("&#32;");
            else result.Append(value[i]);
        }
        return result.ToString();
    }

    public static string DecodeCell(string value)
    {
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                var start = i; while (i < value.Length && value[i] == '\\') i++;
                var count = i - start;
                if (i < value.Length && value[i] == '|' && count % 2 == 1) { result.Append('\\', (count - 1) / 2); result.Append('|'); }
                else { result.Append('\\', count); i--; }
            }
            else result.Append(value[i]);
        }
        return result.ToString().Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase).Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase).Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase).Replace("&#32;", " ", StringComparison.Ordinal);
    }

    public static string Serialize(MarkdownTableModel table)
    {
        string Row(IEnumerable<string> cells) => "| " + string.Join(" | ", cells.Select(EncodeCell)) + " |";
        return string.Join(table.NewLine, new[] { Row(table.Rows[0]), "| " + string.Join(" | ", table.Alignment) + " |" }.Concat(table.Rows.Skip(1).Select(Row)));
    }

    // A backslash escapes a pipe only when preceded by an odd number of backslashes.
    internal static List<string> Cells(string row)
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

public sealed record MarkdownTableModel(int Start, int Length, string[][] Rows, string[] Alignment, string NewLine)
{
    public int ColumnCount => Alignment.Length;
}
