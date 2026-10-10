using DesktopNotes.Domain;

namespace DesktopNotes.Application;

public sealed record LoadResult(WorkspaceSnapshot Snapshot, string? Warning = null)
{
    public IReadOnlyList<Note> Notes => Snapshot.Notes;
    public LoadResult(IReadOnlyList<Note> notes, string? warning = null) : this(new WorkspaceSnapshot { Notes = notes }, warning) { }
}

public interface INoteRepository
{
    Task<LoadResult> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(WorkspaceSnapshot snapshot, CancellationToken cancellationToken = default);
}

public interface IWindowLayerController
{
    void Apply(nint handle, WindowLayer layer);
}

public interface IDesktopMenuRegistration
{
    bool IsRegistered { get; }
    void Register();
    void Unregister();
}

public sealed class NoteService(INoteRepository repository)
{
    private readonly Dictionary<Guid, Note> _notes = [];
    private readonly Dictionary<Guid, NoteGroup> _groups = [];
    private readonly Dictionary<Guid, NoteLink> _links = [];
    public IReadOnlyList<Note> Notes => _notes.Values.ToArray();
    public IReadOnlyList<NoteGroup> Groups => _groups.Values.ToArray();
    public IReadOnlyList<NoteLink> Links => _links.Values.ToArray();
    public WorkspaceSnapshot Snapshot => new() { Notes = Notes, Groups = Groups, Links = Links };
    public event Action? Changed;
    public event Action<Note>? NoteUpdated;

    public async Task<string?> LoadAsync()
    {
        var result = await repository.LoadAsync();
        result.Snapshot.Validate();
        _notes.Clear();
        _groups.Clear();
        _links.Clear();
        foreach (var note in result.Notes) { note.Validate(); _notes.Add(note.Id, note); }
        foreach (var group in result.Snapshot.Groups) _groups.Add(group.Id, group);
        foreach (var link in result.Snapshot.Links) _links.Add(link.Id, link);
        return result.Warning;
    }

    public Note Create(double x, double y)
    {
        var note = new Note { X = x, Y = y };
        Update(note);
        return note;
    }

    public void Update(Note note)
    {
        note.Validate();
        if (_notes.TryGetValue(note.Id, out var previous) && previous == note) return;
        _notes[note.Id] = note;
        NoteUpdated?.Invoke(note);
        Changed?.Invoke();
    }

    public void SetHidden(Guid id, bool hidden) => Update(_notes[id] with { IsHidden = hidden });
    public void Delete(Guid id)
    {
        if (!_notes.Remove(id)) return;
        foreach (var link in _links.Values.Where(l => l.SourceId == id || l.TargetId == id).ToArray()) _links.Remove(link.Id);
        var group = GroupOf(id);
        if (group is not null) SetGroupMembers(group.Id, group.NoteIds.Where(member => member != id));
        Changed?.Invoke();
    }
    public Note Get(Guid id) => _notes[id];
    public NoteGroup? GroupOf(Guid id) => _groups.Values.FirstOrDefault(g => g.NoteIds.Contains(id));
    public bool Contains(Guid id) => _notes.ContainsKey(id);

    public NoteGroup CreateGroup(IEnumerable<Guid> ids)
    {
        var members = ids.Distinct().ToArray();
        if (members.Length < 2 || members.Any(id => !_notes.ContainsKey(id) || GroupOf(id) is not null))
            throw new InvalidOperationException("Группа должна содержать не менее двух свободных заметок.");
        string color;
        do { color = $"#{Random.Shared.Next(0x303030, 0xCFCFCF):X6}"; }
        while (_groups.Values.Any(g => g.BorderColor.Equals(color, StringComparison.OrdinalIgnoreCase)));
        var group = new NoteGroup { NoteIds = members, BorderColor = color };
        _groups.Add(group.Id, group);
        Changed?.Invoke();
        return group;
    }

    public void SetGroupMembers(Guid groupId, IEnumerable<Guid> ids)
    {
        var group = _groups[groupId];
        var members = ids.Distinct().ToArray();
        if (members.Any(id => !_notes.ContainsKey(id) || GroupOf(id) is { } other && other.Id != groupId))
            throw new InvalidOperationException("Заметка уже состоит в другой группе.");
        if (members.Length < 2) _groups.Remove(groupId);
        else _groups[groupId] = group with { NoteIds = members };
        Changed?.Invoke();
    }

    public void SetGroupColor(Guid groupId, string color)
    {
        if (!WorkspaceSnapshot.IsColor(color)) throw new ArgumentException("Ожидается цвет #RRGGBB.", nameof(color));
        _groups[groupId] = _groups[groupId] with { BorderColor = color.ToUpperInvariant() };
        Changed?.Invoke();
    }

    public NoteLink AddLink(Guid source, Guid target, NoteSide? sourceSide = null)
    {
        if (sourceSide is { } side && !Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(sourceSide));
        if (source == target || !_notes.ContainsKey(source) || !_notes.ContainsKey(target)) throw new InvalidOperationException("Выберите другую заметку.");
        var existing = _links.Values.FirstOrDefault(l => l.SourceId == source && l.TargetId == target);
        if (existing is not null) return existing;
        var link = new NoteLink { SourceId = source, TargetId = target, SourceSide = sourceSide };
        _links.Add(link.Id, link);
        Changed?.Invoke();
        return link;
    }

    public void RemoveLink(Guid id) { if (_links.Remove(id)) Changed?.Invoke(); }

    public void SetLinkStyle(Guid id, LinkStyle style)
    {
        var updated = _links[id] with { Style = style };
        (Snapshot with { Links = Links.Select(link => link.Id == id ? updated : link).ToArray() }).Validate();
        _links[id] = updated;
        Changed?.Invoke();
    }

    public bool MoveNotes(IReadOnlyDictionary<Guid, (double X, double Y)> positions)
    {
        if (positions.Keys.Any(id => _notes[id].IsPinned)) return false;
        var updates = positions.Select(p => _notes[p.Key] with { X = p.Value.X, Y = p.Value.Y }).ToArray();
        foreach (var note in updates) note.Validate();
        foreach (var note in updates) _notes[note.Id] = note;
        foreach (var note in updates) NoteUpdated?.Invoke(note);
        Changed?.Invoke();
        return true;
    }
}
