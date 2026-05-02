namespace EllahColNum.Core.Geometry;

/// <summary>
/// Plain 2-D point used by the geometry helpers.  Defined here so the Core
/// project stays free of any Revit type — every Revit-side coordinate is
/// converted into <see cref="Point2D"/> at the boundary.
/// </summary>
public readonly record struct Point2D(double X, double Y)
{
    /// <summary>Vector subtraction: returns a vector pointing from <paramref name="b"/> to this point.</summary>
    public Point2D Minus(Point2D b) => new(X - b.X, Y - b.Y);

    /// <summary>Squared distance — handy for nearest-neighbour searches without a sqrt.</summary>
    public double DistanceSquaredTo(Point2D other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return dx * dx + dy * dy;
    }

    public double DistanceTo(Point2D other) => Math.Sqrt(DistanceSquaredTo(other));
}

/// <summary>
/// A 2-D infinite line described by an origin point and a unit direction
/// vector.  We use it to represent Revit Grid lines once they have been
/// projected onto the XY plane.  Stored as origin + direction (rather than
/// two endpoints) so distance queries are O(1).
/// </summary>
public readonly record struct GridLine2D(Point2D Origin, Point2D Direction)
{
    /// <summary>Perpendicular distance from <paramref name="p"/> to this line.</summary>
    public double DistanceTo(Point2D p)
    {
        // For a unit direction d and a vector v from origin to p, the perpendicular
        // distance is |v × d|.  We do not enforce d being unit-length here so the
        // caller can pass either; we normalise on the fly to keep the helper safe.
        double vx = p.X - Origin.X;
        double vy = p.Y - Origin.Y;
        double dx = Direction.X;
        double dy = Direction.Y;
        double dlen = Math.Sqrt(dx * dx + dy * dy);
        if (dlen < 1e-12) return Math.Sqrt(vx * vx + vy * vy);
        return Math.Abs(vx * dy - vy * dx) / dlen;
    }

    /// <summary>
    /// Angle of this line's direction in degrees, folded into <c>[0°, 90°)</c>.
    /// Two perpendicular lines (e.g. row at 5° and col at 95°) collapse to the
    /// same value, making this the natural cluster key for grouping a building's
    /// grid lines by axis-system.
    /// </summary>
    public double FoldedAngleDegrees()
    {
        double deg = Math.Atan2(Direction.Y, Direction.X) * 180.0 / Math.PI;
        return ((deg % 90.0) + 90.0) % 90.0;
    }

    /// <summary>
    /// Un-folded angle in <c>[0°, 180°)</c>.  Used to distinguish "row" vs
    /// "column" grids inside a single orientation cluster — two grids in the
    /// same cluster are perpendicular if their un-folded angles differ by ≈ 90°.
    /// </summary>
    public double UnfoldedAngleDegrees()
    {
        double deg = Math.Atan2(Direction.Y, Direction.X) * 180.0 / Math.PI;
        return ((deg % 180.0) + 180.0) % 180.0;
    }
}
