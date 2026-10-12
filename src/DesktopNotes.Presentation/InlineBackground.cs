using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace DesktopNotes.Presentation;

/// <summary>Inline fills belong below selection, rather than to opaque glyph runs.</summary>
internal sealed class InlineBackground(LiveMarkdownEditor editor) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0) return;
        var first = textView.VisualLines[0].StartOffset;
        var last = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        // A highlight wins over code fill when both styles cover the same text.
        foreach (var style in new[] { InlineMarkdownLayout.Style.Code, InlineMarkdownLayout.Style.Highlight })
        {
            var brush = style == InlineMarkdownLayout.Style.Highlight ? editor.Theme.Highlight : editor.Theme.InlineCode;
            foreach (var range in editor.Layout.Styles)
            {
                if (!range.Style.HasFlag(style) || range.Style.HasFlag(InlineMarkdownLayout.Style.BlockCode) || range.Start > last || range.Start + range.Length < first) continue;
                var geometry = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 1 };
                geometry.AddSegment(textView, new TextSegment { StartOffset = range.Start, Length = range.Length });
                if (geometry.CreateGeometry() is { } fill) drawingContext.DrawGeometry(brush, null, fill);
            }
        }
    }
}
