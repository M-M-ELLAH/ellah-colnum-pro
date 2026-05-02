using System.Windows;
using System.Windows.Controls;
using EllahColNum.Core.Dimensions.Models;

namespace EllahColNum.Revit.Dimensions.UI;

/// <summary>
/// Code-behind for ProDimensionDialog.xaml.
/// Manages four layer rows (Grid, Columns, Walls, Openings), each with:
///   • enable/disable checkbox
///   • DimensionType dropdown
///   • numeric offset field (metres)
/// Plus a discipline-filtered view selector shared with Phase 1.
/// </summary>
public partial class ProDimensionDialog : Window
{
    private readonly List<ElementRefData>                             _elements;
    private readonly Dictionary<string, List<(long Id, string Name)>> _viewsByDiscipline;
    private readonly List<(long Id, string Name)>                     _dimTypes;
    private bool _suppressRefresh;

    /// <summary>Set on Apply — ready for the command to consume.</summary>
    public ProDimensionOptions? Result { get; private set; }

    public ProDimensionDialog(
        List<ElementRefData>                               elements,
        Dictionary<string, List<(long Id, string Name)>>  viewsByDiscipline,
        List<(long Id, string Name)>                       dimTypes)
    {
        _suppressRefresh   = true;
        _elements          = elements;
        _viewsByDiscipline = viewsByDiscipline;
        _dimTypes          = dimTypes;

        InitializeComponent();

        // Element count badges
        TxtColCount.Text     = elements.Count(e => e.Category == ElementCategory.Column).ToString();
        TxtWallCount.Text    = elements.Count(e => e.Category == ElementCategory.Wall).ToString();
        TxtOpeningCount.Text = elements.Count(e => e.Category == ElementCategory.Opening).ToString();
        TxtGridCount.Text    = elements.Count(e => e.Category == ElementCategory.Grid).ToString();

        PopulateDimTypeDropdowns();
        PopulateDisciplineFilter();

        _suppressRefresh = false;
        UpdateSummary();
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    private void PopulateDimTypeDropdowns()
    {
        foreach (var cmb in new[] { CmbTypeGrid, CmbTypeColumns, CmbTypeWalls, CmbTypeOpenings })
        {
            cmb.Items.Clear();
            cmb.Items.Add(new ComboBoxItem { Tag = "", Content = "(project default)" });

            foreach (var (id, name) in _dimTypes)
                cmb.Items.Add(new ComboBoxItem { Tag = id.ToString(), Content = name });

            cmb.SelectedIndex = 0;
        }
    }

    private void PopulateDisciplineFilter()
    {
        CmbDiscipline.Items.Clear();
        CmbDiscipline.Items.Add(new ComboBoxItem { Tag = "All", Content = "All disciplines" });

        foreach (var d in _viewsByDiscipline.Keys
            .Where(k => !k.Equals("All", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k))
        {
            CmbDiscipline.Items.Add(new ComboBoxItem { Tag = d, Content = d });
        }

        CmbDiscipline.SelectedIndex = 0;
        RefreshViewList("All");
    }

    private void RefreshViewList(string discipline)
    {
        var views = _viewsByDiscipline.TryGetValue(discipline, out var v) ? v
                  : _viewsByDiscipline.TryGetValue("All",      out var a) ? a
                  : new List<(long, string)>();

        ViewListBox.Items.Clear();

        foreach (var (id, name) in views)
        {
            var cb = new CheckBox
            {
                Content   = name,
                Tag       = id,
                Style     = (Style)Resources["ViewCheckBox"],
                IsChecked = false,
            };
            cb.Checked   += ViewCheck_Changed;
            cb.Unchecked += ViewCheck_Changed;
            ViewListBox.Items.Add(new ListBoxItem { Content = cb });
        }

        if (views.Count == 0)
        {
            ViewListBox.Items.Add(new ListBoxItem
            {
                Content = new TextBlock
                {
                    Text       = "No plan views found for this discipline.",
                    Foreground = System.Windows.Media.Brushes.DimGray,
                    FontSize   = 12,
                    Margin     = new Thickness(4, 8, 0, 0),
                },
                IsEnabled = false,
            });
        }

        UpdateSummary();
    }

    // ── Summary ───────────────────────────────────────────────────────────────

    private void UpdateSummary()
    {
        if (TxtSummary == null) return;

        int views   = GetSelectedViewIds().Count;
        var active  = new List<string>();
        if (ChkLayerGrid?.IsChecked     == true) active.Add("grids");
        if (ChkLayerColumns?.IsChecked  == true) active.Add("columns");
        if (ChkLayerWalls?.IsChecked    == true) active.Add("walls");
        if (ChkLayerOpenings?.IsChecked == true) active.Add("openings");

        string layers = active.Count > 0 ? string.Join(" + ", active) : "no layers selected";
        TxtSummary.Text = $"{views} view(s) selected  ·  layers: {layers}";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<long> GetSelectedViewIds()
    {
        var ids = new List<long>();
        foreach (ListBoxItem lbi in ViewListBox.Items)
            if (lbi.Content is CheckBox cb && cb.IsChecked == true && cb.Tag is long id)
                ids.Add(id);
        return ids;
    }

    private static double ParseMetres(TextBox tb, double fallback)
    {
        if (double.TryParse(
                tb.Text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var v))
            return Math.Max(0.1, v);
        return fallback;
    }

    private static double ToFeet(double metres) => metres / 0.3048;

    private string ResolveDimTypeName(ComboBox cmb)
    {
        var tag = (cmb.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(tag)) return "";
        return _dimTypes.FirstOrDefault(t => t.Id.ToString() == tag).Name ?? "";
    }

    private ProDimensionOptions BuildOptions()
    {
        return new ProDimensionOptions
        {
            SelectedViewIds = GetSelectedViewIds(),

            DimGrids    = ChkLayerGrid.IsChecked     == true,
            DimColumns  = ChkLayerColumns.IsChecked  == true,
            DimWalls    = ChkLayerWalls.IsChecked     == true,
            DimOpenings = ChkLayerOpenings.IsChecked  == true,

            GridDimTypeName    = ResolveDimTypeName(CmbTypeGrid),
            ColumnDimTypeName  = ResolveDimTypeName(CmbTypeColumns),
            WallDimTypeName    = ResolveDimTypeName(CmbTypeWalls),
            OpeningDimTypeName = ResolveDimTypeName(CmbTypeOpenings),

            GridOffsetFeet    = ToFeet(ParseMetres(TxtOffsetGrid,    3.0)),
            ColumnOffsetFeet  = ToFeet(ParseMetres(TxtOffsetColumns, 2.0)),
            WallOffsetFeet    = ToFeet(ParseMetres(TxtOffsetWalls,   1.0)),
            OpeningOffsetFeet = ToFeet(ParseMetres(TxtOffsetOpenings,0.5)),
        };
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void CmbDiscipline_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRefresh) return;
        var discipline = (CmbDiscipline.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        RefreshViewList(discipline);
    }

    private void LayerToggle_Changed(object sender, RoutedEventArgs e) => UpdateSummary();
    private void ViewCheck_Changed(object sender, RoutedEventArgs e)   => UpdateSummary();

    private void SelectAllViews_Click(object sender, RoutedEventArgs e)
    {
        foreach (ListBoxItem lbi in ViewListBox.Items)
            if (lbi.Content is CheckBox cb) cb.IsChecked = true;
    }

    private void ClearAllViews_Click(object sender, RoutedEventArgs e)
    {
        foreach (ListBoxItem lbi in ViewListBox.Items)
            if (lbi.Content is CheckBox cb) cb.IsChecked = false;
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        var opts = BuildOptions();

        if (opts.SelectedViewIds.Count == 0)
        {
            MessageBox.Show(
                "Please select at least one view before applying.",
                "ELLAH-ColNum Pro — Pro Dimensions",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!opts.DimGrids && !opts.DimColumns && !opts.DimWalls && !opts.DimOpenings)
        {
            MessageBox.Show(
                "Please enable at least one dimension layer.",
                "ELLAH-ColNum Pro — Pro Dimensions",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result       = opts;
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
