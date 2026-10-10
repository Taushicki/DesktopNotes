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
}
