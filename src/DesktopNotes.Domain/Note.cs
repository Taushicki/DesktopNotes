namespace DesktopNotes.Domain;

public enum WindowLayer { Normal, Topmost, Background }
public enum NoteColor { Yellow, Peach, Mint, Blue, Lavender, Pink, GitHubDark, MaterialDark, MidnightDark }
public enum NoteTextAlignment { Left, Center, Right, Justify }
public enum NoteVerticalAlignment { Top, Center, Bottom }

public sealed record Note
{
    public const double CollapsedHeight = 44;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = "Новая заметка";
    public string Markdown { get; init; } = "";
    public NoteColor Color { get; init; } = NoteColor.Yellow;
    public WindowLayer Layer { get; init; }
    public double X { get; init; } = 80;
    public double Y { get; init; } = 80;
    public double Width { get; init; } = 320;
    public double Height { get; init; } = 280;
    public bool IsCollapsed { get; init; }
    public bool IsHidden { get; init; }
    public bool IsPinned { get; init; }
    public double FontSize { get; init; } = 14;
    public bool IsBold { get; init; }
    public NoteTextAlignment TextAlignment { get; init; }
    public NoteVerticalAlignment VerticalAlignment { get; init; }

    public void Validate()
    {
        if (Id == Guid.Empty || Title is null || Markdown is null ||
            !Enum.IsDefined(Color) || !Enum.IsDefined(Layer) ||
            !Enum.IsDefined(TextAlignment) || !Enum.IsDefined(VerticalAlignment) ||
            !double.IsFinite(FontSize) || FontSize < 8 || FontSize > 72 ||
            !double.IsFinite(X) || !double.IsFinite(Y) ||
            !double.IsFinite(Width) || !double.IsFinite(Height) || Width < 220 || Height < 140)
            throw new InvalidDataException("Некорректные данные заметки.");
    }
}
