using DesktopNotes.Application;
using Xunit;

namespace DesktopNotes.Tests;

public sealed class NoteDeletionTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData(" \r\n\t", false)]
    [InlineData("# Новая заметка\n\n", false)]
    [InlineData("# \n", false)]
    [InlineData("# **Название**\r\n\r\n  ", false)]
    [InlineData("Название\n===\n", false)]
    [InlineData("# Title\nbody", true)]
    [InlineData("# Title\n\n# Second", true)]
    [InlineData("# Title\n\n```\n\n```", true)]
    [InlineData("# Title\n\n- ", true)]
    [InlineData("# Title\n\n<!-- saved comment -->", true)]
    [InlineData("# Title\n\n[ref]: https://example.com", true)]
    [InlineData("plain text", true)]
    public void OnlyEmptyOrTitleOnlyNotesSkipConfirmation(string markdown, bool expected) =>
        Assert.Equal(expected, NoteDeletionPolicy.RequiresConfirmation(markdown));
}
