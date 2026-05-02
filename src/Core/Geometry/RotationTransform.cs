namespace EllahColNum.Core.Geometry;

/// <summary>
/// Tiny helper for rotating 2-D points around the project origin.
///
/// The numbering engine uses this to express column positions in the
/// building's own frame: clustering and ordering happen in rotated
/// coordinates whenever the project's grid is tilted relative to project
/// north.  Pure rotation around the origin is sufficient — it preserves
/// distances and the relative ordering of values along any direction, so
/// no translation is needed.
/// </summary>
public static class RotationTransform
{
    /// <summary>
    /// Threshold below which a rotation angle is treated as "no rotation".
    /// Matches the orthogonality tolerance used by the engine when deciding
    /// whether to enter rotated-coordinate mode.
    /// </summary>
    public const double NegligibleAngleDegrees = 1e-3;

    /// <summary>
    /// Rotate <paramref name="x"/>, <paramref name="y"/> by
    /// <paramref name="angleDegrees"/> (counter-clockwise).  When the angle is
    /// effectively zero the inputs are returned unchanged so the hot path stays
    /// allocation-free for orthogonal projects.
    /// </summary>
    public static (double X, double Y) Rotate(double x, double y, double angleDegrees)
    {
        if (Math.Abs(angleDegrees) < NegligibleAngleDegrees)
            return (x, y);

        double rad = angleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        return (x * cos - y * sin, x * sin + y * cos);
    }
}
