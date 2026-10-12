using DesktopNotes.Application;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class MarkdownFormattingTests
{
    [Theory]
    [InlineData("before word after", 7, 4, "before **word** after")]
    [InlineData("before **word** after", 9, 4, "before word after")]
    [InlineData("before **word** after", 7, 8, "before word after")]
    [InlineData("word", 0, 4, "**word**")]
    [InlineData(" word ", 0, 6, " **word** ")]
    [InlineData("one\n\ntwo", 0, 8, "**one**\n\n**two**")]
    [InlineData("**one**\n\n**two**", 0, 16, "one\n\ntwo")]
    [InlineData("   ", 0, 3, "   ")]
    public void BoldOnlyChangesTheSelectedRange(string text, int start, int length, string expected)
    {
        var edit = MarkdownFormatting.ToggleBold(text, start, length);
        Assert.Equal(expected, text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement));
    }
    [Theory]
    [InlineData("**word**", 0, 8, "*", "***word***")]
    [InlineData("**word**", 2, 4, "*", "***word***")]
    [InlineData("***word***", 3, 4, "*", "**word**")]
    [InlineData("***word***", 0, 10, "**", "*word*")]
    [InlineData("**one two**", 2, 3, "**", "one **two**")]
    [InlineData("**one** two", 0, 11, "**", "**one two**")]
    [InlineData("`**literal**`", 0, 13, "*", "*`**literal**`*")]
    [InlineData("[**word**](https://example.com)", 0, 31, "*", "[***word***](https://example.com)")]
    public void InlineCommandsPreserveOtherStyles(string text, int start, int length, string marker, string expected)
    {
        var edit = MarkdownEditingCommands.ToggleInline(text, start, length, marker);
        Assert.Equal(expected, text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement));
    }

    [Fact]
    public void AllInlineStylesCanBeAddedAndRemovedInEveryOrder()
    {
        static IEnumerable<string[]> Orders(string[] markers) => markers.Length == 0 ? new[] { Array.Empty<string>() } :
            markers.SelectMany((marker, index) => Orders(markers.Where((_, i) => i != index).ToArray()).Select(rest => new[] { marker }.Concat(rest).ToArray()));
        foreach (var order in Orders(["**", "*", "~~", "==", "`", "++"]))
        {
            var text = "before word after"; var start = 7; var length = 4;
            foreach (var marker in order)
            {
                var edit = MarkdownEditingCommands.ToggleInline(text, start, length, marker);
                text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
                start = edit.Start + edit.SelectionStart; length = edit.SelectionLength;
                Assert.Contains("word", text.Substring(start, length));
            }
            Assert.Equal("before ==~~++***`word`***++~~== after", text);
            foreach (var marker in order)
            {
                var edit = MarkdownEditingCommands.ToggleInline(text, start, length, marker);
                text = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
                start = edit.Start + edit.SelectionStart; length = edit.SelectionLength;
            }
            Assert.Equal("before word after", text);
        }
    }

}
