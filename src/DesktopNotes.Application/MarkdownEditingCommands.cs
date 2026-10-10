using System.Text.RegularExpressions;

namespace DesktopNotes.Application;

public static class MarkdownEditingCommands
{
    public static MarkdownTextEdit SetHeading(string text, int start, int length, int level)
    {
        if (level is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(level));
        var first = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var last = text.IndexOf('\n', Math.Min(text.Length, start + Math.Max(0, length - 1))); if (last < 0) last = text.Length;
        var original = text[first..last]; var lines = original.Split('\n');
        var pattern = new Regex(@"^(?<indent> {0,3})(?<hashes>#{1,6})[ \t]+");
        var nonempty = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        var remove = level == 0 || nonempty.Length > 0 && nonempty.All(line => pattern.Match(line) is { Success: true } match && match.Groups["hashes"].Length == level);
        var changed = string.Join('\n', lines.Select(line =>
        {
            if (lines.Length > 1 && string.IsNullOrWhiteSpace(line)) return line;
            var match = pattern.Match(line);
            var indent = match.Success ? match.Groups["indent"].Value : "";
            var content = match.Success ? line[match.Length..] : line;
            if (match.Success) content = Regex.Replace(content, @"[ \t]+#+[ \t]*(?=\r?$)", "");
            return indent + (remove ? "" : new string('#', level) + " ") + content;
        }));
        return new(first, original.Length, changed, 0, changed.Length);
    }

    public static MarkdownTextEdit DeleteLines(string text, int start, int length)
    {
        var first = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var last = text.IndexOf('\n', Math.Min(text.Length, start + Math.Max(0, length - 1)));
        if (last >= 0) last++;
        else
        {
            last = text.Length;
            if (first > 0) { first--; if (first > 0 && text[first - 1] == '\r') first--; }
        }
        return new(first, last - first, "", 0, 0);
    }

    public static MarkdownTextEdit IndentLines(string text, int start, int length, bool unindent)
    {
        var first = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var last = text.IndexOf('\n', Math.Min(text.Length, start + Math.Max(0, length - 1))); if (last < 0) last = text.Length;
        return Indent(text, first, Math.Max(1, last - first), unindent);
    }

    public static MarkdownTextEdit ToggleTasks(string text, int start, int length)
    {
        if (length == 0) return ToggleTask(text, start);
        var first = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var last = text.IndexOf('\n', start + length - 1); if (last < 0) last = text.Length;
        var original = text[first..last];
        var changed = string.Join('\n', original.Split('\n').Select(line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return line;
            var edit = ToggleTask(line, 0);
            return line.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        }));
        return new(first, original.Length, changed, 0, changed.Length);
    }

    public static MarkdownTextEdit? PairCharacter(string text, int start, int length, char character)
    {
        var pairs = new Dictionary<char, char> { ['('] = ')', ['['] = ']', ['{'] = '}', ['"'] = '"', ['\''] = '\'' };
        if (length == 0 && character is '\'' or '"' && start > 0 && char.IsLetterOrDigit(text[start - 1]) && text.LastIndexOf(character, start - 1) < 0) return null;
        if (length == 0 && start < text.Length && character == text[start] && pairs.Values.Contains(character))
            return new(start, 0, "", 1, 0);
        if (!pairs.TryGetValue(character, out var close)) return null;
        if (length == 0 && (character is '\'' or '"') && start > 0 && char.IsLetterOrDigit(text[start - 1])) return null;
        if (length == 0 && start < text.Length && char.IsLetterOrDigit(text[start])) return null;
        return new(start, length, character + text.Substring(start, length) + close, 1, length);
    }

    public static MarkdownTextEdit ToggleInline(string text, int start, int length, string marker)
    {
        if (marker == "**" && length > 0) return MarkdownFormatting.ToggleBold(text, start, length);
        var selected = text.Substring(start, length);
        if (selected.Length >= marker.Length * 2 && selected.StartsWith(marker, StringComparison.Ordinal) && selected.EndsWith(marker, StringComparison.Ordinal))
            return new(start, length, selected[marker.Length..^marker.Length], 0, length - marker.Length * 2);
        if (start >= marker.Length && start + length + marker.Length <= text.Length &&
            text.AsSpan(start - marker.Length, marker.Length).SequenceEqual(marker) && text.AsSpan(start + length, marker.Length).SequenceEqual(marker))
            return new(start - marker.Length, length + marker.Length * 2, selected, 0, length);
        return new(start, length, marker + selected + marker, marker.Length, length);
    }

    public static MarkdownTextEdit ContinueLine(string text, int caret, string? listItemPrefix = null)
    {
        var start = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var current = text[start..caret];
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var match = Regex.Match(current, @"^([ \t]*)(?:(?<bullet>[-+*])|(?<number>\d+)(?<delimiter>[.)])|(?<quote>>)) (?<task>\[[ xX]\] )?");
        var isContinuation = !match.Success && listItemPrefix is not null;
        if (isContinuation) match = Regex.Match(listItemPrefix!, @"^([ \t]*)(?:(?<bullet>[-+*])|(?<number>\d+)(?<delimiter>[.)])) (?<task>\[[ xX]\] )?");
        if (!match.Success) return new(caret, 0, newline, newline.Length, 0);
        var lineEnd = text.IndexOf('\n', caret); if (lineEnd < 0) lineEnd = text.Length;
        if (lineEnd > start && text[lineEnd - 1] == '\r') lineEnd--;
        if (!isContinuation && string.IsNullOrWhiteSpace(text[(start + match.Length)..lineEnd]))
        {
            return new(start, lineEnd - start, "", 0, 0);
        }
        var prefix = match.Groups[1].Value;
        if (match.Groups["number"].Success && int.TryParse(match.Groups["number"].Value, out var number) && number < int.MaxValue)
            prefix += (number + 1) + match.Groups["delimiter"].Value + " ";
        else prefix += (match.Groups["bullet"].Success ? match.Groups["bullet"].Value : ">") + " ";
        if (match.Groups["task"].Success) prefix += "[ ] ";
        return new(caret, 0, newline + prefix, newline.Length + prefix.Length, 0);
    }

    public static MarkdownTextEdit PrefixLines(string text, int start, int length, string prefix)
    {
        var first = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var last = text.IndexOf('\n', Math.Min(text.Length, start + Math.Max(0, length - 1))); if (last < 0) last = text.Length;
        var original = text[first..last]; var lines = original.Split('\n');
        var remove = lines.All(line => line.StartsWith(prefix, StringComparison.Ordinal));
        var changed = string.Join('\n', lines.Select(line => remove ? line[prefix.Length..] :
            prefix.StartsWith('#') ? prefix + Regex.Replace(line, @"^#{1,6} ", "") : prefix + line));
        return new(first, original.Length, changed, 0, changed.Length);
    }

    public static MarkdownTextEdit Indent(string text, int start, int length, bool unindent)
    {
        if (length == 0 && !unindent) return new(start, 0, "  ", 2, 0);
        var first = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var last = text.IndexOf('\n', Math.Min(text.Length, start + Math.Max(0, length - 1))); if (last < 0) last = text.Length;
        var original = text[first..last];
        var changed = string.Join('\n', original.Split('\n').Select(line => unindent ? Regex.Replace(line, @"^(\t| {1,2})", "") : "  " + line));
        return new(first, original.Length, changed, 0, changed.Length);
    }

    public static MarkdownTextEdit ToggleTask(string text, int caret)
    {
        var first = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var last = text.IndexOf('\n', caret); if (last < 0) last = text.Length;
        var line = text[first..last]; var match = Regex.Match(line, @"^\s*[-+*] \[(?<check>[ xX])\]");
        if (match.Success)
        {
            var index = first + match.Groups["check"].Index;
            return new(index, 1, match.Groups["check"].Value == " " ? "x" : " ", 1, 0);
        }
        var indent = Regex.Match(line, @"^[ \t]*").Value;
        var content = line[indent.Length..];
        var bullet = Regex.Match(content, @"^[-+*] ");
        var changed = indent + "- [ ] " + content[bullet.Length..];
        return new(first, line.Length, changed, Math.Min(changed.Length, Math.Max(indent.Length + 6, caret - first + 6 - bullet.Length)), 0);
    }
}
