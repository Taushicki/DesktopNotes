namespace DesktopNotes.Application;

public sealed record MarkdownTextEdit(int Start, int Length, string Replacement, int SelectionStart, int SelectionLength);

public static class MarkdownFormatting
{
    // Return a single replacement so the editor can retain its selection and undo history.
    public static MarkdownTextEdit ToggleBold(string text, int start, int length)
    {
        if (start < 0 || length <= 0 || start > text.Length - length) throw new ArgumentOutOfRangeException(nameof(start));
        var selected = text.Substring(start, length);
        const string paragraphs = @"(\r?\n[ \t]*\r?\n(?:[ \t]*\r?\n)*)";
        static bool Wrapped(string value) => value.Length >= 4 && value.StartsWith("**", StringComparison.Ordinal) && value.EndsWith("**", StringComparison.Ordinal);
        if (Wrapped(selected) && !System.Text.RegularExpressions.Regex.IsMatch(selected, paragraphs))
            return new(start, length, selected[2..^2], 0, length - 4);
        if (start >= 2 && start + length + 2 <= text.Length && text.AsSpan(start - 2, 2).SequenceEqual("**") && text.AsSpan(start + length, 2).SequenceEqual("**"))
            return new(start - 2, length + 4, selected, 0, length);
        var leading = selected.Length - selected.TrimStart().Length;
        var content = selected.Trim();
        if (content.Length == 0) return new(start, length, selected, 0, length);
        // Markdown emphasis cannot cross a blank paragraph; decorate each paragraph separately.
        var parts = System.Text.RegularExpressions.Regex.Split(content, paragraphs);
        var remove = parts.Where((_, index) => index % 2 == 0).All(value => Wrapped(value.Trim()));
        for (var i = 0; i < parts.Length; i += 2)
        {
            var part = parts[i]; var trimmed = part.Trim();
            var offset = part.Length - part.TrimStart().Length;
            if (trimmed.Length == 0) continue;
            parts[i] = part[..offset] + (remove ? trimmed[2..^2] : "**" + trimmed + "**") + part[(offset + trimmed.Length)..];
        }
        var formatted = string.Concat(parts);
        var replacement = selected[..leading] + formatted + selected[(leading + content.Length)..];
        return new(start, length, replacement, leading, formatted.Length);
    }
}
