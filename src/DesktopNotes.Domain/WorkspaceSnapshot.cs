using System.Globalization;

namespace DesktopNotes.Domain;

public sealed record NoteGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public IReadOnlyList<Guid> NoteIds { get; init; } = [];
    public string BorderColor { get; init; } = "#3B82F6";
}

public enum LinkLinePattern { Solid, Dashed, Dotted }
public enum LinkRoute { Straight, Curved }
public enum NoteSide { Left, Top, Right, Bottom }

public sealed record LinkStyle
{
    public double Width { get; init; } = 2;
    public string Color { get; init; } = "#808080";
    public LinkLinePattern Pattern { get; init; }
    public LinkRoute Route { get; init; }
}

public sealed record NoteLink
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SourceId { get; init; }
    public Guid TargetId { get; init; }
    public NoteSide? SourceSide { get; init; }
    public LinkStyle Style { get; init; } = new();
}

public sealed record WorkspaceSnapshot
{
    public IReadOnlyList<Note> Notes { get; init; } = [];
    public IReadOnlyList<NoteGroup> Groups { get; init; } = [];
    public IReadOnlyList<NoteLink> Links { get; init; } = [];

    public static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#' &&
        uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);

    public void Validate()
    {
        if (Notes is null || Groups is null || Links is null) throw new InvalidDataException("Отсутствуют данные рабочего пространства.");
        var ids = new HashSet<Guid>();
        foreach (var note in Notes)
        {
            if (note is null) throw new InvalidDataException("Пустая заметка.");
            note.Validate();
            if (!ids.Add(note.Id)) throw new InvalidDataException("Повторяющийся идентификатор заметки.");
        }
        var groupIds = new HashSet<Guid>();
        var members = new HashSet<Guid>();
        foreach (var group in Groups)
        {
            if (group is null || group.Id == Guid.Empty || !groupIds.Add(group.Id) ||
                !IsColor(group.BorderColor) || group.NoteIds is null || group.NoteIds.Count < 2)
                throw new InvalidDataException("Некорректная группа.");
            foreach (var id in group.NoteIds)
                if (!ids.Contains(id) || !members.Add(id)) throw new InvalidDataException("Заметка отсутствует или состоит в нескольких группах.");
        }
        var linkIds = new HashSet<Guid>();
        var pairs = new HashSet<(Guid, Guid)>();
        foreach (var link in Links)
        {
            if (link is null || link.Id == Guid.Empty || !linkIds.Add(link.Id) || !ids.Contains(link.SourceId) ||
                !ids.Contains(link.TargetId) || link.SourceId == link.TargetId || !pairs.Add((link.SourceId, link.TargetId)) ||
                (link.SourceSide is { } side && !Enum.IsDefined(side)) ||
                link.Style is null || !IsColor(link.Style.Color) || !double.IsFinite(link.Style.Width) ||
                link.Style.Width <= 0 || link.Style.Width > 20 || !Enum.IsDefined(link.Style.Pattern) || !Enum.IsDefined(link.Style.Route))
                throw new InvalidDataException("Некорректная связь.");
        }
    }
}
