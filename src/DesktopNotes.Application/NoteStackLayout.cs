using DesktopNotes.Domain;

namespace DesktopNotes.Application;

// Adjacency is evaluated before resizing, so opening a note cannot accidentally
// attach unrelated notes merely because its new bounds overlap them.
public static class NoteStackLayout
{
    // Exact snapping and physical-pixel rounding can leave a sub-DIP gap.
    public const double ContactTolerance = 1;

    public static double VisibleHeight(Note note) => note.IsCollapsed ? Note.CollapsedHeight : note.Height;

    public static IReadOnlyList<Note> AttachedBelow(Note source, IReadOnlyList<Note> notes)
    {
        if (source.IsHidden) return [];
        var remaining = notes.Where(note => note.Id != source.Id && !note.IsHidden).ToList();
        var result = new List<Note>();
        var pending = new Queue<Note>();
        pending.Enqueue(source);
        while (pending.TryDequeue(out var upper))
        {
            var bottom = upper.Y + VisibleHeight(upper);
            for (var index = remaining.Count - 1; index >= 0; index--)
            {
                var lower = remaining[index];
                if (lower.Y <= upper.Y || Math.Abs(lower.Y - bottom) > ContactTolerance ||
                    Math.Min(upper.X + upper.Width, lower.X + lower.Width) <= Math.Max(upper.X, lower.X)) continue;
                remaining.RemoveAt(index);
                result.Add(lower);
                pending.Enqueue(lower);
            }
        }
        return result;
    }
}
