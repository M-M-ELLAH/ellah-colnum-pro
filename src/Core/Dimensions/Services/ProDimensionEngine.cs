using EllahColNum.Core.Dimensions.Models;

namespace EllahColNum.Core.Dimensions.Services;

/// <summary>
/// Pure-logic engine for Pro Dimensions (Phase 2).
/// No Revit dependency — takes Revit-free ElementRefData objects and
/// sorts/groups them into a ProDimensionPlan ready for the Revit helper.
///
/// Separation principle:
///   The collector (Revit layer) owns all API calls and Reference extraction.
///   This engine owns only sorting and grouping logic.
/// </summary>
public class ProDimensionEngine
{
    /// <summary>
    /// Builds a ProDimensionPlan from raw element data + user options.
    /// Each LayerGroup is populated only if its corresponding toggle is on
    /// AND at least 2 elements are available (minimum for a dimension string).
    /// </summary>
    public ProDimensionPlan BuildPlan(
        IReadOnlyList<ElementRefData> elements,
        ProDimensionOptions           options)
    {
        var plan = new ProDimensionPlan();

        PopulateLayer(plan.Grids,    elements, ElementCategory.Grid,    options.DimGrids,    options.GridOffsetFeet,    options.GridDimTypeName);
        PopulateLayer(plan.Columns,  elements, ElementCategory.Column,  options.DimColumns,  options.ColumnOffsetFeet,  options.ColumnDimTypeName);
        // Beams are treated as columns for dimensioning purposes (same layer, same offset)
        PopulateLayerMerge(plan.Columns, elements, ElementCategory.Beam, options.DimColumns, options.ColumnOffsetFeet, options.ColumnDimTypeName);
        PopulateLayer(plan.Walls,    elements, ElementCategory.Wall,    options.DimWalls,    options.WallOffsetFeet,    options.WallDimTypeName);
        PopulateLayer(plan.Openings, elements, ElementCategory.Opening, options.DimOpenings, options.OpeningOffsetFeet, options.OpeningDimTypeName);

        return plan;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void PopulateLayer(
        LayerGroup                    layer,
        IReadOnlyList<ElementRefData> allElements,
        ElementCategory               category,
        bool                          enabled,
        double                        offsetFeet,
        string                        dimTypeName)
    {
        layer.OffsetFeet  = offsetFeet;
        layer.DimTypeName = dimTypeName;

        if (!enabled) return;

        var matching = allElements
            .Where(e => e.Category == category)
            .ToList();

        layer.Elements  = matching;

        // SortedByX: used to create the HORIZONTAL dimension string (measures N-S grid spacing).
        // We deduplicate by X position with a 0.1 ft tolerance to avoid zero-length segments.
        layer.SortedByX = DeduplicateByPosition(
            matching.OrderBy(e => e.X).ToList(),
            e => e.X);

        // SortedByY: used to create the VERTICAL dimension string.
        layer.SortedByY = DeduplicateByPosition(
            matching.OrderBy(e => e.Y).ToList(),
            e => e.Y);
    }

    /// <summary>
    /// Merges additional elements into an already-populated layer (used to add Beams to the Column layer).
    /// Only adds elements when the layer is enabled and not already set from a prior category.
    /// </summary>
    private static void PopulateLayerMerge(
        LayerGroup                    layer,
        IReadOnlyList<ElementRefData> allElements,
        ElementCategory               category,
        bool                          enabled,
        double                        offsetFeet,
        string                        dimTypeName)
    {
        if (!enabled) return;

        var extra = allElements.Where(e => e.Category == category).ToList();
        if (extra.Count == 0) return;

        layer.Elements = layer.Elements.Concat(extra).ToList();

        layer.SortedByX = DeduplicateByPosition(
            layer.Elements.OrderBy(e => e.X).ToList(), e => e.X);
        layer.SortedByY = DeduplicateByPosition(
            layer.Elements.OrderBy(e => e.Y).ToList(), e => e.Y);
    }
    /// Prevents zero-length dimension segments that would throw inside NewDimension.
    /// </summary>
    private static List<ElementRefData> DeduplicateByPosition(
        List<ElementRefData>         sorted,
        Func<ElementRefData, double> getPos)
    {
        const double MinSegmentFt = 0.1;
        if (sorted.Count == 0) return sorted;

        var result = new List<ElementRefData> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            if (Math.Abs(getPos(sorted[i]) - getPos(result[^1])) >= MinSegmentFt)
                result.Add(sorted[i]);
        }
        return result;
    }
}
