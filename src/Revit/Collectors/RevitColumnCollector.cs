using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using EllahColNum.Core.Interfaces;
using EllahColNum.Core.Models;
using EllahColNum.Revit.Helpers;

namespace EllahColNum.Revit.Collectors;

/// <summary>
/// Reads structural and architectural columns from the active Revit document.
/// Implements IColumnReader so Core logic never touches Revit API directly.
/// </summary>
public class RevitColumnCollector : IColumnReader
{
    private readonly Document _doc;

    public RevitColumnCollector(Document doc)
    {
        _doc = doc;
    }

    // ── Column collection ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns column instances for the given category, optionally filtered by family names.
    /// Pass an empty/null familyFilter to return all families.
    /// categoryName: "Structural Columns" (default) or "Architectural Columns"
    /// Supports both regular FamilyInstance columns (LocationPoint) and
    /// Model In-Place columns (position derived from BoundingBox centre).
    /// </summary>
    public List<ColumnData> ReadAllColumns(
        IReadOnlyList<string>? familyFilter  = null,
        string?                categoryName  = null)
    {
        var results   = new List<ColumnData>();
        var category  = CategoryNameToBuiltIn(categoryName ?? "Structural Columns");
        var allLevels = LevelsByElevation(); // cached once for BoundingBox-based level matching

        var elements = new FilteredElementCollector(_doc)
            .OfCategory(category)
            .OfClass(typeof(FamilyInstance))
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>();

        foreach (var fi in elements)
        {
            // ── Detect Model In-Place elements ────────────────────────────
            // In-place families have Family.IsInPlace == true.
            // Their StructuralType is often UnknownFraming or NonStructural,
            // so we must skip the StructuralType filter for them.
            // We detect them BEFORE choosing the position strategy.
            bool isModelInPlace = false;
            try { isModelInPlace = fi.Symbol?.Family?.IsInPlace == true; }
            catch { /* some proxy elements may throw — treat as regular */ }

            // ── Determine position ────────────────────────────────────────
            // For regular columns: use LocationPoint (centre of insertion).
            // For Model In-Place: ALWAYS use BoundingBox centre, because
            // LocationPoint on an in-place FamilyInstance often returns the
            // family's local origin (0,0,0 in project space) rather than the
            // actual visual position of the geometry.
            XYZ? position      = null;
            BoundingBoxXYZ? bb = null;

            if (isModelInPlace)
            {
                bb = fi.get_BoundingBox(null);
                if (bb != null)
                    position = new XYZ(
                        (bb.Min.X + bb.Max.X) / 2.0,
                        (bb.Min.Y + bb.Max.Y) / 2.0,
                        bb.Min.Z);
                // Fallback to LocationPoint if BoundingBox unavailable
                if (position == null && fi.Location is LocationPoint lpInPlace)
                    position = lpInPlace.Point;
            }
            else if (fi.Location is LocationPoint lp)
            {
                position = lp.Point;
            }
            else
            {
                // Non-in-place with no LocationPoint: try BoundingBox
                bb = fi.get_BoundingBox(null);
                if (bb != null)
                {
                    position = new XYZ(
                        (bb.Min.X + bb.Max.X) / 2.0,
                        (bb.Min.Y + bb.Max.Y) / 2.0,
                        bb.Min.Z);
                    isModelInPlace = true;
                }
            }

            if (position == null) continue;

            // ── StructuralType filter ─────────────────────────────────────
            // Apply only to regular (non-in-place) structural columns.
            // This excludes SC_Reference Column and other placeholder families
            // while allowing genuine in-place structural columns through.
            if (category == BuiltInCategory.OST_StructuralColumns && !isModelInPlace)
            {
                if (fi.StructuralType != StructuralType.Column) continue;
                if (fi.Symbol == null) continue;
            }

            // ── Base level ────────────────────────────────────────────────
            // Try the standard parameter first; fall back to the nearest level
            // by Z elevation for Model In-Place elements.
            var baseLevel = GetLevel(fi, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)
                            ?? (isModelInPlace ? FindNearestLevel(position.Z, allLevels) : null);
            if (baseLevel == null) continue;

            // ── Top level ─────────────────────────────────────────────────
            var topLevel = GetLevel(fi, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM);
            if (topLevel == null && isModelInPlace)
            {
                // Use the level nearest to the top of the bounding box.
                var topZ = bb?.Max.Z ?? position.Z;
                topLevel = FindNearestLevel(topZ, allLevels) ?? baseLevel;
            }

            // Regular structural columns must have a top level
            // (SC_Reference Column / phantom families typically don't).
            if (category == BuiltInCategory.OST_StructuralColumns && !isModelInPlace && topLevel == null)
                continue;

            // ── Family filter ─────────────────────────────────────────────
            var familyName = fi.Symbol?.FamilyName ?? "(In-Place)";
            if (familyFilter is { Count: > 0 } &&
                !familyFilter.Contains(familyName, StringComparer.OrdinalIgnoreCase))
                continue;

            results.Add(new ColumnData
            {
                ElementId          = fi.Id.Value,
                X                  = position.X,
                Y                  = position.Y,
                BaseLevelName      = baseLevel.Name,
                TopLevelName       = topLevel?.Name  ?? baseLevel.Name,
                BaseLevelElevation = baseLevel.Elevation,
                TopLevelElevation  = topLevel?.Elevation ?? baseLevel.Elevation,
                BaseOffset         = fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM)?.AsDouble() ?? 0,
                TopOffset          = fi.get_Parameter(BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)?.AsDouble()  ?? 0,
                FamilyName         = familyName,
                TypeName           = fi.Symbol?.Name ?? "",
                StructuralMaterial = fi.get_Parameter(BuiltInParameter.STRUCTURAL_MATERIAL_PARAM)?.AsValueString() ?? "",
                ColumnLocationMark = fi.get_Parameter(BuiltInParameter.COLUMN_LOCATION_MARK)?.AsString() ?? "",
                CurrentMark        = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "",
                MovesWithGrids     = fi.LookupParameter("Moves With Grids")?.AsInteger() == 1,
            });
        }

        return results;
    }

    /// <summary>
    /// Returns the level whose elevation is closest to <paramref name="z"/>.
    /// Used for Model In-Place elements that have no standard level parameters.
    /// </summary>
    private static Level? FindNearestLevel(double z, List<Level> levels)
    {
        if (levels.Count == 0) return null;
        return levels.MinBy(l => Math.Abs(l.Elevation - z));
    }

    /// <summary>IColumnReader — delegates to ReadAllColumns with no filters.</summary>
    List<ColumnData> IColumnReader.ReadAllColumns() => ReadAllColumns();

    // ── Level / discipline metadata ───────────────────────────────────────────

    /// <summary>
    /// Returns all project level names sorted by elevation (lowest first).
    /// </summary>
    public List<string> ReadLevelNames()
    {
        return LevelsByElevation().Select(l => l.Name).ToList();
    }

    /// <summary>
    /// Returns the set of structural column ElementIds that Revit considers VISIBLE in the
    /// best available floor-plan view for <paramref name="levelName"/>.
    ///
    /// Called lazily (only when the user selects a specific floor) so that plugin startup
    /// remains fast.  Results are cached by the caller.
    ///
    /// Delegates the actual lookup to <see cref="FloorPlanViewLocator"/>, which handles
    /// Bidi/Hebrew name normalisation, Level.ElementId matching, discipline priority,
    /// and view-template visibility verification.  When <paramref name="levelElevation"/>
    /// is positive it is used as an encoding-safe fallback for matching the Level.
    ///
    /// Returns an empty set when no suitable view yields any column — the caller is then
    /// expected to fall back to its elevation/span-through filter.
    /// </summary>
    public HashSet<long> ReadColumnIdsForFloor(string levelName, double levelElevation = 0.0)
        => FloorPlanViewLocator.ReadColumnIdsForFloor(_doc, levelName, levelElevation);

    /// <summary>
    /// Diagnostic variant of <see cref="ReadColumnIdsForFloor"/>: returns the
    /// full <see cref="FloorViewLookupResult"/> so the caller can surface a
    /// meaningful explanation when no columns are found (matched-level count,
    /// candidate-view count, chosen view, etc.).  Always returns non-null.
    /// </summary>
    public FloorViewLookupResult LocateColumnsForFloor(string levelName, double levelElevation = 0.0)
        => FloorPlanViewLocator.LocateColumnIdsForFloor(_doc, levelName, levelElevation);

    /// <summary>
    /// Convenience: builds a per-level map for ALL levels that have floor-plan views.
    /// NOTE: Prefer <see cref="ReadColumnIdsForFloor"/> for on-demand single-floor queries.
    /// </summary>
    public Dictionary<string, HashSet<long>> ReadColumnIdsByFloorPlan()
    {
        var allLevelNames = new FilteredElementCollector(_doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .Select(l => l.Name)
            .ToList();

        var result = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in allLevelNames)
        {
            var ids = ReadColumnIdsForFloor(name);
            if (ids.Count > 0)
                result[name] = ids;
        }
        return result;
    }

    /// <summary>
    /// Returns a mapping of level name → elevation in Revit internal feet.
    /// Used for elevation-based SPECIFIC FLOOR filtering (supports multi-story columns).
    /// </summary>
    public Dictionary<string, double> ReadLevelElevations()
    {
        return LevelsByElevation()
            .ToDictionary(l => l.Name, l => l.Elevation, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns a dictionary of discipline → sorted level names.
    /// Keys always include "All". Additional keys depend on which ViewPlan disciplines exist.
    /// Common keys: "Structural", "Architectural", "Mechanical", "Electrical".
    /// </summary>
    public Dictionary<string, List<string>> ReadLevelNamesByDiscipline()
    {
        var elevationMap = LevelsByElevation()
            .ToDictionary(l => l.Name, l => l.Elevation, StringComparer.OrdinalIgnoreCase);

        var allLevelNames = elevationMap.Keys.OrderBy(n => elevationMap[n]).ToList();

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["All"] = allLevelNames
        };

        // Collect discipline → level names from ViewPlan views
        var disciplineToLevels = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var views = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v => !v.IsTemplate);

        foreach (var view in views)
        {
            try
            {
                var level = view.GenLevel;
                if (level == null) continue;

                var discipline = DisciplineName(view);
                if (!disciplineToLevels.ContainsKey(discipline))
                    disciplineToLevels[discipline] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                disciplineToLevels[discipline].Add(level.Name);
            }
            catch { /* skip views that throw */ }
        }

        foreach (var kvp in disciplineToLevels)
        {
            result[kvp.Key] = kvp.Value
                .Where(n => elevationMap.ContainsKey(n))
                .OrderBy(n => elevationMap[n])
                .ToList();
        }

        return result;
    }

    // ── Family metadata ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns distinct family names for all supported column categories
    /// (Structural Columns + Architectural Columns) that have instances in the model.
    /// Key = display category name, Value = sorted family name list.
    /// </summary>
    public Dictionary<string, List<string>> ReadFamilyNamesByCategory()
    {
        var categories = new[]
        {
            "Structural Columns",
            "Architectural Columns",
        };

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var cat in categories)
        {
            var names = new FilteredElementCollector(_doc)
                .OfCategory(CategoryNameToBuiltIn(cat))
                .OfClass(typeof(FamilyInstance))
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol != null)
                .Select(fi => fi.Symbol.FamilyName ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();

            if (names.Count > 0)
                result[cat] = names;
        }

        // Always ensure Structural Columns key exists
        if (!result.ContainsKey("Structural Columns"))
            result["Structural Columns"] = [];

        return result;
    }

    /// <summary>Legacy: returns structural column family names only.</summary>
    public List<string> ReadFamilyNames() =>
        ReadFamilyNamesByCategory().GetValueOrDefault("Structural Columns") ?? [];

    // ── Parameter metadata ────────────────────────────────────────────────────

    /// <summary>
    /// Returns writable string parameter names from the first structural column found.
    /// Always starts with "Mark".
    /// </summary>
    public List<string> ReadTextParameterNames()
    {
        const string defaultMark = "Mark";

        var firstColumn = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_StructuralColumns)
            .OfClass(typeof(FamilyInstance))
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>()
            .FirstOrDefault();

        if (firstColumn == null) return [defaultMark];

        var names = firstColumn.Parameters
            .Cast<Parameter>()
            .Where(p => p.Definition != null &&
                        p.StorageType == StorageType.String &&
                        !p.IsReadOnly)
            .Select(p => p.Definition.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        names.Remove(defaultMark);
        names.Insert(0, defaultMark);
        return names;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Maps a display category name to the Revit BuiltInCategory.</summary>
    public static BuiltInCategory CategoryNameToBuiltIn(string name) => name switch
    {
        "Architectural Columns" => BuiltInCategory.OST_Columns,
        _                       => BuiltInCategory.OST_StructuralColumns,
    };

    private List<Level> LevelsByElevation() =>
        new FilteredElementCollector(_doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .ToList();

    private static string DisciplineName(View view)
    {
        try
        {
            return view.Discipline switch
            {
                ViewDiscipline.Structural    => "Structural",
                ViewDiscipline.Architectural => "Architectural",
                ViewDiscipline.Mechanical    => "Mechanical",
                ViewDiscipline.Electrical    => "Electrical",
                ViewDiscipline.Plumbing      => "Plumbing",
                ViewDiscipline.Coordination  => "Coordination",
                _                            => "Other",
            };
        }
        catch { return "Other"; }
    }

    private Level? GetLevel(FamilyInstance fi, BuiltInParameter param)
    {
        var id = fi.get_Parameter(param)?.AsElementId();
        return id != null && id != ElementId.InvalidElementId
            ? _doc.GetElement(id) as Level
            : null;
    }
}
