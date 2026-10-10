using DesktopNotes.Domain;

namespace DesktopNotes.Application;

public readonly record struct DiagramPoint(double X, double Y);

public static class ArrowRouter
{
    public static NoteSide NearestSide(NoteBounds bounds, DiagramPoint point) => Enum.GetValues<NoteSide>()
        .MinBy(side => DistanceSquared(Anchor(bounds, side), point));

    private static double DistanceSquared(DiagramPoint a, DiagramPoint b) => Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2);
    private static bool Horizontal(NoteSide side) => side is NoteSide.Left or NoteSide.Right;

    public static DiagramPoint Anchor(NoteBounds bounds, NoteSide side) => side switch
    {
        NoteSide.Left => new(bounds.X, bounds.Y + bounds.Height / 2),
        NoteSide.Top => new(bounds.X + bounds.Width / 2, bounds.Y),
        NoteSide.Right => new(bounds.Right, bounds.Y + bounds.Height / 2),
        NoteSide.Bottom => new(bounds.X + bounds.Width / 2, bounds.Bottom),
        _ => throw new ArgumentOutOfRangeException(nameof(side))
    };

    private static DiagramPoint Outward(DiagramPoint start, NoteSide side, double distance) => side switch
    {
        NoteSide.Left => start with { X = start.X - distance },
        NoteSide.Top => start with { Y = start.Y - distance },
        NoteSide.Right => start with { X = start.X + distance },
        _ => start with { Y = start.Y + distance }
    };

    public static IReadOnlyList<DiagramPoint> FromSide(NoteBounds source, DiagramPoint end, NoteSide side, double clearance = 32)
    {
        var start = Anchor(source, side);
        var outward = Outward(start, side, clearance);
        var forward = side switch
        {
            NoteSide.Left => end.X < start.X,
            NoteSide.Top => end.Y < start.Y,
            NoteSide.Right => end.X > start.X,
            _ => end.Y > start.Y
        };
        if (forward)
        {
            if (Horizontal(side))
            {
                var middle = (start.X + end.X) / 2;
                return Clean([start, new(middle, start.Y), new(middle, end.Y), end]);
            }
            var middleY = (start.Y + end.Y) / 2;
            return Clean([start, new(start.X, middleY), new(end.X, middleY), end]);
        }
        if (Horizontal(side))
        {
            var y = end.Y < start.Y ? source.Y - clearance : source.Bottom + clearance;
            return Clean([start, outward, new(outward.X, y), new(end.X, y), end]);
        }
        var x = end.X < start.X ? source.X - clearance : source.Right + clearance;
        return Clean([start, outward, new(x, outward.Y), new(x, end.Y), end]);
    }

    public static IReadOnlyList<DiagramPoint> Route(NoteBounds source, NoteBounds target, NoteSide? sourceSide = null, double clearance = 32)
    {
        if (sourceSide is { } fixedSide)
        {
            var end = Anchor(target, NearestSide(target, Anchor(source, fixedSide)));
            return FromSide(source, end, fixedSide, clearance);
        }
        var pairs = from a in Enum.GetValues<NoteSide>() from b in Enum.GetValues<NoteSide>()
                    select (SourceSide: a, TargetSide: b, Start: Anchor(source, a), End: Anchor(target, b));
        var pair = pairs.MinBy(pair => DistanceSquared(pair.Start, pair.End));
        var start = pair.Start; var finish = pair.End;
        var overlapping = source.X < target.Right && source.Right > target.X && source.Y < target.Bottom && source.Bottom > target.Y;
        // Touching endpoints need an outside route rather than an invisible zero-length line.
        if (overlapping || DistanceSquared(start, finish) < 400)
        {
            if (Math.Abs(target.X - source.X) >= Math.Abs(target.Y - source.Y))
            {
                var a = Anchor(source, NoteSide.Top); var b = Anchor(target, NoteSide.Top);
                var outside = Math.Min(source.Y, target.Y) - clearance;
                return Clean([a, new(a.X, outside), new(b.X, outside), b]);
            }
            var right = Math.Max(source.Right, target.Right) + clearance;
            var first = Anchor(source, NoteSide.Right); var last = Anchor(target, NoteSide.Right);
            return Clean([first, new(right, first.Y), new(right, last.Y), last]);
        }
        if (Horizontal(pair.SourceSide) && Horizontal(pair.TargetSide))
        {
            var middle = pair.SourceSide == pair.TargetSide
                ? pair.SourceSide == NoteSide.Right ? Math.Max(source.Right, target.Right) + clearance : Math.Min(source.X, target.X) - clearance
                : (start.X + finish.X) / 2;
            return Clean([start, new(middle, start.Y), new(middle, finish.Y), finish]);
        }
        if (!Horizontal(pair.SourceSide) && !Horizontal(pair.TargetSide))
        {
            var middle = pair.SourceSide == pair.TargetSide
                ? pair.SourceSide == NoteSide.Bottom ? Math.Max(source.Bottom, target.Bottom) + clearance : Math.Min(source.Y, target.Y) - clearance
                : (start.Y + finish.Y) / 2;
            return Clean([start, new(start.X, middle), new(finish.X, middle), finish]);
        }
        var corner = Horizontal(pair.SourceSide) ? new DiagramPoint(finish.X, start.Y) : new(start.X, finish.Y);
        return Clean([start, corner, finish]);
    }

    private static IReadOnlyList<DiagramPoint> Clean(IEnumerable<DiagramPoint> points)
    {
        var result = new List<DiagramPoint>();
        foreach (var point in points)
        {
            if (result.Count > 0 && result[^1] == point) continue;
            while (result.Count >= 2)
            {
                var a = result[^2]; var b = result[^1];
                var sameX = a.X == b.X && b.X == point.X;
                var sameY = a.Y == b.Y && b.Y == point.Y;
                if (!(sameX || sameY) || (b.X - a.X) * (point.X - b.X) + (b.Y - a.Y) * (point.Y - b.Y) < 0) break;
                result.RemoveAt(result.Count - 1);
            }
            result.Add(point);
        }
        return result;
    }

    public static DiagramPoint Boundary(NoteBounds bounds, DiagramPoint center, double dx, double dy)
    {
        if (dx == 0 && dy == 0) return new(bounds.Right, center.Y);
        var factor = Math.Min(dx == 0 ? double.PositiveInfinity : bounds.Width / 2 / Math.Abs(dx),
            dy == 0 ? double.PositiveInfinity : bounds.Height / 2 / Math.Abs(dy));
        return new(center.X + dx * factor, center.Y + dy * factor);
    }
}
