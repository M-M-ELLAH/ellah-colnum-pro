using Autodesk.Revit.DB;
using EllahColNum.Core.Geometry;

namespace EllahColNum.Revit.Helpers;

/// <summary>
/// Extracts every linear Revit Grid as a Core <see cref="GridLine2D"/>.
///
/// Lives here (in the Revit project) because it depends on RevitAPI, but
/// produces only Core types so the rest of the pipeline — clustering,
/// per-column zone scoring, sort key computation — runs entirely on pure
/// C# types and stays unit-testable.
///
/// Curved or arc grids are skipped: they have no single direction and
/// therefore don't fit the orientation-cluster model.  Vertical grids
/// (rare; usually 3-D / section grids) are also skipped because their
/// projection onto the XY plane has zero magnitude.
/// </summary>
public static class RevitGridReader
{
    /// <summary>Returns every linear Grid in <paramref name="doc"/> as a Core line.</summary>
    public static List<GridLine2D> ReadGridLines(Document doc)
    {
        var lines = new List<GridLine2D>();
        if (doc == null) return lines;

        var collector = new FilteredElementCollector(doc).OfClass(typeof(Grid));
        foreach (Grid g in collector)
        {
            if (g.Curve is not Line line) continue;

            var origin = line.Origin;
            var dir    = line.Direction;
            double horizMag = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            if (horizMag < 1e-9) continue;

            // Project onto XY and normalise the direction.  The Core helpers
            // already handle non-unit directions but normalising up-front
            // keeps later math allocation-free.
            lines.Add(new GridLine2D(
                Origin:    new Point2D(origin.X, origin.Y),
                Direction: new Point2D(dir.X / horizMag, dir.Y / horizMag)));
        }

        return lines;
    }
}
