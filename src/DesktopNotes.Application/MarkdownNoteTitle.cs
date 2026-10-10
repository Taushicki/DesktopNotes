using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DesktopNotes.Application;

/// <summary>Derives a note's display name from its first nonempty top-level H1.</summary>
public static class MarkdownNoteTitle
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseEmphasisExtras().DisableHtml().Build();

    public static string Read(string markdown, string fallback = "Без названия")
    {
        foreach (var heading in Markdown.Parse(markdown, Pipeline).OfType<HeadingBlock>().Where(h => h.Level == 1))
        {
            var text = new StringBuilder();
            if (heading.Inline is not null) Append(heading.Inline, text);
            var title = Regex.Replace(text.ToString(), @"\s+", " ").Trim();
            if (title.Length > 0) return title;
        }
        return string.IsNullOrWhiteSpace(fallback) ? "Без названия" : fallback;
    }

    private static void Append(ContainerInline container, StringBuilder text)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: text.Append(literal.Content.ToString()); break;
                case CodeInline code: text.Append(code.Content); break;
                case LineBreakInline: text.Append(' '); break;
                case AutolinkInline link: text.Append(link.Url); break;
                case LinkInline { IsImage: true }: break;
                case ContainerInline nested: Append(nested, text); break;
            }
        }
    }
}
