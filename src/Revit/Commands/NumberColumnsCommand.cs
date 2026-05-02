using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using EllahColNum.Core.Geometry;
using EllahColNum.Core.Licensing;
using EllahColNum.Core.Models;
using EllahColNum.Core.Services;
using EllahColNum.Revit.Collectors;
using EllahColNum.Revit.Helpers;
using EllahColNum.Revit.UI;
using EllahColNum.Revit.Writers;
using System.Windows.Interop;

namespace EllahColNum.Revit.Commands;

/// <summary>
/// Entry point: Revit calls Execute() when the user clicks the ribbon button.
/// Opens the NumberingDialog for settings + preview, then writes marks on confirm.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class NumberColumnsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiApp = commandData.Application;
        var uiDoc = uiApp.ActiveUIDocument;

        // ── Guard: must have an open document ─────────────────────────────
        if (uiDoc == null)
        {
            TaskDialog.Show("ELLAH-ColNum Pro",
                "No document is open.\nPlease open a Revit project first.");
            return Result.Cancelled;
        }

        var doc = uiDoc.Document;

        // ── Guard: document must not be a family editor ───────────────────
        if (doc.IsFamilyDocument)
        {
            TaskDialog.Show("ELLAH-ColNum Pro",
                "This command works only in a project document (not in the Family Editor).");
            return Result.Cancelled;
        }

        try
        {
            // ── 0. License check ──────────────────────────────────────────
            if (!LicenseChecker.IsLicensed())
            {
                var activation = new ActivationDialog();
                new WindowInteropHelper(activation).Owner = commandData.Application.MainWindowHandle;
                activation.ShowDialog();

                if (!activation.Activated)
                {
                    TaskDialog.Show("ELLAH-ColNum Pro",
                        "A valid license is required to use this plugin.\n\n" +
                        "Contact us at ellah@ellah.co.il to purchase a license.");
                    return Result.Cancelled;
                }
            }

            // ── 1. Collect metadata (levels by discipline, families by category, parameters) ──
            // Note: floor column-ID sets are computed LAZILY inside the dialog (per selected
            // floor) to keep startup fast — ReadColumnIdsByFloorPlan() is NOT called here.
            var reader              = new RevitColumnCollector(doc);
            var levelsByDiscipline  = reader.ReadLevelNamesByDiscipline();
            var levelElevations     = reader.ReadLevelElevations();
            var familiesByCategory  = reader.ReadFamilyNamesByCategory();
            var paramNames          = reader.ReadTextParameterNames();

            // ── 2. Collect all structural columns (no filter yet) ─────────
            var allColumns = reader.ReadAllColumns();

            if (allColumns.Count == 0)
            {
                var dlg = new TaskDialog("ELLAH-ColNum Pro")
                {
                    MainInstruction = "No structural columns found.",
                    MainContent     =
                        "Make sure the model contains elements of category " +
                        "\"Structural Columns\" (not Architectural columns).\n\n" +
                        "If you are in a linked model view, try switching to the host model.",
                    CommonButtons = TaskDialogCommonButtons.Close,
                };
                dlg.Show();
                return Result.Cancelled;
            }

            // ── 2b. Detect zones & per-column rotations ────────────────────
            // Engineering-correct multi-axis support:
            //   1. Read every linear Revit Grid as a Core GridLine2D.
            //   2. Cluster grids by orientation → each cluster is one "axis
            //      system" of the building (e.g. tilted core + orthogonal
            //      podium).
            //   3. Score each column against every cluster using the distance
            //      to the nearest perpendicular grid intersection inside that
            //      cluster.  Each column lands in the zone whose grids
            //      actually pass through its position.
            //   4. Pass the resulting per-column zone + rotation maps to the
            //      engine via NumberingOptions.  The engine then clusters
            //      rows/columns within each zone's local frame and orders
            //      clusters globally by the PROJECT frame so the engineer
            //      reads the plan in a single coherent sweep.
            //
            // Pure orthogonal buildings → one zone at 0° → behaves identically
            // to the legacy code path.  Uniform tilted buildings → one zone
            // at the detected angle → engine rotates everything by that angle.
            // Mixed buildings → multiple zones, each handled in its own frame.
            var gridLines = RevitGridReader.ReadGridLines(doc);

            var classifierColumns = allColumns
                .Select(c => (c.ElementId,
                              Position: new EllahColNum.Core.Geometry.Point2D(c.X, c.Y)))
                .ToList();
            var zoneMap = EllahColNum.Core.Geometry.BuildingZoneClassifier.Classify(
                gridLines, classifierColumns);

            Dictionary<long, double>? perColumnRotation = null;
            Dictionary<long, int>?    perColumnZone     = null;
            double singleZoneRotation                    = 0.0;

            if (zoneMap.Zones.Count == 1)
            {
                // Uniform building — keep the simple single-rotation path.
                singleZoneRotation = zoneMap.Zones[0].RotationDegrees;
            }
            else if (zoneMap.Zones.Count >= 2)
            {
                // Multi-zone — build the per-column maps the engine consumes.
                perColumnRotation = new Dictionary<long, double>(allColumns.Count);
                perColumnZone     = new Dictionary<long, int>(allColumns.Count);

                foreach (var (id, zoneId) in zoneMap.ColumnZoneByElementId)
                {
                    perColumnZone    [id] = zoneId;
                    perColumnRotation[id] = zoneMap.Zones[zoneId].RotationDegrees;
                }
            }
            else if (gridLines.Count < 4)
            {
                // No usable grids — last-resort PCA fallback (kept tight on
                // confidence so it never overrides a working orthogonal path).
                const double MinUsefulAngleDeg = 1.0;
                const double MinPcaConfidence  = 0.6;
                var pcaAxis = EllahColNum.Core.Geometry.BuildingAxisDetector.Detect(
                    allColumns.Select(c => (c.X, c.Y)));
                if (Math.Abs(pcaAxis.AngleDegrees) >= MinUsefulAngleDeg &&
                    pcaAxis.Confidence            >= MinPcaConfidence)
                {
                    singleZoneRotation = pcaAxis.AngleDegrees;
                }
            }

            // ── 3. Open settings + preview dialog (re-entry loop for Choose Your Way) ──
            var defaultOptions  = new NumberingOptions
            {
                BuildingRotationDegrees    = singleZoneRotation,
                ColumnRotationByElementId  = perColumnRotation,
                ColumnZoneByElementId      = perColumnZone,
            };
            var grouper         = new ColumnGrouper(defaultOptions.PositionToleranceFeet);
            var groups          = grouper.Group(allColumns);

            long?            anchorId     = null;
            string?          anchorFloor  = null;
            string?          anchorGrid   = null;
            string?          anchorFamily = null;
            NumberingOptions? savedOptions = null;

            NumberingOptions finalOptions;

            while (true)
            {
                var dialog = new NumberingDialog(
                    groups,
                    savedOptions ?? defaultOptions,
                    allColumns.Count,
                    levelsByDiscipline,
                    levelElevations,
                    familiesByCategory,
                    paramNames,
                    uiDoc,
                    anchorId,
                    anchorFloor,
                    anchorGrid,
                    anchorFamily,
                    savedOptions);

                new WindowInteropHelper(dialog).Owner = commandData.Application.MainWindowHandle;
                dialog.ShowDialog();

                if (dialog.PickColumnRequested)
                {
                    savedOptions = dialog.CurrentOptions;
                    try
                    {
                        var sel = uiDoc.Selection.PickObject(
                            Autodesk.Revit.UI.Selection.ObjectType.Element,
                            "Click the column you want numbering to start from");
                        if (doc.GetElement(sel) is Autodesk.Revit.DB.FamilyInstance fi)
                        {
                            anchorId     = fi.Id.Value;
                            anchorFamily = fi.Symbol?.FamilyName ?? "";
                            anchorGrid   = fi.get_Parameter(BuiltInParameter.COLUMN_LOCATION_MARK)
                                             ?.AsString() ?? "";

                            // Use the ACTIVE VIEW's associated level as the anchor floor.
                            // This reflects the floor the user is actually working on —
                            // critical for multi-story columns that span many floors:
                            // the column's BaseLevelParam would return floor 1 even when
                            // the user clicks the column in the floor 6 structural plan.
                            var viewLevel  = (uiDoc.ActiveView as Autodesk.Revit.DB.ViewPlan)?.GenLevel;
                            Level? anchorLevel = viewLevel;

                            // Fallback: use the column's own base level if the active view
                            // has no associated floor level (e.g. 3-D or section views).
                            if (anchorLevel == null)
                            {
                                var baseLevelId = fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_PARAM)
                                                    ?.AsElementId();
                                if (baseLevelId != null && baseLevelId != ElementId.InvalidElementId)
                                    anchorLevel = doc.GetElement(baseLevelId) as Level;
                                if (anchorLevel == null && fi.LevelId != ElementId.InvalidElementId)
                                    anchorLevel = doc.GetElement(fi.LevelId) as Level;
                            }

                            anchorFloor = anchorLevel?.Name ?? "";
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { /* user cancelled pick */ }
                    continue;
                }

                // ── Manual mark-correction mode ──────────────────────
                // The user clicked "Edit Column Marks" on the bottom bar.
                // We hand control over to the dedicated pick-and-edit
                // loop, which handles its own transactions and exits
                // cleanly when the user is finished.  Returning from the
                // command here is the right thing — there's no numbering
                // engine run on this code path.
                if (dialog.EditMarksRequested)
                {
                    return RunManualMarkEditLoop(
                        commandData,
                        uiDoc,
                        doc,
                        allColumns,
                        dialog.CurrentOptions);
                }

                if (dialog.DialogResult != true)
                    return Result.Cancelled;

                finalOptions = dialog.Result!;
                break;
            }

            // ── 4. Re-collect with family filter + target category applied ─
            var filteredColumns = reader.ReadAllColumns(
                finalOptions.FamilyFilter.Count > 0 ? finalOptions.FamilyFilter : null,
                finalOptions.TargetCategoryName);

            // ── 4b. SPECIFIC FLOOR — keep only elements relevant to the chosen floor ──
            // Primary path: ask Revit which structural columns are VISIBLE in the floor's
            // best plan view.  FloorPlanViewLocator handles BiDi/Hebrew name normalisation,
            // Level.ElementId matching, discipline priority, and view-template visibility.
            //
            // Fallback: elevation-based matching when no plan view yields any column,
            // including span-through detection for multi-story columns.  The elevation
            // is taken from finalOptions.SpecificFloorElevation, which the dialog populates
            // from the FloorItem cached at populate-time — immune to dictionary-lookup
            // failures caused by Hebrew encoding differences across separate API calls.
            if (!string.IsNullOrWhiteSpace(finalOptions.SpecificFloorName))
            {
                var floorName = finalOptions.SpecificFloorName;
                var floorElev = finalOptions.SpecificFloorElevation;

                var floorIds = reader.ReadColumnIdsForFloor(floorName, floorElev);

                if (floorIds.Count > 0)
                {
                    const double tol = 0.1;
                    filteredColumns = filteredColumns
                        .Where(c => floorIds.Contains(c.ElementId) &&
                            (EllahColNum.Core.Text.BidiText.EqualsIgnoreBidi(c.TopLevelName, floorName) ||
                             (floorElev > 0 && Math.Abs(c.TopLevelElevation - floorElev) <= tol)        ||
                             (floorElev > 0 && c.BaseLevelElevation < floorElev - tol &&
                                               c.TopLevelElevation   > floorElev + tol)))
                        .ToList();
                }
                else
                {
                    const double tol = 0.1;
                    filteredColumns = filteredColumns
                        .Where(c =>
                            EllahColNum.Core.Text.BidiText.EqualsIgnoreBidi(c.BaseLevelName, floorName) ||
                            EllahColNum.Core.Text.BidiText.EqualsIgnoreBidi(c.TopLevelName,  floorName) ||
                            (floorElev > 0 && Math.Abs(c.BaseLevelElevation - floorElev) <= tol)        ||
                            (floorElev > 0 && Math.Abs(c.TopLevelElevation  - floorElev) <= tol)        ||
                            // Span-through: multi-story column whose base is below and top
                            // is above this floor's elevation — Revit shows it in the plan.
                            (floorElev > 0 &&
                             c.BaseLevelElevation < floorElev - tol &&
                             c.TopLevelElevation  > floorElev + tol))
                        .ToList();
                }
            }

            // ── 5. Re-group with the (possibly different) tolerance ────────
            var filteredGrouper = new ColumnGrouper(finalOptions.PositionToleranceFeet);
            var filteredGroups  = filteredGrouper.Group(filteredColumns);

            // ── 6. Run numbering engine ────────────────────────────────────
            var engine = new NumberingEngine(finalOptions);
            var result = engine.AssignMarks(filteredGroups);

            // ── 7. Write marks inside an undoable Transaction ─────────────
            using var tx = new Transaction(doc, "ELLAH-ColNum Pro: Number Structural Columns");
            tx.Start();

            var writer  = new RevitMarkWriter(doc, finalOptions.TargetParameterName);
            int updated = writer.WriteMarks(filteredColumns);

            tx.Commit();

            // ── 8. Brief success notification ─────────────────────────────
            var paramLabel = string.IsNullOrWhiteSpace(finalOptions.TargetParameterName)
                ? "Mark"
                : finalOptions.TargetParameterName;

            TaskDialog.Show("ELLAH-ColNum Pro — Done",
                $"Successfully numbered {result.Groups.Count} column positions.\n" +
                $"Elements updated: {updated}\n" +
                $"Written to parameter: {paramLabel}\n\n" +
                "To undo: press Ctrl+Z in Revit.");

            return Result.Succeeded;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = $"ELLAH-ColNum Pro error: {ex.Message}";
            TaskDialog.Show("ELLAH-ColNum Pro — Error",
                $"An unexpected error occurred:\n\n{ex.Message}\n\n" +
                "No changes were made to your model.");
            return Result.Failed;
        }
    }

    /// <summary>
    /// Manual mark-correction loop.
    ///
    /// Entered when the user clicks "Edit Column Marks" on the bottom bar of
    /// <see cref="NumberingDialog"/>.  Walks one column at a time:
    ///   1. <see cref="UIDocument.Selection.PickObject"/> prompts the user to
    ///      click a column (ESC exits the loop).
    ///   2. A small <see cref="EditMarkDialog"/> shows the column's current
    ///      mark and lets the user type a new one.
    ///   3. <b>Save → Pick next</b> writes the mark inside its own
    ///      Transaction (so each edit is independently undoable) and loops.
    ///   4. <b>Save and Done</b> writes the final edit and exits the loop.
    ///   5. <b>Cancel</b> on the popup discards the edit and returns to the
    ///      picker.
    ///
    /// Vertical propagation: when the picked column belongs to a multi-floor
    /// group (same XY position across levels) every member of the group
    /// receives the new mark, so column "C-103" stays "C-103" on every floor
    /// it spans.  Single-floor / orphan columns are updated alone.
    ///
    /// Diagnostic safety: each save runs inside its own Transaction so a
    /// caught Revit error never corrupts mid-session.  All UI strings are
    /// defensive against null parameters.
    /// </summary>
    private Result RunManualMarkEditLoop(
        ExternalCommandData commandData,
        UIDocument          uiDoc,
        Document            doc,
        List<ColumnData>    allColumns,
        NumberingOptions    options)
    {
        // Build an ElementId → group map once, so a single click can
        // propagate to every column instance the user implicitly selected.
        var grouper = new ColumnGrouper(options.PositionToleranceFeet);
        var groups  = grouper.Group(allColumns);
        var idToGroup = new Dictionary<long, ColumnGroup>();
        foreach (var g in groups)
            foreach (var c in g.Columns)
                idToGroup[c.ElementId] = g;

        var paramName = string.IsNullOrWhiteSpace(options.TargetParameterName)
            ? "Mark"
            : options.TargetParameterName;

        int totalUpdated = 0;
        int columnsEdited = 0;

        while (true)
        {
            FamilyInstance? picked;
            try
            {
                var sel = uiDoc.Selection.PickObject(
                    Autodesk.Revit.UI.Selection.ObjectType.Element,
                    "Click a column to edit its mark.  Press ESC when finished.");
                picked = doc.GetElement(sel) as FamilyInstance;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                break; // ESC pressed → exit loop normally
            }

            if (picked == null) continue;

            // Read the current mark off the picked instance using the same
            // parameter the engine writes to (Mark by default, or whichever
            // text parameter the user configured upstream).
            var currentMark = picked.LookupParameter(paramName)?.AsString()
                              ?? picked.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString()
                              ?? "";

            // Show the popup, anchored to Revit's main window so it stays
            // on top of the picker.
            var popup = new EditMarkDialog(currentMark);
            new System.Windows.Interop.WindowInteropHelper(popup).Owner =
                commandData.Application.MainWindowHandle;
            var ok = popup.ShowDialog() == true;
            if (!ok) continue; // user cancelled this one — keep picking

            // ── Apply the new mark in its own Transaction ─────────────────
            // Per-edit transactions give the engineer a clean Ctrl+Z stack
            // (every Save = one undoable step) and isolate any Revit-side
            // failure to the column the user was editing.
            try
            {
                using var tx = new Transaction(doc, $"Edit column mark → {popup.NewMark}");
                tx.Start();

                if (idToGroup.TryGetValue(picked.Id.Value, out var group))
                {
                    foreach (var c in group.Columns) c.AssignedMark = popup.NewMark;
                    var writer = new RevitMarkWriter(doc, paramName);
                    totalUpdated += writer.WriteMarks(group.Columns);
                }
                else
                {
                    // Column the grouper didn't see (e.g. category change
                    // since we collected) — update just this one instance.
                    var single = new List<ColumnData>
                    {
                        new() { ElementId = picked.Id.Value, AssignedMark = popup.NewMark }
                    };
                    var writer = new RevitMarkWriter(doc, paramName);
                    totalUpdated += writer.WriteMarks(single);
                }

                tx.Commit();
                columnsEdited++;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("ELLAH-ColNum Pro — Edit failed",
                    $"Could not save mark for the picked column:\n\n{ex.Message}\n\n" +
                    "The other columns you've already edited are unaffected.");
            }

            if (popup.FinishedAfterSave) break; // user clicked "Save and Done"
        }

        if (columnsEdited == 0)
            return Result.Cancelled;

        TaskDialog.Show("ELLAH-ColNum Pro — Manual edits saved",
            $"Updated {columnsEdited} column position(s).\n" +
            $"Element instances changed: {totalUpdated}\n" +
            $"Parameter: {paramName}\n\n" +
            "Each save is its own undo step — Ctrl+Z reverses one at a time.");

        return Result.Succeeded;
    }
}
