using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DesktopNotes.Presentation;

public sealed record MarkdownRenderActions(Action<int, bool>? ToggleTask = null, Action<string>? OpenNote = null);
public interface IMarkdownRenderer { FlowDocument Render(string markdown, MarkdownRenderActions? actions = null); }

public sealed class MarkdownRenderer : IMarkdownRenderer
{
    private readonly MarkdownPipeline _pipeline = MarkdownPipelines.Create();
    public FlowDocument Render(string markdown, MarkdownRenderActions? actions = null)
    {
        var document = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 14, PagePadding = new Thickness(4), Foreground = Brushes.DarkSlateGray };
        foreach (var block in Markdig.Markdown.Parse(markdown, _pipeline))
            document.Blocks.Add(RenderBlock(block, actions));
        return document;
    }

    private static System.Windows.Documents.Block RenderBlock(Markdig.Syntax.Block block, MarkdownRenderActions? actions)
    {
        switch (block)
        {
            case Markdig.Extensions.Tables.Table table:
                var visualTable = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 2) };
                var rows = new System.Windows.Documents.TableRowGroup(); visualTable.RowGroups.Add(rows);
                foreach (var row in table.OfType<Markdig.Extensions.Tables.TableRow>())
                {
                    var visualRow = new System.Windows.Documents.TableRow(); rows.Rows.Add(visualRow);
                    foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
                    {
                        var visualCell = new System.Windows.Documents.TableCell { BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(0.5), Padding = new Thickness(3),
                            FontWeight = row.IsHeader ? FontWeights.Bold : FontWeights.Normal };
                        foreach (var child in cell) visualCell.Blocks.Add(RenderBlock(child, actions));
                        visualRow.Cells.Add(visualCell);
                    }
                }
                return visualTable;
            case HeadingBlock heading:
                var title = Paragraph(heading.Inline, actions);
                title.FontSize = Math.Max(15, 28 - heading.Level * 2);
                title.FontWeight = FontWeights.SemiBold;
                return title;
            case ParagraphBlock paragraph: return Paragraph(paragraph.Inline, actions);
            case CodeBlock code:
                return new Paragraph(new Run(code.Lines.ToString()))
                {
                    FontFamily = new FontFamily("Consolas"), FontSize = 12,
                    Background = new SolidColorBrush(Color.FromArgb(22, 0, 0, 0)), Padding = new Thickness(4), Margin = new Thickness(0)
                };
            case ListBlock list:
                var result = new System.Windows.Documents.List
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0), Padding = new Thickness(16, 0, 0, 0), MarkerOffset = 3
                };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0) result.StartIndex = start;
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var listItem = new ListItem { Margin = new Thickness(0), Padding = new Thickness(0) };
                    foreach (var child in item) listItem.Blocks.Add(RenderBlock(child, actions));
                    result.ListItems.Add(listItem);
                }
                return result;
            case QuoteBlock quote:
                var section = new Section { BorderBrush = Brushes.SlateGray, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(6, 0, 0, 0), Margin = new Thickness(0) };
                foreach (var child in quote) section.Blocks.Add(RenderBlock(child, actions));
                return section;
            case ThematicBreakBlock: return new Paragraph(new Run("────────────")) { Foreground = Brushes.SlateGray };
            default: return new Paragraph();
        }
    }

    private static Paragraph Paragraph(ContainerInline? inline, MarkdownRenderActions? actions)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        if (inline is not null) AddInlines(paragraph.Inlines, inline, actions);
        return paragraph;
    }

    private static void AddInlines(InlineCollection target, ContainerInline container, MarkdownRenderActions? actions)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case WikiLinkInline wiki:
                    var wikiLink = new Hyperlink(new Run(wiki.Label)) { Foreground = Brushes.SteelBlue };
                    wikiLink.Click += (_, _) => actions?.OpenNote?.Invoke(wiki.Destination);
                    target.Add(wikiLink); break;
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case Markdig.Extensions.TaskLists.TaskList task:
                    var checkbox = new System.Windows.Controls.CheckBox { IsChecked = task.Checked, Focusable = false, VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = actions?.ToggleTask is not null };
                    System.Windows.Automation.AutomationProperties.SetName(checkbox, UiText.Get("Задача выполнена"));
                    checkbox.Click += (_, _) => actions?.ToggleTask?.Invoke(task.Span.Start, checkbox.IsChecked == true);
                    target.Add(new InlineUIContainer(checkbox)); break;
                case CodeInline code: target.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas"), Background = new SolidColorBrush(Color.FromArgb(22, 0, 0, 0)) }); break;
                // Preserve the lines the user entered, including Shift+Enter.
                // Like Obsidian with strict line breaks disabled, soft breaks remain visible.
                case LineBreakInline: target.Add(new LineBreak()); break;
                case EmphasisInline emphasis:
                    Span span = emphasis.DelimiterCount == 2 ? new Bold() : new Italic();
                    if (emphasis.DelimiterChar == '~') { span = new Span { TextDecorations = TextDecorations.Strikethrough }; }
                    if (emphasis.DelimiterChar == '+') { span = new Span { TextDecorations = TextDecorations.Underline }; }
                    if (emphasis.DelimiterChar == '=') { span = new Span { Background = Brushes.LightGoldenrodYellow }; }
                    AddInlines(span.Inlines, emphasis, actions);
                    target.Add(span);
                    break;
                case LinkInline link when link.IsImage: target.Add(new Run(UiText.Get("[изображение]"))); break;
                case LinkInline link:
                    if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    {
                        var hyperlink = new Hyperlink { NavigateUri = uri };
                        AddInlines(hyperlink.Inlines, link, actions);
                        hyperlink.RequestNavigate += (_, args) =>
                        {
                            try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
                            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                            { System.Windows.MessageBox.Show(UiText.Get("Не удалось открыть ссылку: ") + ex.Message, "DesktopNotes"); }
                            args.Handled = true;
                        };
                        target.Add(hyperlink);
                    }
                    else AddInlines(target, link, actions);
                    break;
                case AutolinkInline auto: target.Add(new Run(auto.Url)); break;
                case ContainerInline nested: AddInlines(target, nested, actions); break;
            }
        }
    }

}
