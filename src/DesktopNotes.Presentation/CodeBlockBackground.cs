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
            if (!frame.HasClosingFence && frame.ContentStart >= frame.End) continue;
            var lines = textView.VisualLines.Where(line => line.FirstDocumentLine.Offset < frame.End && line.LastDocumentLine.EndOffset >= frame.Start).ToArray();
            if (lines.Length == 0) continue;
            var first = editor.Document.GetLineByOffset(frame.Start);
            var last = editor.Document.GetLineByOffset(Math.Max(frame.Start, frame.End - 1));
            var top = textView.GetVisualTopByDocumentLine(first.LineNumber) - textView.ScrollOffset.Y;
            var bottom = (last.LineNumber < editor.Document.LineCount
                ? textView.GetVisualTopByDocumentLine(last.LineNumber + 1) : textView.DocumentHeight) - textView.ScrollOffset.Y;
            if (bottom <= top) continue;
            drawingContext.DrawRoundedRectangle(_fill, _outline, new Rect(0.5, top + 0.5, textView.ActualWidth - 1, Math.Max(1, bottom - top - 1)), 6, 6);
        }
    }
}
