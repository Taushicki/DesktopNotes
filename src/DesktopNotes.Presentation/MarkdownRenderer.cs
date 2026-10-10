using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DesktopNotes.Presentation;

public interface IMarkdownRenderer { FlowDocument Render(string markdown); }

public sealed class MarkdownRenderer : IMarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();
    public FlowDocument Render(string markdown)
    {
        var document = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 14, PagePadding = new Thickness(8), Foreground = Brushes.DarkSlateGray };
        foreach (var block in Markdig.Markdown.Parse(markdown, _pipeline))
            document.Blocks.Add(RenderBlock(block));
        return document;
    }

    private static System.Windows.Documents.Block RenderBlock(Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = Paragraph(heading.Inline);
                title.FontSize = Math.Max(15, 28 - heading.Level * 2);
                title.FontWeight = FontWeights.SemiBold;
                return title;
            case ParagraphBlock paragraph: return Paragraph(paragraph.Inline);
            case CodeBlock code:
                return new Paragraph(new Run(code.Lines.ToString()))
                {
                    FontFamily = new FontFamily("Consolas"), FontSize = 12,
                    Background = new SolidColorBrush(Color.FromArgb(22, 0, 0, 0)), Padding = new Thickness(8)
                };
            case ListBlock list:
                var result = new System.Windows.Documents.List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(16, 4, 0, 4)
                };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) result.StartIndex = start;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var listItem = new ListItem();
                    foreach (var child in item) listItem.Blocks.Add(RenderBlock(child));
                    result.ListItems.Add(listItem);
                }
                return result;
            case QuoteBlock quote:
                var section = new Section { BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0) };
                foreach (var child in quote) section.Blocks.Add(RenderBlock(child));
                return section;
            case ThematicBreakBlock: return new Paragraph(new Run("────────────")) { Foreground = Brushes.SlateGray };
            default: return new Paragraph();
        }
    }

    private static Paragraph Paragraph(ContainerInline? inline)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 4, 0, 8) };
        if (inline is not null) AddInlines(paragraph.Inlines, inline);
        return paragraph;
    }

    private static void AddInlines(InlineCollection target, ContainerInline container)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case CodeInline code: target.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas"), Background = new SolidColorBrush(Color.FromArgb(22, 0, 0, 0)) }); break;
                case LineBreakInline line: target.Add(line.IsHard ? new LineBreak() : new Run(" ")); break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterCount == 2 ? new Bold() : new Italic();
                    AddInlines(span.Inlines, emphasis);
                    target.Add(span);
                    break;
                case LinkInline link when link.IsImage: target.Add(new Run("[изображение]")); break;
                case LinkInline link:
                    if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    {
                        var hyperlink = new Hyperlink { NavigateUri = uri };
                        AddInlines(hyperlink.Inlines, link);
                        hyperlink.RequestNavigate += (_, args) =>
                        {
                            try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
                            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                            { System.Windows.MessageBox.Show("Не удалось открыть ссылку: " + ex.Message, "DesktopNotes"); }
                            args.Handled = true;
                        };
                        target.Add(hyperlink);
                    }
                    else AddInlines(target, link);
                    break;
                case AutolinkInline auto: target.Add(new Run(auto.Url)); break;
                case ContainerInline nested: AddInlines(target, nested); break;
            }
        }
    }
}
