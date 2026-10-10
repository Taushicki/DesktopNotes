namespace DesktopNotes.Application;

// Draft membership is separate from persisted groups until a non-Ctrl click commits it.
public sealed class GroupSelectionSession(NoteService notes)
{
    private readonly HashSet<Guid> _members = [];
    public Guid? TargetGroupId { get; private set; }
    public bool IsActive { get; private set; }
    public IReadOnlySet<Guid> Members => _members;
    public event Action? StateChanged;

    public string? Toggle(Guid noteId)
    {
        if (!notes.Contains(noteId)) return null;
        var group = notes.GroupOf(noteId);
        if (!IsActive)
        {
            IsActive = true;
            TargetGroupId = group?.Id;
            if (group is not null) _members.UnionWith(group.NoteIds);
            else _members.Add(noteId);
        }
        else if (group is not null && group.Id != TargetGroupId)
        {
            if (TargetGroupId is not null) return "Эта заметка уже состоит в другой группе. Сначала исключите её из той группы.";
            TargetGroupId = group.Id;
            _members.UnionWith(group.NoteIds);
        }
        else if (!_members.Add(noteId)) _members.Remove(noteId);
        StateChanged?.Invoke();
        return null;
    }

    public void Commit()
    {
        if (!IsActive) return;
        _members.RemoveWhere(id => !notes.Contains(id));
        if (TargetGroupId is { } id && notes.Groups.Any(g => g.Id == id)) notes.SetGroupMembers(id, _members);
        else
        {
            _members.RemoveWhere(member => notes.GroupOf(member) is not null);
            if (_members.Count >= 2) notes.CreateGroup(_members);
        }
        Clear();
    }

    public void Cancel() { if (IsActive) Clear(); }
    private void Clear()
    {
        IsActive = false;
        TargetGroupId = null;
        _members.Clear();
        StateChanged?.Invoke();
    }
}
