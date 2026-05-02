namespace EllahColNum.Core.Dimensions.Models;

/// <summary>
/// User-configured options for the Pro Dimensions command (Phase 2).
/// One instance is built by ProDimensionDialog and consumed by
/// ProDimensionEngine → ProDimensionHelper.
/// </summary>
public class ProDimensionOptions
{
    // ── View selection ────────────────────────────────────────────────────────

    /// <summary>Revit ElementId.Value for every selected ViewPlan.</summary>
    public List<long> SelectedViewIds { get; set; } = new();

    // ── Layer toggles ─────────────────────────────────────────────────────────

    public bool DimGrids    { get; set; } = true;
    public bool DimColumns  { get; set; } = true;
    public bool DimWalls    { get; set; } = true;
    public bool DimOpenings { get; set; } = true;

    // ── Per-layer DimensionType names (empty string = Revit project default) ──

    public string GridDimTypeName    { get; set; } = "";
    public string ColumnDimTypeName  { get; set; } = "";
    public string WallDimTypeName    { get; set; } = "";
    public string OpeningDimTypeName { get; set; } = "";

    // ── Per-layer offsets from the building's outermost extents (feet) ────────
    // Grids sit furthest out, openings sit closest in.

    /// <summary>~3 m — outermost row.</summary>
    public double GridOffsetFeet    { get; set; } = 9.843;

    /// <summary>~2 m</summary>
    public double ColumnOffsetFeet  { get; set; } = 6.562;

    /// <summary>~1 m</summary>
    public double WallOffsetFeet    { get; set; } = 3.281;

    /// <summary>~0.5 m — innermost row.</summary>
    public double OpeningOffsetFeet { get; set; } = 1.640;
}
