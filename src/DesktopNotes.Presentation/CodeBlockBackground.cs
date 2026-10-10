using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace DesktopNotes.Presentation;

/// <summary>One frame per code block, independent of caret, focus and wrapped rows.</summary>
internal sealed class CodeBlockBackground(LiveMarkdownEditor editor) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;
    private readonly Brush _fill = new SolidColorBrush(Color.FromArgb(18, 30, 40, 55));
    private readonly Pen _outline = new(new SolidColorBrush(Color.FromArgb(65, 55, 65, 80)), 1);

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid || textView.ActualWidth <= 2) return;
        foreach (var frame in editor.Layout.CodeFrames)
        {
            if (Bounds(textView, frame) is { } bounds)
                drawingContext.DrawRoundedRectangle(_fill, _outline, bounds, 6, 6);
        }
    }

    internal Rect? Bounds(TextView textView, InlineMarkdownLayout.CodeFrame frame)
    {
        if (!frame.HasClosingFence && frame.ContentStart >= frame.End) return null;
        if (frame.ContentStart == frame.ClosingStart) return null;
        if (!textView.VisualLines.Any(line => line.FirstDocumentLine.Offset < frame.End && line.LastDocumentLine.EndOffset >= frame.Start)) return null;
        var start = frame.ContentStart ?? frame.Start;
        var end = frame.ClosingStart ?? frame.End;
        var first = editor.Document.GetLineByOffset(start);
        var last = editor.Document.GetLineByOffset(Math.Max(start, end - 1));
        var top = textView.GetVisualTopByDocumentLine(first.LineNumber) - textView.ScrollOffset.Y;
        var bottom = (last.LineNumber < editor.Document.LineCount
            ? textView.GetVisualTopByDocumentLine(last.LineNumber + 1) : textView.DocumentHeight) - textView.ScrollOffset.Y;
        // Use content rows, not the hidden fence rows, as the padding reference.
        // Consolas glyphs sit slightly below the line-box centre; shift the frame
        // down by two units for optical centring, scaling with the note font.
        if (frame.ContentStart is not null)
        {
            var scale = editor.FontSize / 14;
            top -= 6 * scale;
            bottom += 10 * scale;
        }
        if (bottom <= top) return null;
        return new Rect(0.5, top + 0.5, textView.ActualWidth - 1, Math.Max(1, bottom - top - 1));
    }
}
