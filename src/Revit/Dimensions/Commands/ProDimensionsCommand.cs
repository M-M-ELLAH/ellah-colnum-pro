using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using EllahColNum.Core.Dimensions.Models;
using EllahColNum.Core.Dimensions.Services;
using EllahColNum.Core.Licensing;
using EllahColNum.Revit.Dimensions.Collectors;
using EllahColNum.Revit.Dimensions.Helpers;
using EllahColNum.Revit.Dimensions.UI;
using EllahColNum.Revit.UI;
using System.Windows.Interop;

namespace EllahColNum.Revit.Dimensions.Commands;

/// <summary>
/// Pro Dimensions — Phase 2.
/// Creates multi-layer dimension strings from real building elements:
/// structural columns, wall faces, door/window openings, and grid lines.
///
/// Execution flow:
///   0. Guard: open project document required
///   1. License check
///   2. Collect all element data + References  (RevitElementCollector)
///   3. Show ProDimensionDialog                (WPF)
///   4. Build ProDimensionPlan                 (ProDimensionEngine — pure logic)
///   5. Create Dimension elements in one Transaction (ProDimensionHelper)
///   6. Report result to user
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class ProDimensionsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiApp = commandData.Application;
        var uiDoc = uiApp.ActiveUIDocument;

        // ── Guard: need an open project document ──────────────────────────
        if (uiDoc == null)
        {
            TaskDialog.Show("ELLAH-ColNum Pro — Pro Dimensions",
                "No document is open.\nPlease open a Revit project first.");
            return Result.Cancelled;
        }

        var doc = uiDoc.Document;

        if (doc.IsFamilyDocument)
        {
            TaskDialog.Show("ELLAH-ColNum Pro — Pro Dimensions",
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

            // ── 1. Collect all elements + References ──────────────────────
            var collector = new RevitElementCollector(doc);
            var allElements = collector.CollectAll();

            // Require at least some dimensionable elements
            int totalUsable = allElements.Count;
            if (totalUsable < 2)
            {
                TaskDialog.Show("ELLAH-ColNum Pro — Pro Dimensions",
                    "Not enough dimensionable elements found in this document.\n\n" +
                    "Pro Dimensions needs at least 2 elements (columns, walls, grids, or openings).\n\n" +
                    "Please add structural elements to the project and try again.");
                return Result.Cancelled;
            }

            // ── 2. Collect views + dimension types ────────────────────────
            var viewsByDiscipline = collector.ReadPlanViewsByDiscipline();
            var dimTypes          = collector.ReadDimensionTypes();

            // ── 3. Show dialog ────────────────────────────────────────────
            var dialog = new ProDimensionDialog(allElements, viewsByDiscipline, dimTypes);
            new WindowInteropHelper(dialog).Owner = commandData.Application.MainWindowHandle;

            if (dialog.ShowDialog() != true)
                return Result.Cancelled;

            var options = dialog.Result!;

            // ── 4. Build the dimension plan (pure logic) ──────────────────
            var engine = new ProDimensionEngine();
            var plan   = engine.BuildPlan(allElements, options);

            if (!plan.ActiveLayers().Any())
            {
                TaskDialog.Show("ELLAH-ColNum Pro — Pro Dimensions",
                    "No active layers have enough elements to create dimension strings.\n\n" +
                    "Each enabled layer needs at least 2 elements.\n\n" +
                    "Check your layer settings and try again.");
                return Result.Cancelled;
            }

            // ── 5. Resolve selected ViewPlan elements ─────────────────────
            var selectedViews = options.SelectedViewIds
                .Select(id => doc.GetElement(new ElementId(id)) as ViewPlan)
                .Where(v => v != null)
                .Cast<ViewPlan>()
                .ToList();

            if (selectedViews.Count == 0)
            {
                TaskDialog.Show("ELLAH-ColNum Pro — Pro Dimensions",
                    "No valid views could be found for the selected IDs.\n\n" +
                    "Please reopen the dialog and select at least one view.");
                return Result.Cancelled;
            }

            // ── 6. Compute building extents (used to position dim lines) ──
            var bounds = ProDimensionHelper.ComputeBuildingBounds(allElements);

            // ── 7. Create dimensions in one undoable Transaction ──────────
            using var tx = new Transaction(doc, "ELLAH-ColNum Pro: Pro Dimensions");
            tx.Start();

            var helper  = new ProDimensionHelper(doc);
            int created = helper.CreateDimensions(plan, collector.RefMap, selectedViews, bounds);

            tx.Commit();

            // ── 8. Report success ─────────────────────────────────────────
            var layerNames = plan.ActiveLayers()
                .Select(lg => lg.Category.ToString().ToLower())
                .ToList();

            TaskDialog.Show("ELLAH-ColNum Pro — Pro Dimensions — Done",
                $"Successfully created {created} dimension string(s) " +
                $"across {selectedViews.Count} view(s).\n\n" +
                $"Layers applied: {string.Join(", ", layerNames)}\n\n" +
                "To undo: press Ctrl+Z in Revit.");

            return Result.Succeeded;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = $"ELLAH-ColNum Pro Pro Dimensions error: {ex.Message}";
            TaskDialog.Show("ELLAH-ColNum Pro — Error",
                $"An unexpected error occurred:\n\n{ex.Message}\n\n" +
                "No changes were made to your model.");
            return Result.Failed;
        }
    }
}
