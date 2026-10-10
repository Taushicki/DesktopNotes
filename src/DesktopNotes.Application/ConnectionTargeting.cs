namespace DesktopNotes.Application;

public static class ConnectionTargeting
{
    public static double Distance(NoteBounds bounds, DiagramPoint cursor)
    {
        var dx = Math.Max(0, Math.Max(bounds.X - cursor.X, cursor.X - bounds.Right));
        var dy = Math.Max(0, Math.Max(bounds.Y - cursor.Y, cursor.Y - bounds.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static Guid? FindClosest(IEnumerable<(Guid Id, NoteBounds Bounds)> candidates, DiagramPoint cursor, double radius = 18)
    {
        var closest = candidates.Select(candidate => (candidate.Id, Distance: Distance(candidate.Bounds, cursor)))
            .Where(candidate => candidate.Distance <= radius).OrderBy(candidate => candidate.Distance).ToArray();
        return closest.Length == 0 ? null : closest[0].Id;
    }
}
