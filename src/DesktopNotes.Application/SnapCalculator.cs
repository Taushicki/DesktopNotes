namespace DesktopNotes.Application;

// Coordinates may be DIPs or physical pixels, as long as all rectangles and the threshold share a unit.
public readonly record struct NoteBounds(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public NoteBounds Offset(double x, double y) => this with { X = X + x, Y = Y + y };
}

public static class SnapCalculator
{
    public const double DefaultDistance = 3;
    private const double ExactDistance = 1;
    private const double AttractionStrength = 0.5;

    // During dragging, gently approach the edge; lock exactly only within one DIP.
    // No retained snap state: leaving the radius immediately releases the note.
    public static (double X, double Y) GetDragOffset(IReadOnlyList<NoteBounds> moving, IReadOnlyList<NoteBounds> targets, double scale = 1)
    {
        var offset = GetOffset(moving, targets, DefaultDistance * scale);
        return (Soften(offset.X), Soften(offset.Y));

        double Soften(double value) => Math.Abs(value) <= ExactDistance * scale ? value : value * AttractionStrength;
    }

    public static (double X, double Y) GetOffset(IReadOnlyList<NoteBounds> moving, IReadOnlyList<NoteBounds> targets, double distance = DefaultDistance)
    {
        SnapCandidate? bestX = null, bestY = null;
        foreach (var source in moving)
        foreach (var target in targets)
        {
            // Include small gaps between perpendicular ranges so diagonal corners can meet.
            if (Math.Max(source.Y, target.Y) - Math.Min(source.Bottom, target.Bottom) <= distance)
            {
                var alignment = NearestAlignment(target.Y - source.Y, target.Bottom - source.Bottom);
                Choose(target.X - source.Right, alignment, ref bestX);
                Choose(target.Right - source.X, alignment, ref bestX);
            }
            if (Math.Max(source.X, target.X) - Math.Min(source.Right, target.Right) <= distance)
            {
                var alignment = NearestAlignment(target.X - source.X, target.Right - source.Right);
                Choose(target.Y - source.Bottom, alignment, ref bestY);
                Choose(target.Bottom - source.Y, alignment, ref bestY);
            }
        }
        // A corner alignment belongs to an adjacent edge, never to a distant note.
        // Actual adjacent-edge snaps take priority when both axes have candidates.
        return (bestX?.Offset ?? bestY?.Alignment ?? 0, bestY?.Offset ?? bestX?.Alignment ?? 0);

        double? NearestAlignment(double first, double second)
        {
            var nearest = Math.Abs(first) <= Math.Abs(second) ? first : second;
            return Math.Abs(nearest) <= distance ? nearest : null;
        }

        void Choose(double offset, double? alignment, ref SnapCandidate? best)
        {
            if (Math.Abs(offset) > distance) return;
            if (best is null || Math.Abs(offset) < Math.Abs(best.Value.Offset) ||
                (Math.Abs(offset) == Math.Abs(best.Value.Offset) &&
                 Math.Abs(alignment ?? double.PositiveInfinity) < Math.Abs(best.Value.Alignment ?? double.PositiveInfinity)))
                best = new(offset, alignment);
        }
    }

    private readonly record struct SnapCandidate(double Offset, double? Alignment);
}
