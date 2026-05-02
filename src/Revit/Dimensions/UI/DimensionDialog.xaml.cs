using System.Windows;
using System.Windows.Controls;
using EllahColNum.Core.Dimensions.Models;

namespace EllahColNum.Revit.Dimensions.UI;

public partial class DimensionDialog : Window
{
    private readonly List<GridLineData>                          _grids;
    private readonly Dictionary<string, List<(long Id, string Name)>> _viewsByDiscipline;
    private readonly List<(long Id, string Name)>               _dimTypes;
    private bool _suppressRefresh;

    /// <summary>Set on Apply — contains the user's choices ready for the command to consume.</summary>
    public DimensionOptions? Result { get; private set; }

    public DimensionDialog(
        List<GridLineData>                               grids,
        Dictionary<string, List<(long Id, string Name)>> viewsByDiscipline,
        List<(long Id, string Name)>                     dimTypes)
    {
        _suppressRefresh   = true;
        _grids             = grids;
        _viewsByDiscipline = viewsByDiscipline;
        _dimTypes          = dimTypes;

        InitializeComponent();

        // Stat badges
        TxtVerticalCount.Text   = grids.Count(g => g.IsVertical).ToString();
        TxtHorizontalCount.Text = grids.Count(g => !g.IsVertical).ToString();

        PopulateDisciplineFilter();
        PopulateDimStyles();

        _suppressRefresh = false;
        UpdateSummary();
    }

    // ── Initialisation ───────────────────────────────────────────────────────

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

    private void PopulateDimStyles()
    {
        CmbDimStyle.Items.Clear();
        CmbDimStyle.Items.Add(new ComboBoxItem { Tag = "", Content = "(project default)" });

        foreach (var (id, name) in _dimTypes)
            CmbDimStyle.Items.Add(new ComboBoxItem { Tag = id.ToString(), Content = name });

        CmbDimStyle.SelectedIndex = 0;
    }

    // ── Summary ───────────────────────────────────────────────────────────────

    private void UpdateSummary()
    {
        if (TxtSummary == null) return;
        var selected  = GetSelectedViewIds().Count;
        var vGrids    = _grids.Count(g => g.IsVertical);
        var hGrids    = _grids.Count(g => !g.IsVertical);
        var dimVert   = ChkVertical?.IsChecked   == true;
        var dimHoriz  = ChkHorizontal?.IsChecked == true;

        var dirParts = new List<string>();
        if (dimVert  && vGrids >= 2) dirParts.Add($"{vGrids} vertical");
        if (dimHoriz && hGrids >= 2) dirParts.Add($"{hGrids} horizontal");
        var dirNote = dirParts.Count > 0 ? string.Join(" + ", dirParts) + " grid lines" : "no grids to dimension";

        TxtSummary.Text = $"{selected} view(s) selected  ·  {dirNote}";
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

    private DimensionOptions BuildOptions()
    {
        double offsetMeters = 1.0;
        if (double.TryParse(
                TxtOffset.Text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var raw))
            offsetMeters = Math.Max(0.1, raw);

        double offsetFeet = offsetMeters / 0.3048;

        var dimTypeTag  = (CmbDimStyle.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        var dimTypeName = dimTypeTag.Length > 0
            ? _dimTypes.FirstOrDefault(t => t.Id.ToString() == dimTypeTag).Name ?? ""
            : "";

        return new DimensionOptions
        {
            SelectedViewIds         = GetSelectedViewIds(),
            DimensionTypeName       = dimTypeName,
            OffsetFromGridFeet      = offsetFeet,
            DimensionVerticalGrids  = ChkVertical.IsChecked   == true,
            DimensionHorizontalGrids = ChkHorizontal.IsChecked == true,
        };
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    private void CmbDiscipline_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRefresh) return;
        var discipline = (CmbDiscipline.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        RefreshViewList(discipline);
    }

    private void ViewCheck_Changed(object sender, RoutedEventArgs e) => UpdateSummary();

    private void ChkDirection_Changed(object sender, RoutedEventArgs e) => UpdateSummary();

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
                "ELLAH-ColNum Pro — Smart Dimensions",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!opts.DimensionVerticalGrids && !opts.DimensionHorizontalGrids)
        {
            MessageBox.Show(
                "Please check at least one grid direction (vertical or horizontal).",
                "ELLAH-ColNum Pro — Smart Dimensions",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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
