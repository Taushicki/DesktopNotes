namespace DesktopNotes.Application;

// Selection is transient. Deleting a selected connection never deletes its notes.
public sealed class LinkSelectionSession(NoteService notes)
{
    public Guid? SelectedId { get; private set; }
    public event Action? Changed;

    public void Select(Guid id)
    {
        if (!notes.Links.Any(link => link.Id == id)) return;
        if (SelectedId == id) return;
        SelectedId = id;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (SelectedId is null) return;
        SelectedId = null;
        Changed?.Invoke();
    }

    public bool DeleteSelected()
    {
        if (SelectedId is not { } id) return false;
        Clear();
        notes.RemoveLink(id);
        return true;
    }
}
