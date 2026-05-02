using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace EllahColNum.Revit.App;

/// <summary>
/// Registers the ELLAH-ColNum Pro ribbon tab and buttons when Revit starts.
/// Buttons: Number Columns | Pro Dimensions
/// (Smart Grid Dimensions Phase 1 is removed — Pro Dimensions supersedes it.)
/// </summary>
[Regeneration(RegenerationOption.Manual)]
public class EllahColNumProApp : IExternalApplication
{
    public Result OnStartup(UIControlledApplication app)
    {
        try
        {
            const string tabName = "ELLAH-ColNum Pro";
            app.CreateRibbonTab(tabName);

            var panel = app.CreateRibbonPanel(tabName, "Column Tools");

            // ── Number Columns ────────────────────────────────────────────
            panel.AddItem(new PushButtonData(
                name:         "NumberColumns",
                text:         "Number\nColumns",
                assemblyName: Assembly.GetExecutingAssembly().Location,
                className:    "EllahColNum.Revit.Commands.NumberColumnsCommand")
            {
                ToolTip         = "Numbers all structural columns across all floors with a single click.",
                LongDescription =
                    "ELLAH-ColNum Pro detects your existing manual numbering, " +
                    "continues the sequence intelligently, and fills in all unnumbered columns " +
                    "— across every floor, in one operation.",
                Image      = IconNumberColumns(16),
                LargeImage = IconNumberColumns(32),
            });

            // ── Pro Dimensions (hidden — feature under development, not included in current release)
            // panel.AddItem(new PushButtonData(
            //     name:         "ProDimensions",
            //     text:         "Pro\nDimensions",
            //     assemblyName: Assembly.GetExecutingAssembly().Location,
            //     className:    "EllahColNum.Revit.Dimensions.Commands.ProDimensionsCommand")
            // {
            //     ToolTip         = "Creates multi-layer professional dimension strings from columns, walls, openings and grids.",
            //     LongDescription =
            //         "ELLAH-ColNum Pro — Pro Dimensions analyses all structural columns, " +
            //         "wall faces, doors, windows and grid lines in your project and places " +
            //         "stacked dimension strings (one layer per element category) across " +
            //         "every selected plan view in a single operation.",
            //     Image      = IconProDimensions(16),
            //     LargeImage = IconProDimensions(32),
            // });

            return Result.Succeeded;
        }
        catch
        {
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;

    // ══════════════════════════════════════════════════════════════════════
    //  Icon: Number Columns
    //  Visual concept: two column cross-sections (squares) with a numeric
    //  tag — represents "identify columns by number".
    // ══════════════════════════════════════════════════════════════════════
    private static BitmapSource IconNumberColumns(int size)
    {
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            double s = size;

            // ── Background: deep navy gradient ──────────────────────────
            var bgBrush = new LinearGradientBrush(
                Color.FromRgb(18, 38, 80),
                Color.FromRgb(28, 58, 120),
                new Point(0, 0), new Point(1, 1));
            ctx.DrawRectangle(bgBrush, null, new Rect(0, 0, s, s));

            // ── Scale factor so icon works at both 16 and 32 px ─────────
            double sc = s / 32.0;

            // ── Two column cross-sections (solid squares, light grey) ───
            var colBrush = new SolidColorBrush(Color.FromRgb(200, 215, 240));
            double cw = 7 * sc;   // column square width
            double ch = 7 * sc;
            double r  = 1.5 * sc; // corner rounding

            // Left column
            DrawRoundRect(ctx, colBrush, null, 3 * sc, 12 * sc, cw, ch, r);
            // Right column
            DrawRoundRect(ctx, colBrush, null, 17 * sc, 12 * sc, cw, ch, r);

            // ── Dashed connector line between columns ────────────────────
            var dashPen = new Pen(new SolidColorBrush(Color.FromRgb(120, 160, 230)), 1.0 * sc)
            {
                DashStyle = new DashStyle(new double[] { 2.5, 2.0 }, 0)
            };
            ctx.DrawLine(dashPen,
                new Point(10 * sc, 15.5 * sc),
                new Point(17 * sc, 15.5 * sc));

            // ── Number badge (white circle with "1") ─────────────────────
            double bx = 21 * sc, by = 4 * sc, br = 5 * sc;
            ctx.DrawEllipse(
                new SolidColorBrush(Color.FromRgb(80, 140, 255)),
                new Pen(new SolidColorBrush(Colors.White), 0.8 * sc),
                new Point(bx, by), br, br);

            // "1" text inside badge
            var tf = new Typeface(new FontFamily("Segoe UI"),
                FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var ft = new FormattedText("1",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf, 5.5 * sc,
                Brushes.White, 1.0);
            ctx.DrawText(ft, new Point(bx - ft.Width / 2, by - ft.Height / 2));

            // ── Label "No." under columns ─────────────────────────────────
            var tf2 = new Typeface(new FontFamily("Segoe UI"),
                FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var ft2 = new FormattedText("No.",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, tf2, 5.0 * sc,
                new SolidColorBrush(Color.FromRgb(160, 190, 255)), 1.0);
            ctx.DrawText(ft2, new Point(s / 2 - ft2.Width / 2, 22 * sc));
        }

        return ToBitmap(visual, size);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Icon: Pro Dimensions
    //  Visual concept: three stacked dimension strings, each a different
    //  colour (green=grids, blue=columns, red=walls), with arrowheads.
    //  Represents "professional multi-layer dimensioning".
    // ══════════════════════════════════════════════════════════════════════
    private static BitmapSource IconProDimensions(int size)
    {
        var visual = new DrawingVisual();
        using (var ctx = visual.RenderOpen())
        {
            double s = size;

            // ── Background: deep purple gradient ─────────────────────────
            var bgBrush = new LinearGradientBrush(
                Color.FromRgb(45, 12, 70),
                Color.FromRgb(75, 25, 110),
                new Point(0, 0), new Point(1, 1));
            ctx.DrawRectangle(bgBrush, null, new Rect(0, 0, s, s));

            double sc = s / 32.0;

            // Dimension line rows: Y positions, colors
            var layers = new[]
            {
                (y: 8.0,  color: Color.FromRgb(120, 210, 140)),  // grid — green
                (y: 15.0, color: Color.FromRgb(100, 160, 240)),  // columns — blue
                (y: 22.0, color: Color.FromRgb(240, 120, 130)),  // walls — red
            };

            double x1 = 4 * sc, x2 = 28 * sc;

            foreach (var (y, color) in layers)
            {
                double yy    = y * sc;
                double thick = 1.3 * sc;
                var pen      = new Pen(new SolidColorBrush(color), thick);

                // Main horizontal line
                ctx.DrawLine(pen, new Point(x1, yy), new Point(x2, yy));

                // Left tick
                ctx.DrawLine(pen, new Point(x1, yy - 2.5 * sc), new Point(x1, yy + 2.5 * sc));
                // Right tick
                ctx.DrawLine(pen, new Point(x2, yy - 2.5 * sc), new Point(x2, yy + 2.5 * sc));

                // Left arrowhead
                DrawArrow(ctx, pen, new Point(x1 + 5 * sc, yy), new Point(x1, yy), 3 * sc);
                // Right arrowhead
                DrawArrow(ctx, pen, new Point(x2 - 5 * sc, yy), new Point(x2, yy), 3 * sc);

                // Coloured dot at left end
                ctx.DrawEllipse(new SolidColorBrush(color), null,
                    new Point(x1 + 1.5 * sc, yy), 1.5 * sc, 1.5 * sc);
            }
        }

        return ToBitmap(visual, size);
    }

    // ── Drawing helpers ───────────────────────────────────────────────────

    private static void DrawRoundRect(DrawingContext ctx,
        Brush? fill, Pen? pen,
        double x, double y, double w, double h, double r)
    {
        var geo = new StreamGeometry();
        using (var sgc = geo.Open())
        {
            sgc.BeginFigure(new Point(x + r, y), true, true);
            sgc.LineTo(new Point(x + w - r, y), true, false);
            sgc.ArcTo(new Point(x + w, y + r), new Size(r, r), 0, false,
                SweepDirection.Clockwise, true, false);
            sgc.LineTo(new Point(x + w, y + h - r), true, false);
            sgc.ArcTo(new Point(x + w - r, y + h), new Size(r, r), 0, false,
                SweepDirection.Clockwise, true, false);
            sgc.LineTo(new Point(x + r, y + h), true, false);
            sgc.ArcTo(new Point(x, y + h - r), new Size(r, r), 0, false,
                SweepDirection.Clockwise, true, false);
            sgc.LineTo(new Point(x, y + r), true, false);
            sgc.ArcTo(new Point(x + r, y), new Size(r, r), 0, false,
                SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        ctx.DrawGeometry(fill, pen, geo);
    }

    private static void DrawArrow(DrawingContext ctx, Pen pen,
        Point from, Point to, double headLen)
    {
        var dir = to - from;
        double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len < 0.001) return;
        var unit = new Vector(dir.X / len, dir.Y / len);
        var perp  = new Vector(-unit.Y, unit.X);

        double hw = headLen * 0.35;
        var tip   = to;
        var base1 = tip - unit * headLen + perp * hw;
        var base2 = tip - unit * headLen - perp * hw;

        var geo = new StreamGeometry();
        using (var sgc = geo.Open())
        {
            sgc.BeginFigure(tip,   true, true);
            sgc.LineTo(base1, true, false);
            sgc.LineTo(base2, true, false);
        }
        geo.Freeze();
        ctx.DrawGeometry(pen.Brush, null, geo);
    }

    private static BitmapSource ToBitmap(DrawingVisual visual, int size)
    {
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
}
