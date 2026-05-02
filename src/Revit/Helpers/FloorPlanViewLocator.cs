using Autodesk.Revit.DB;
using EllahColNum.Core.Text;

namespace EllahColNum.Revit.Helpers;

/// <summary>
/// Robust discovery of floor-plan views for a given building level.
///
/// Why this is its own class:
///   Hebrew/RTL level names, multiple view disciplines per floor, and
///   view templates that hide structural columns each independently break
///   naive name-based view lookups.  Centralising the resolution rules in
///   one place — outside the column collector — keeps the logic auditable
///   and lets us add new fallbacks (or diagnostics) without touching the
///   rest of the plug-in.
///
/// Resolution strategy (most-reliable first):
///   1. Locate <see cref="Level"/> objects by Bidi-normalised name OR by
///      elevation (handles renamed/duplicate levels and Hebrew encoding).
///   2. Find every <see cref="ViewPlan"/> whose GenLevel.Id matches any of
///      those Levels — element-id matching bypasses string issues entirely.
///   3. Order candidates by discipline preference for column numbering:
///        Structural → Architectural → Coordination → Other.
///      Mechanical / Electrical / Plumbing views are deliberately skipped
///      because their view templates almost always hide columns.
///   4. For each candidate view, query Revit for the structural columns
///      visible there.  Return the FIRST view that yields ≥1 column.
///   5. If every candidate returned 0 columns, return an empty set so the
///      caller can fall back to elevation/span-through filtering.
///
/// All public methods are side-effect free and never throw — failures are
/// reported through return values or the diagnostic record.
/// </summary>
public static class FloorPlanViewLocator
{
    /// <summary>
    /// Discipline ordering used when ranking candidate views for column
    /// numbering.  Lower index = higher priority.  Disciplines NOT listed
    /// here (Mechanical / Electrical / Plumbing) are skipped entirely.
    /// </summary>
    private static readonly ViewDiscipline[] _columnDisciplinePriority =
    {
        ViewDiscipline.Structural,
        ViewDiscipline.Architectural,
        ViewDiscipline.Coordination,
    };

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the set of structural-column ElementIds visible on the floor
    /// identified by <paramref name="levelName"/>.  When <paramref name="levelElevation"/>
    /// is positive it is used as a tiebreaker / encoding-safe fallback for
    /// matching the underlying <see cref="Level"/>.
    ///
    /// Returns an empty set when no candidate view contains any column —
    /// the caller is then expected to apply its elevation/span-through
    /// filter on the full column list.
    /// </summary>
    public static HashSet<long> ReadColumnIdsForFloor(
        Document doc,
        string   levelName,
        double   levelElevation = 0.0)
    {
        var diag = LocateColumnIdsForFloor(doc, levelName, levelElevation);
        return diag.ColumnIds;
    }

    /// <summary>
    /// Like <see cref="ReadColumnIdsForFloor"/>, but also returns a record
    /// describing which view was used and why other candidates were skipped.
    /// Useful for diagnostics dialogs.  Always returns a non-null result.
    /// </summary>
    public static FloorViewLookupResult LocateColumnIdsForFloor(
        Document doc,
        string   levelName,
        double   levelElevation = 0.0)
    {
        var result = new FloorViewLookupResult { RequestedLevelName = levelName };

        var matchingLevels = FindMatchingLevels(doc, levelName, levelElevation);
        result.MatchedLevelCount = matchingLevels.Count;
        if (matchingLevels.Count == 0) return result;

        var matchingLevelIds = matchingLevels.Select(l => l.Id).ToHashSet();
        var candidates       = CollectCandidateViews(doc, matchingLevelIds);
        result.CandidateViewCount = candidates.Count;
        if (candidates.Count == 0) return result;

        // Try each candidate in discipline-priority order; first non-empty wins.
        foreach (var view in OrderByDisciplinePriority(candidates))
        {
            HashSet<long> ids;
            try
            {
                ids = new FilteredElementCollector(doc, view.Id)
                    .OfCategory(BuiltInCategory.OST_StructuralColumns)
                    .WhereElementIsNotElementType()
                    .Select(e => e.Id.Value)
                    .ToHashSet();
            }
            catch
            {
                continue; // some views throw under specific phase/template combos — skip
            }

            if (ids.Count > 0)
            {
                result.ChosenViewName       = view.Name;
                result.ChosenViewDiscipline = SafeDisciplineName(view);
                result.ColumnIds            = ids;
                return result;
            }
        }

        // All candidates returned zero columns — let the caller fall back.
        return result;
    }

    /// <summary>
    /// Returns the best ViewPlan associated with <paramref name="levelName"/>,
    /// preferring Structural over Architectural / Coordination.  Used by
    /// callers that need the view itself (e.g. selecting / activating it).
    /// Returns null when no suitable view exists.
    /// </summary>
    public static ViewPlan? FindBestViewForLevel(
        Document doc,
        string   levelName,
        double   levelElevation = 0.0)
    {
        var matchingLevels = FindMatchingLevels(doc, levelName, levelElevation);
        if (matchingLevels.Count == 0) return null;

        var matchingLevelIds = matchingLevels.Select(l => l.Id).ToHashSet();
        var candidates       = CollectCandidateViews(doc, matchingLevelIds);
        return OrderByDisciplinePriority(candidates).FirstOrDefault();
    }

    // ── Internals ─────────────────────────────────────────────────────────

    /// <summary>
    /// Finds every <see cref="Level"/> in the document that matches the
    /// requested level — first by Bidi-normalised name, then (if the name
    /// match is empty) by elevation tolerance.  Returning multiple levels is
    /// allowed: in messy projects two distinct Level objects may share an
    /// elevation, and we want the caller to consider views from all of them.
    /// </summary>
    private static List<Level> FindMatchingLevels(
        Document doc,
        string   levelName,
        double   levelElevation)
    {
        var allLevels = new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .ToList();

        if (allLevels.Count == 0) return [];

        var byName = allLevels
            .Where(l => BidiText.EqualsIgnoreBidi(l.Name, levelName))
            .ToList();

        if (byName.Count > 0) return byName;

        // Name match failed (likely Hebrew encoding / rename) — fall back to elevation.
        if (levelElevation > 0)
        {
            const double tol = 0.1; // ~3 cm in Revit internal feet
            var byElev = allLevels
                .Where(l => Math.Abs(l.Elevation - levelElevation) <= tol)
                .ToList();
            if (byElev.Count > 0) return byElev;
        }

        return [];
    }

    /// <summary>
    /// Returns every non-template ViewPlan whose <see cref="ViewPlan.GenLevel"/>
    /// belongs to <paramref name="levelIds"/>.  ElementId matching is
    /// immune to encoding mismatches.
    /// </summary>
    private static List<ViewPlan> CollectCandidateViews(
        Document     doc,
        HashSet<ElementId> levelIds)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .Where(v =>
            {
                if (v.IsTemplate) return false;
                try
                {
                    var lvl = v.GenLevel;
                    return lvl != null && levelIds.Contains(lvl.Id);
                }
                catch { return false; }
            })
            .ToList();
    }

    private static IEnumerable<ViewPlan> OrderByDisciplinePriority(
        IEnumerable<ViewPlan> views)
    {
        // Stable ordering: discipline priority first, then any non-priority
        // disciplines preserved at the end for a final attempt.
        var byPriority = new List<ViewPlan>();
        var others     = new List<ViewPlan>();

        foreach (var v in views)
        {
            ViewDiscipline d;
            try { d = v.Discipline; } catch { d = ViewDiscipline.Coordination; }

            if (Array.IndexOf(_columnDisciplinePriority, d) >= 0)
                byPriority.Add(v);
            else
                others.Add(v);
        }

        return byPriority
            .OrderBy(v => Array.IndexOf(_columnDisciplinePriority, SafeDiscipline(v)))
            .Concat(others);
    }

    private static ViewDiscipline SafeDiscipline(View v)
    {
        try { return v.Discipline; }
        catch { return ViewDiscipline.Coordination; }
    }

    private static string SafeDisciplineName(View v) => SafeDiscipline(v) switch
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

/// <summary>
/// Diagnostic outcome of a floor-plan view lookup — useful for both the
/// runtime path (ColumnIds) and for surfacing meaningful messages when
/// the lookup yields nothing.
/// </summary>
public sealed class FloorViewLookupResult
{
    public string         RequestedLevelName   { get; set; } = "";
    public int            MatchedLevelCount    { get; set; }
    public int            CandidateViewCount   { get; set; }
    public string?        ChosenViewName       { get; set; }
    public string?        ChosenViewDiscipline { get; set; }
    public HashSet<long>  ColumnIds            { get; set; } = [];
}
