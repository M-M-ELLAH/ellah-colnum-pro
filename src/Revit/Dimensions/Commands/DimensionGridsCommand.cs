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
/// Smart Grid Dimensions — Phase 1.
/// Revit calls Execute() when the user clicks the ribbon button.
/// Collects all straight grid lines, opens the DimensionDialog for
/// view/style selection, then creates dimension strings in one Transaction.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class DimensionGridsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiApp = commandData.Application;
        var uiDoc = uiApp.ActiveUIDocument;

        // ── Guard: open project document required ─────────────────────────
        if (uiDoc == null)
        {
            TaskDialog.Show("ELLAH-ColNum Pro — Smart Dimensions",
                "No document is open.\nPlease open a Revit project first.");
            return Result.Cancelled;
        }

        var doc = uiDoc.Document;

        if (doc.IsFamilyDocument)
        {
            TaskDialog.Show("ELLAH-ColNum Pro — Smart Dimensions",
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

            // ── 1. Collect grids ──────────────────────────────────────────
            var collector = new RevitGridCollector(doc);
            var (grids, gridMap) = collector.ReadAllGrids();

            if (grids.Count < 2)
            {
                TaskDialog.Show("ELLAH-ColNum Pro — Smart Dimensions",
                    "Not enough grid lines found in this document.\n\n" +
                    "Smart Grid Dimensions needs at least 2 straight (linear) grid lines.\n\n" +
                    "Arc-based or spline grids are not supported in Phase 1.");
                return Result.Cancelled;
            }

            // ── 2. Collect views and dimension types ──────────────────────
            var viewsByDiscipline = collector.ReadPlanViewsByDiscipline();
            var dimTypes          = collector.ReadDimensionTypes();

            // ── 3. Open settings dialog ───────────────────────────────────
            var dialog = new DimensionDialog(grids, viewsByDiscipline, dimTypes);
            new WindowInteropHelper(dialog).Owner = commandData.Application.MainWindowHandle;

            if (dialog.ShowDialog() != true)
                return Result.Cancelled;

            var options = dialog.Result!;

            // ── 4. Build dimension plan (pure logic — no Revit) ───────────
            var engine = new DimensionEngine();
            var plan   = engine.BuildPlan(grids, options);

            bool hasVertical   = options.DimensionVerticalGrids   && plan.VerticalGrids.Count   >= 2;
            bool hasHorizontal = options.DimensionHorizontalGrids && plan.HorizontalGrids.Count >= 2;

            if (!hasVertical && !hasHorizontal)
            {
                TaskDialog.Show("ELLAH-ColNum Pro — Smart Dimensions",
                    "Not enough grids in the selected direction(s).\n\n" +
                    "Each selected direction needs at least 2 parallel grid lines.\n\n" +
                    $"Vertical (N-S): {plan.VerticalGrids.Count} found  " +
                    $"·  Horizontal (E-W): {plan.HorizontalGrids.Count} found");
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
                TaskDialog.Show("ELLAH-ColNum Pro — Smart Dimensions",
                    "No valid views could be found for the selected IDs.\n\n" +
                    "Please reopen the dialog and select at least one view.");
                return Result.Cancelled;
            }

            // ── 6. Resolve DimensionType (null = Revit default) ───────────
            DimensionType? dimType = null;
            if (!string.IsNullOrWhiteSpace(options.DimensionTypeName))
            {
                dimType = new FilteredElementCollector(doc)
                    .OfClass(typeof(DimensionType))
                    .Cast<DimensionType>()
                    .FirstOrDefault(dt => dt.Name == options.DimensionTypeName);
            }

            // ── 7. Create dimensions inside an undoable Transaction ───────
            using var tx = new Transaction(doc, "ELLAH-ColNum Pro: Smart Grid Dimensions");
            tx.Start();

            var helper  = new DimensionHelper(doc);
            int created = helper.CreateDimensions(plan, gridMap, selectedViews, options, dimType);

            tx.Commit();

            // ── 8. Success notification ───────────────────────────────────
            TaskDialog.Show("ELLAH-ColNum Pro — Smart Dimensions — Done",
                $"Successfully created {created} dimension string(s) " +
                $"across {selectedViews.Count} view(s).\n\n" +
                "To undo: press Ctrl+Z in Revit.");

            return Result.Succeeded;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = $"ELLAH-ColNum Pro Smart Dimensions error: {ex.Message}";
            TaskDialog.Show("ELLAH-ColNum Pro — Error",
                $"An unexpected error occurred:\n\n{ex.Message}\n\n" +
                "No changes were made to your model.");
            return Result.Failed;
        }
    }
}
