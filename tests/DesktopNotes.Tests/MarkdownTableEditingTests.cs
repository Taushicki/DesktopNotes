using DesktopNotes.Application;
using Markdig;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class MarkdownTableEditingTests
{
    private const string Table = "| A | B |\n| :--- | ---: |\n| one | two |";

    private static string Apply(string text, MarkdownTextEdit edit) => text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);

    [Theory]
    [InlineData("|")]
    [InlineData("---")]
    [InlineData(" **bold** ")]
    [InlineData("  ")]
    [InlineData("a\\|b")]
    [InlineData("a\\\\|b")]
    [InlineData("Привет | мир\nвторая строка")]
    public void CellCharactersCannotChangeTableStructureAndSurviveRoundTrip(string value)
    {
        var table = Assert.Single(MarkdownTableEditing.ReadTables(Table));
        table.Rows[0][0] = value; table.Rows[1][1] = value;
        var serialized = MarkdownTableEditing.Serialize(table);
        var restored = Assert.Single(MarkdownTableEditing.ReadTables(serialized));
        Assert.Equal(2, restored.ColumnCount); Assert.Equal(2, restored.Rows.Length);
        Assert.Equal(value, restored.Rows[0][0]); Assert.Equal(value, restored.Rows[1][1]);
        Assert.Equal(table.Alignment, restored.Alignment);
    }

    [Fact]
    public void RowIsInsertedBeforeFollowingTextWithoutChangingOtherContent()
    {
        var source = "# Title\n\n" + Table + "\n\noutside\n\n" + Table;
        var start = source.IndexOf("| A", StringComparison.Ordinal);
        var edit = Assert.IsType<MarkdownTextEdit>(MarkdownTableEditing.Expand(source, start, TableExpansion.Row));
        Assert.Equal("# Title\n\n" + Table + "\n|  |  |\n\noutside\n\n" + Table, Apply(source, edit));
        Assert.Equal(' ', Apply(source, edit)[edit.Start + edit.SelectionStart]);
    }

    [Theory]
    [InlineData(TableExpansion.ColumnLeft, "|  | A | B |\n| --- | :--- | ---: |\n|  | one | two |")]
    [InlineData(TableExpansion.ColumnRight, "| A | B |  |\n| :--- | ---: | --- |\n| one | two |  |")]
    public void ColumnUpdatesEveryRowAndPreservesExistingAlignment(TableExpansion direction, string expected)
    {
        var edit = Assert.IsType<MarkdownTextEdit>(MarkdownTableEditing.Expand(Table, 0, direction));
        Assert.Equal(expected, Apply(Table, edit));
        Assert.Equal(' ', edit.Replacement[edit.SelectionStart]);
    }

    [Theory]
    [InlineData(TableExpansion.Row)]
    [InlineData(TableExpansion.ColumnLeft)]
    [InlineData(TableExpansion.ColumnRight)]
    public void CrLfAndEscapedPipeContentSurviveExpansion(TableExpansion direction)
    {
        var source = "| A | B |\r\n| --- | --- |\r\n| one \\| two | **bold** |";
        var edit = Assert.IsType<MarkdownTextEdit>(MarkdownTableEditing.Expand(source, 0, direction));
        var result = Apply(source, edit);
        Assert.Contains("one \\| two", result); Assert.Contains("**bold**", result);
        Assert.DoesNotContain("\n", result.Replace("\r\n", "", StringComparison.Ordinal));
        var expected = direction == TableExpansion.Row ? 2 : 3;
        var parsed = Markdig.Markdown.Parse(result, new Markdig.MarkdownPipelineBuilder().UsePipeTables().UsePreciseSourceLocation().Build());
        var table = Assert.IsType<Markdig.Extensions.Tables.Table>(Assert.Single(parsed));
        Assert.Equal(expected, Assert.IsType<Markdig.Extensions.Tables.TableRow>(table[0]).Count);
    }

    [Fact]
    public void PipeLessAndShortRowsAreExpandedWithoutDroppingContent()
    {
        const string source = "A | B\n| --- | --- |\none |";
        var edit = Assert.IsType<MarkdownTextEdit>(MarkdownTableEditing.Expand(source, 0, TableExpansion.ColumnRight));
        Assert.Equal("|A | B|  |\n| --- | --- | --- |\n|one |  |  |", Apply(source, edit));
    }

    [Fact]
    public void ExtraBodyCellsStayBeforeTheNewRightColumn()
    {
        const string source = "| A | B |\n| --- | --- |\n| one | two | extra |";
        var edit = Assert.IsType<MarkdownTextEdit>(MarkdownTableEditing.Expand(source, 0, TableExpansion.ColumnRight));
        Assert.Equal("| A | B |  |  |\n| --- | --- | --- | --- |\n| one | two | extra |  |", Apply(source, edit));
    }

    [Fact]
    public void StaleTableOffsetCannotModifyPlainTextOrFencedCode()
    {
        Assert.Null(MarkdownTableEditing.Expand("plain text", 0, TableExpansion.Row));
        var code = new string((char)96, 3) + "\n" + Table + "\n" + new string((char)96, 3);
        Assert.Null(MarkdownTableEditing.Expand(code, 4, TableExpansion.ColumnLeft));
    }
}
