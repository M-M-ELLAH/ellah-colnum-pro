namespace EllahColNum.Core.Dimensions.Models;

/// <summary>
/// Output of ProDimensionEngine.BuildPlan.
/// Each LayerGroup carries the elements that belong to one dimension layer,
/// pre-sorted by position so the Helper can iterate directly.
/// </summary>
public class ProDimensionPlan
{
    public LayerGroup Grids    { get; set; } = new(ElementCategory.Grid);
    public LayerGroup Columns  { get; set; } = new(ElementCategory.Column);
    public LayerGroup Walls    { get; set; } = new(ElementCategory.Wall);
    public LayerGroup Openings { get; set; } = new(ElementCategory.Opening);

    /// <summary>All active layers, for convenient iteration.</summary>
    public IEnumerable<LayerGroup> ActiveLayers()
    {
        if (Grids.Elements.Count    >= 2) yield return Grids;
        if (Columns.Elements.Count  >= 2) yield return Columns;
        if (Walls.Elements.Count    >= 2) yield return Walls;
        if (Openings.Elements.Count >= 2) yield return Openings;
    }
}

/// <summary>
/// One dimension layer: a category, an offset, dimension type name,
/// and the sorted list of elements to dimension in each axis.
/// </summary>
public class LayerGroup
{
    public ElementCategory Category        { get; }
    public double          OffsetFeet      { get; set; }
    public string          DimTypeName     { get; set; } = "";

    /// <summary>All elements in this layer, sorted by X (for horizontal dim) or Y (for vertical dim).</summary>
    public List<ElementRefData> Elements   { get; set; } = new();

    /// <summary>Subset sorted by X — elements whose primary axis is N-S (used for horizontal dimension strings).</summary>
    public List<ElementRefData> SortedByX  { get; set; } = new();

    /// <summary>Subset sorted by Y — elements whose primary axis is E-W (used for vertical dimension strings).</summary>
    public List<ElementRefData> SortedByY  { get; set; } = new();

    public LayerGroup(ElementCategory category) => Category = category;
}
