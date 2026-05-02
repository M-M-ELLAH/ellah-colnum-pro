namespace EllahColNum.Core.Models;

/// <summary>
/// All user-configurable options for the numbering operation.
/// Passed from the UI to the numbering engine.
/// </summary>
public class NumberingOptions
{
    /// <summary>
    /// How the mark is generated.
    /// Sequential: C-1, C-2, C-3
    /// GridBased:  A1, B2, C3  (uses Revit's Column Location Mark)
    /// </summary>
    public NumberingMode Mode { get; set; } = NumberingMode.Sequential;

    /// <summary>Text prefix for sequential mode (e.g. "C-" → "C-1", or "" for plain numbers)</summary>
    public string Prefix { get; set; } = "";

    /// <summary>First number in sequential mode (default 100)</summary>
    public int StartNumber { get; set; } = 100;

    /// <summary>
    /// Zero-pad numbers in sequential mode.
    /// E.g. true + PadLength 2 → "C-01" instead of "C-1"
    /// </summary>
    public bool PadWithZeros { get; set; } = false;

    /// <summary>Total digit length when PadWithZeros is true</summary>
    public int PadLength { get; set; } = 2;

    /// <summary>
    /// Tolerance in Revit feet for grouping columns at the SAME POSITION ACROSS FLOORS (vertical stacking).
    /// Default: 0.5 ft ≈ 15 cm — handles minor modeling imprecisions between floors.
    /// </summary>
    public double PositionToleranceFeet { get; set; } = 0.5;

    /// <summary>
    /// Tolerance in Revit feet for grouping columns into the SAME ROW (or column) on a single floor.
    /// Columns whose Y (or X for vertical sorts) values differ by less than this are treated as
    /// belonging to the same row/column band during sort-order calculation.
    /// Default: 1.64 ft ≈ 50 cm — reasonable for slightly off-grid structural placements.
    /// </summary>
    public double RowToleranceFeet { get; set; } = 1.64;

    /// <summary>
    /// How to handle columns that already have a Mark.
    /// </summary>
    public ContinuationMode ContinuationMode { get; set; } = ContinuationMode.SmartContinue;

    /// <summary>
    /// Overwrite existing marks. If false, columns that already have a Mark are skipped.
    /// Only relevant when ContinuationMode = Override.
    /// </summary>
    public bool OverwriteExisting { get; set; } = true;

    /// <summary>
    /// Sorting direction for sequential mode.
    /// TopLeftToRight: north-west corner first, left→right, row by row downward (default — Israeli convention)
    /// LeftToRight: sorts by X then Y (West→East, South→North)
    /// BottomToTop: sorts by Y then X
    /// </summary>
    public SortDirection SortBy { get; set; } = SortDirection.TopLeftToRight;

    /// <summary>
    /// Text appended after the number in sequential mode.
    /// E.g. Prefix="C-", Number=1, Suffix="-S" → "C-1-S"
    /// </summary>
    public string Suffix { get; set; } = "";

    /// <summary>
    /// Level/floor name whose column XY positions define the sort order.
    /// Empty string = use all columns' positions (existing behavior).
    /// When set, sort positions are anchored to that floor's columns; groups
    /// without a column at that floor fall back to their average position.
    /// </summary>
    public string ReferenceFloorName { get; set; } = "";

    /// <summary>
    /// Elevation of <see cref="ReferenceFloorName"/> in Revit internal feet.
    /// Required so the engine can recognise multi-story columns that pass
    /// THROUGH the reference floor (base below, top above) as belonging to it —
    /// the same way Revit's plan view of that floor would show them.
    ///
    /// 0 disables elevation-based detection and the engine falls back to
    /// pure BaseLevelName matching (legacy behaviour, kept for backwards
    /// compatibility with existing unit tests).
    /// </summary>
    public double ReferenceFloorElevation { get; set; } = 0;

    /// <summary>
    /// Whitelist of family names to include in numbering.
    /// Empty list = include all families (existing behavior).
    /// Example: ["M_Concrete-Rectangular-Column", "M_Concrete-Round-Column"]
    /// </summary>
    public List<string> FamilyFilter { get; set; } = [];

    /// <summary>
    /// Name of the Revit text parameter to write the mark into.
    /// Empty string = default built-in MARK parameter (ALL_MODEL_MARK).
    /// </summary>
    public string TargetParameterName { get; set; } = "";

    /// <summary>
    /// Revit element category to collect and number.
    /// "Structural Columns" = OST_StructuralColumns (default)
    /// "Architectural Columns" = OST_Columns
    /// </summary>
    public string TargetCategoryName { get; set; } = "Structural Columns";

    /// <summary>
    /// When set, this element's group is rotated to position 0 in the sorted list
    /// so it receives the start number. Used by the "Choose Your Way" feature.
    /// </summary>
    public long? StartAnchorElementId { get; set; } = null;

    /// <summary>
    /// When set, only columns whose BaseLevelName matches this floor are collected
    /// and numbered.  Null or empty = Full Project (all floors — default behaviour).
    /// </summary>
    public string? SpecificFloorName { get; set; } = null;

    /// <summary>
    /// Per-column rotation override (degrees, key = ElementId).  Lets the Revit
    /// layer pass a DIFFERENT rotation per column for buildings whose grid
    /// system is split across multiple orientations (e.g. a tilted core plus
    /// an orthogonal podium).  When set, the engine picks each group's
    /// rotation by looking up the first contained column's ElementId; a
    /// missing key falls back to <see cref="BuildingRotationDegrees"/>.
    ///
    /// Set together with <see cref="ColumnZoneByElementId"/> so the engine can
    /// also keep columns from different zones from clustering into the same
    /// row/column even when their rotated coordinates happen to collide.
    /// </summary>
    public Dictionary<long, double>? ColumnRotationByElementId { get; set; } = null;

    /// <summary>
    /// Per-column zone id (key = ElementId).  The engine treats any two
    /// columns with different zone ids as ALWAYS belonging to different
    /// row/column clusters, regardless of how close their sort coordinates
    /// might be.  This is what stops the orthogonal podium and a tilted core
    /// from accidentally sharing a "row" just because their rotated Y values
    /// land within tolerance of each other.
    ///
    /// When null or empty every column shares zone 0 and the engine behaves
    /// exactly as it did before multi-zone support was added.
    /// </summary>
    public Dictionary<long, int>? ColumnZoneByElementId { get; set; } = null;

    /// <summary>
    /// Rotation of the building's primary axis relative to project east, in
    /// degrees, normalised to [-45°, +45°].  When non-zero the engine performs
    /// row/column clustering and ordering in the building's own frame so that
    /// non-orthogonal grids are numbered the way an engineer reads the plan.
    ///
    /// Conventions:
    ///   •  0 (default) → "no rotation" — the engine behaves exactly like the
    ///                    legacy code path.  Always used for orthogonal projects.
    ///   •  Non-zero    → rotate every column position by  −BuildingRotationDegrees
    ///                    before clustering / sorting.  Real-world XY (and therefore
    ///                    vertical-stacking / Revit writes) is left untouched.
    ///
    /// Populated automatically by the Revit layer (grid-based detection with PCA
    /// fallback).  Values whose magnitude is below ~1° are treated as zero by the
    /// dialog so orthogonal buildings pay zero algorithmic cost.
    /// </summary>
    public double BuildingRotationDegrees { get; set; } = 0;

    /// <summary>
    /// Elevation of <see cref="SpecificFloorName"/> in Revit internal feet.
    /// Carried end-to-end from the dialog so the command does NOT need to
    /// re-resolve the floor's elevation through dictionary lookups (which
    /// can fail for Hebrew level names due to BiDi-marker mismatches).
    /// 0 = elevation-based filtering disabled in the command (legacy path).
    /// </summary>
    public double SpecificFloorElevation { get; set; } = 0;
}

public enum NumberingMode
{
    /// <summary>C-1, C-2, C-3 — simple sequential</summary>
    Sequential,

    /// <summary>A1, B2, C3 — based on Revit grid intersection</summary>
    GridBased
}

public enum SortDirection
{
    /// <summary>Sort by X ascending then Y ascending (West→East, row by row South→North)</summary>
    LeftToRight,

    /// <summary>Sort by X descending then Y descending (East→West, row by row North→South)</summary>
    RightToLeft,

    /// <summary>Sort by Y ascending then X ascending (South→North, column by column West→East)</summary>
    BottomToTop,

    /// <summary>Sort by X ascending then Y descending (West→East column by column, North→South)</summary>
    TopToBottom,

    /// <summary>
    /// Top-left to right, row by row downward.
    /// Sorts by Y descending (north→south) then X ascending (west→east).
    /// Matches the Israeli structural engineering convention:
    /// start from north-west corner, go right, then next row down.
    /// </summary>
    TopLeftToRight,

    /// <summary>Row by row, moving UP between rows, right to left within each row</summary>
    RightToLeftUp,

    /// <summary>Column by column, moving LEFT between columns, top to bottom within each column</summary>
    TopBottomLeft,

    /// <summary>Column by column, moving LEFT between columns, bottom to top within each column</summary>
    BottomTopLeft,
}

public enum ContinuationMode
{
    /// <summary>
    /// Default: detect existing pattern and continue from the highest number.
    /// Fully-numbered groups keep their mark. Partially-numbered groups get completed.
    /// New groups get the next available number.
    /// </summary>
    SmartContinue,

    /// <summary>
    /// Renumber everything from scratch, ignoring any existing marks.
    /// </summary>
    Override,

    /// <summary>
    /// Only number groups that have NO mark at all. Skip everything else.
    /// </summary>
    AddOnly,
}
