using Markdig;
using Markdig.Syntax;

namespace DesktopNotes.Application;

public static class NoteDeletionPolicy
{
    public static bool RequiresConfirmation(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return false;
        var document = Markdown.Parse(markdown);
        if (document.Count != 1 || document[0] is not HeadingBlock { Level: 1 } heading) return true;
        // Keep confirmation for source that is not rendered (e.g. link definitions).
        return !string.IsNullOrWhiteSpace(markdown[..heading.Span.Start]) ||
            !string.IsNullOrWhiteSpace(markdown[(heading.Span.End + 1)..]);
    }
}
