using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EllahColNum.Core.Models;
using EllahColNum.Core.Services;

namespace EllahColNum.Revit.UI;

public partial class NumberingDialog : Window
{
    private readonly List<ColumnGroup> _groups;
    private bool _suppressRefresh;

    // Metadata passed from the command
    private readonly Dictionary<string, List<string>>   _levelsByDiscipline;
    private readonly Dictionary<string, double>         _levelElevations;
    private readonly Dictionary<string, List<string>>   _familiesByCategory;

    /// <summary>
    /// Per-floor structural column ElementId sets, populated lazily on first access.
    /// Key = level name; Value = ElementIds visible in that floor's best plan view.
    /// </summary>
    private readonly Dictionary<string, HashSet<long>>  _columnIdsByFloor = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-floor diagnostic record from the last view-lookup attempt — used to
    /// build an informative summary line when a floor unexpectedly yields zero
    /// columns.  Populated alongside <see cref="_columnIdsByFloor"/>.
    /// </summary>
    private readonly Dictionary<string, EllahColNum.Revit.Helpers.FloorViewLookupResult>
        _floorDiagnosticByFloor = new(StringComparer.OrdinalIgnoreCase);

    // UIDocument for click-to-highlight
    private readonly Autodesk.Revit.UI.UIDocument? _uidoc;

    // Choose Your Way state
    private long?   _anchorElementId;
    private string? _anchorFloor;
    private string? _anchorGridRef;
    private string? _anchorFamily;

    // Scope / Method state
    private bool _isSpecificFloor    = false;   // false = Full Project
    private bool _isChooseYourWayMode = false;  // false = Smart Numbering

    /// <summary>
    /// Building's primary axis rotation (degrees, normalised to [-45°, +45°]),
    /// detected once by the Revit command at start-up and carried through every
    /// <see cref="BuildOptions"/> call so the preview engine and the final write
    /// both see the same value.  For multi-zone buildings this is left at 0
    /// because each column is rotated by its own zone via <see cref="_perColumnRotation"/>.
    /// </summary>
    private double _buildingRotationDegrees;

    /// <summary>
    /// Per-column rotation map for buildings whose grid system is split across
    /// multiple orientations.  Null on uniform projects.  Carried verbatim
    /// through every <see cref="BuildOptions"/> call.
    /// </summary>
    private Dictionary<long, double>? _perColumnRotation;

    /// <summary>
    /// Per-column zone-id map paired with <see cref="_perColumnRotation"/>.
    /// Null on uniform projects.
    /// </summary>
    private Dictionary<long, int>? _perColumnZone;

    public bool             PickColumnRequested { get; private set; }

    /// <summary>
    /// Set when the user chose "Edit Column Marks" from the bottom bar.
    /// The hosting command should close the dialog and enter the
    /// pick-and-edit loop instead of running the numbering engine.
    /// </summary>
    public bool             EditMarksRequested  { get; private set; }
    public NumberingOptions CurrentOptions      => BuildOptions();
    public NumberingOptions? Result             { get; private set; }

    public NumberingDialog(
        List<ColumnGroup>                  groups,
        NumberingOptions                   defaults,
        int                                totalElements,
        Dictionary<string, List<string>>   levelsByDiscipline,
        Dictionary<string, double>         levelElevations,
        Dictionary<string, List<string>>   familiesByCategory,
        List<string>                       paramNames,
        Autodesk.Revit.UI.UIDocument?      uidoc           = null,
        long?                              anchorElementId  = null,
        string?                            anchorFloor      = null,
        string?                            anchorGridRef    = null,
        string?                            anchorFamily     = null,
        NumberingOptions?                  savedOptions     = null)
    {
        _suppressRefresh    = true;
        _groups             = groups;
        _levelsByDiscipline = levelsByDiscipline;
        _levelElevations    = levelElevations;
        _familiesByCategory = familiesByCategory;
        _uidoc              = uidoc;
        _anchorElementId    = anchorElementId;
        _anchorFloor        = anchorFloor;
        _anchorGridRef      = anchorGridRef;
        _anchorFamily       = anchorFamily;

        // Building-axis rotation: carry whatever the caller (the Revit command)
        // detected.  Saved options take precedence so user reruns stay stable.
        var axisSource = savedOptions ?? defaults;
        _buildingRotationDegrees = axisSource.BuildingRotationDegrees;
        _perColumnRotation       = axisSource.ColumnRotationByElementId;
        _perColumnZone           = axisSource.ColumnZoneByElementId;

        InitializeComponent();

        TxtPositionCount.Text = groups.Count.ToString();
        TxtElementCount.Text  = totalElements.ToString();

        PopulateDisciplineFilter();       // also calls RefreshChooseWayFloorList + SpecificFloorList
        PopulateFamilyCategoryFilter();
        PopulateTargetParams(paramNames);

        // Restore previous or default settings
        if (savedOptions != null)
            PopulateSettings(savedOptions);
        else
            PopulateSettings(defaults);

        // If reopening after a column pick → switch to CHOOSE YOUR WAY and show anchor info
        if (_anchorElementId.HasValue)
        {
            _isChooseYourWayMode = true;
            AnchorInfoBorder.Visibility = Visibility.Visible;
            TxtAnchorInfo.Text = $"{_anchorFamily}  |  {_anchorGridRef}  |  Floor: {_anchorFloor}";
            if (!string.IsNullOrWhiteSpace(_anchorFloor))
                SelectComboByTag(CmbChooseWayFloor, _anchorFloor);
        }

        // Apply initial button styles and panel visibility
        UpdateScopeUI();
        UpdateModeUI();

        _suppressRefresh = false;
        ApplyAutoToleranceIfAvailable();
        RefreshPreview();
    }

    // ── Scope / Mode toggle UI ────────────────────────────────────────────────

    private void UpdateScopeUI()
    {
        BtnFullProject.Style    = _isSpecificFloor
            ? (Style)Resources["SegBtnInactive"]
            : (Style)Resources["SegBtnActive"];

        BtnSpecificFloor.Style  = _isSpecificFloor
            ? (Style)Resources["SegBtnActive"]
            : (Style)Resources["SegBtnInactive"];

        CmbSpecificFloor.Visibility = _isSpecificFloor
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateModeUI()
    {
        BtnSmartNumbering.Style = _isChooseYourWayMode
            ? (Style)Resources["SegBtnInactive"]
            : (Style)Resources["SegBtnActive"];

        BtnChooseYourWay.Style  = _isChooseYourWayMode
            ? (Style)Resources["SegBtnActive"]
            : (Style)Resources["SegBtnInactive"];

        PanelSmartNumbering.Visibility = _isChooseYourWayMode
            ? Visibility.Collapsed
            : Visibility.Visible;

        PanelChooseYourWay.Visibility = _isChooseYourWayMode
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ── Initialisation ──────────────────────────────────────────────────────

    private void PopulateDisciplineFilter()
    {
        CmbFloorDiscipline.Items.Clear();
        CmbFloorDiscipline.Items.Add(new ComboBoxItem { Tag = "All", Content = "All disciplines" });

        foreach (var discipline in _levelsByDiscipline.Keys
            .Where(k => !k.Equals("All", StringComparison.OrdinalIgnoreCase))
            .OrderBy(k => k))
        {
            CmbFloorDiscipline.Items.Add(new ComboBoxItem
                { Tag = discipline, Content = discipline });
        }
        CmbFloorDiscipline.SelectedIndex = 0;
        RefreshReferenceFloorList("All");
        RefreshChooseWayFloorList("All");
        PopulateSpecificFloorList();
    }

    /// <summary>
    /// Carries a floor's name and its elevation (Revit internal feet) together so that
    /// the elevation is always available without any string-matching dictionary lookup.
    /// ToString() returns Name so that SelectComboByTag and BuildOptions still work.
    /// </summary>
    private sealed record FloorItem(string Name, double ElevFeet)
    {
        public override string ToString() => Name;
    }

    private void PopulateSpecificFloorList()
    {
        var levels = _levelsByDiscipline.TryGetValue("All", out var l) ? l : [];

        // Elevations sorted ascending — same order as the "All" level list
        // (both originate from LevelsByElevation() sorted by elevation).
        // Used as a positional fallback when TryGetValue fails due to Hebrew
        // encoding differences between the two separate Level.Name query calls.
        var sortedElevs = _levelElevations.Values.OrderBy(v => v).ToList();

        CmbSpecificFloor.Items.Clear();
        CmbSpecificFloor.Items.Add(new ComboBoxItem
        {
            Tag     = new FloorItem("", 0),
            Content = "(select a floor)"
        });

        for (int i = 0; i < levels.Count; i++)
        {
            var name = levels[i];
            // Primary: name-based lookup
            if (!_levelElevations.TryGetValue(name, out var elev) || elev == 0)
            {
                // Positional fallback: index i in the sorted "All" list corresponds to
                // index i in the sorted elevations list — same source, same order.
                elev = i < sortedElevs.Count ? sortedElevs[i] : 0.0;
            }
            CmbSpecificFloor.Items.Add(new ComboBoxItem
            {
                Tag     = new FloorItem(name, elev),
                Content = name
            });
        }
        CmbSpecificFloor.SelectedIndex = 0;
    }

    private void RefreshChooseWayFloorList(string discipline)
    {
        var levels = _levelsByDiscipline.TryGetValue(discipline, out var l) ? l
                   : _levelsByDiscipline.TryGetValue("All",      out var a) ? a
                   : [];

        var previousTag = (CmbChooseWayFloor.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

        CmbChooseWayFloor.Items.Clear();
        CmbChooseWayFloor.Items.Add(new ComboBoxItem
            { Tag = "", Content = "(auto — from picked column)" });
        foreach (var name in levels)
            CmbChooseWayFloor.Items.Add(new ComboBoxItem { Tag = name, Content = name });

        var restoreTag = !string.IsNullOrWhiteSpace(_anchorFloor) ? _anchorFloor : previousTag;
        SelectComboByTag(CmbChooseWayFloor, restoreTag);
    }

    private void RefreshReferenceFloorList(string discipline)
    {
        var levels = _levelsByDiscipline.TryGetValue(discipline, out var l) ? l
                   : _levelsByDiscipline.TryGetValue("All",       out var a) ? a
                   : [];

        var previousTag = (CmbReferenceFloor.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

        CmbReferenceFloor.Items.Clear();
        CmbReferenceFloor.Items.Add(new ComboBoxItem
            { Tag = "", Content = "(all floors — default)" });
        foreach (var name in levels)
            CmbReferenceFloor.Items.Add(new ComboBoxItem { Tag = name, Content = name });

        SelectComboByTag(CmbReferenceFloor, previousTag);
    }

    private void PopulateFamilyCategoryFilter()
    {
        CmbFamilyCategory.Items.Clear();
        foreach (var cat in _familiesByCategory.Keys.OrderBy(k => k))
            CmbFamilyCategory.Items.Add(new ComboBoxItem { Tag = cat, Content = cat });

        if (CmbFamilyCategory.Items.Count == 0)
            CmbFamilyCategory.Items.Add(new ComboBoxItem
                { Tag = "Structural Columns", Content = "Structural Columns" });

        CmbFamilyCategory.SelectedIndex = 0;
        RefreshFamilyList(SelectedCategoryName());
    }

    private void RefreshFamilyList(string categoryName)
    {
        var families = _familiesByCategory.TryGetValue(categoryName, out var f) ? f : [];

        FamilyListBox.Items.Clear();
        foreach (var name in families)
        {
            var cb = new CheckBox
            {
                Content   = name,
                Style     = (Style)Resources["FamilyCheckBox"],
                IsChecked = false,
            };
            cb.Checked   += FamilyCheck_Changed;
            cb.Unchecked += FamilyCheck_Changed;
            FamilyListBox.Items.Add(new ListBoxItem { Content = cb });
        }
        UpdateFamilyCountLabel();
    }

    private void PopulateTargetParams(List<string> paramNames)
    {
        CmbTargetParam.Items.Clear();
        CmbTargetParam.Items.Add(new ComboBoxItem { Tag = "", Content = "Mark (default)" });
        foreach (var name in paramNames.Where(n => n != "Mark"))
            CmbTargetParam.Items.Add(new ComboBoxItem { Tag = name, Content = name });
        CmbTargetParam.SelectedIndex = 0;

        SelectComboByTag(CmbToleranceUnit, "cm");
        TxtTolerance.Text = "5";
        SelectComboByTag(CmbRowToleranceUnit, "cm");
        TxtRowTolerance.Text = "50";
        UpdateToleranceHint();
    }

    private void PopulateSettings(NumberingOptions o)
    {
        SelectComboByTag(CmbMode, o.ContinuationMode switch
        {
            ContinuationMode.Override => "Override",
            ContinuationMode.AddOnly  => "AddOnly",
            _                         => "SmartContinue",
        });

        TxtPrefix.Text      = o.Prefix;
        TxtSuffix.Text      = o.Suffix;
        TxtStartNumber.Text = o.StartNumber.ToString();

        var sortTag = o.SortBy switch
        {
            SortDirection.LeftToRight    => "LeftToRight",
            SortDirection.RightToLeft    => "RightToLeft",
            SortDirection.BottomToTop    => "BottomToTop",
            SortDirection.TopToBottom    => "TopToBottom",
            SortDirection.RightToLeftUp  => "RightToLeftUp",
            SortDirection.TopBottomLeft  => "TopBottomLeft",
            SortDirection.BottomTopLeft  => "BottomTopLeft",
            _                            => "TopLeftToRight",
        };
        SelectComboByTag(CmbSort,          sortTag);
        SelectComboByTag(CmbChooseWaySort, sortTag);

        if (!string.IsNullOrWhiteSpace(o.ReferenceFloorName))
            SelectComboByTag(CmbReferenceFloor, o.ReferenceFloorName);

        if (!string.IsNullOrWhiteSpace(o.TargetParameterName))
            SelectComboByTag(CmbTargetParam, o.TargetParameterName);

        if (!string.IsNullOrWhiteSpace(o.TargetCategoryName))
            SelectComboByTag(CmbFamilyCategory, o.TargetCategoryName);

        TxtTolerance.Text = (o.PositionToleranceFeet * 30.48).ToString("F0");
        SelectComboByTag(CmbToleranceUnit, "cm");

        TxtRowTolerance.Text = (o.RowToleranceFeet * 30.48).ToString("F0");
        SelectComboByTag(CmbRowToleranceUnit, "cm");

        // Restore specific floor if previously selected
        if (!string.IsNullOrWhiteSpace(o.SpecificFloorName))
        {
            _isSpecificFloor = true;
            SelectComboByTag(CmbSpecificFloor, o.SpecificFloorName);
        }

        UpdateToleranceHint();
    }

    private static void SelectComboByTag(ComboBox cmb, string tag)
    {
        foreach (ComboBoxItem item in cmb.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                cmb.SelectedItem = item;
                return;
            }
        }
        if (cmb.Items.Count > 0) cmb.SelectedIndex = 0;
    }

    // ── Lazy floor-IDs helper ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the set of structural column ElementIds visible in the best floor-plan view
    /// for <paramref name="floorName"/>.  Computes and caches on first access.
    ///
    /// Passes the floor's elevation (when known) to the locator so it can fall back from
    /// name match to elevation match — important for Hebrew level names whose underlying
    /// strings may carry invisible BiDi marks that break ordinary string equality.
    ///
    /// Returns an empty set when no candidate view yields any column (caller should then
    /// fall back to elevation/span-through filtering on the full column list).
    /// </summary>
    private HashSet<long> GetOrComputeFloorIds(string floorName)
    {
        if (_columnIdsByFloor.TryGetValue(floorName, out var cached)) return cached;
        if (_uidoc?.Document == null) return [];

        try
        {
            var elev = SafeFloorElevation(floorName);
            var collector = new EllahColNum.Revit.Collectors.RevitColumnCollector(_uidoc.Document);
            var diag      = collector.LocateColumnsForFloor(floorName, elev);
            _columnIdsByFloor[floorName]      = diag.ColumnIds;
            _floorDiagnosticByFloor[floorName] = diag;
            return diag.ColumnIds;
        }
        catch
        {
            _columnIdsByFloor[floorName] = [];
            return [];
        }
    }

    // ── Live preview ────────────────────────────────────────────────────────

    private void RefreshPreview()
    {
        if (_suppressRefresh) return;

        var options = BuildOptions();
        var engine  = new NumberingEngine(options);

        var activeGroups  = _groups;
        int spanOnlyCount = 0;

        if (!string.IsNullOrWhiteSpace(options.SpecificFloorName))
        {
            var floorName = options.SpecificFloorName;

            // ── Primary: Revit view-based filter (lazy, cached) ───────────────────────
            // Asks Revit which columns are VISIBLE in the floor's best plan view.
            // Handles multi-story, in-place, and any other column Revit shows — no guessing.
            var floorIds = GetOrComputeFloorIds(floorName);

            if (floorIds.Count > 0)
            {
                var floorItem = (CmbSpecificFloor.SelectedItem as ComboBoxItem)?.Tag as FloorItem;
                var floorElev = floorItem?.ElevFeet ?? 0.0;
                if (floorElev == 0) floorElev = FindFloorElevation(floorName);

                const double tol = 0.1;
                activeGroups = _groups
                    .Where(g => g.Columns.Any(c =>
                        floorIds.Contains(c.ElementId) &&
                        (EllahColNum.Core.Text.BidiText.EqualsIgnoreBidi(c.TopLevelName, floorName) ||
                         (!double.IsNaN(floorElev) && floorElev > 0 && Math.Abs(c.TopLevelElevation - floorElev) <= tol) ||
                         (!double.IsNaN(floorElev) && floorElev > 0 && c.BaseLevelElevation < floorElev - tol &&
                                                                        c.TopLevelElevation  > floorElev + tol))))
                    .ToList();
            }
            else
            {
                // ── Fallback: elevation / name / span-through filter ──────────────────
                // Used when no floor-plan view exists for the level.
                // FloorItem.ElevFeet is stored at ComboBox-creation time and is immune to
                // Hebrew string encoding mismatches in dictionary lookups.
                var floorItem = (CmbSpecificFloor.SelectedItem as ComboBoxItem)?.Tag as FloorItem;
                var floorElev = floorItem?.ElevFeet ?? 0.0;
                if (floorElev == 0) floorElev = FindFloorElevation(floorName);

                const double tol = 0.1;
                activeGroups = _groups.Where(g => g.Columns.Any(c =>
                    EllahColNum.Core.Text.BidiText.EqualsIgnoreBidi(c.BaseLevelName, floorName) ||
                    EllahColNum.Core.Text.BidiText.EqualsIgnoreBidi(c.TopLevelName,  floorName) ||
                    (!double.IsNaN(floorElev) && floorElev > 0 && Math.Abs(c.BaseLevelElevation - floorElev) <= tol) ||
                    (!double.IsNaN(floorElev) && floorElev > 0 && Math.Abs(c.TopLevelElevation  - floorElev) <= tol) ||
                    // Span-through: multi-story column that physically passes through this floor.
                    (!double.IsNaN(floorElev) && floorElev > 0 &&
                     c.BaseLevelElevation < floorElev - tol &&
                     c.TopLevelElevation  > floorElev + tol)
                )).ToList();
            }
            spanOnlyCount = 0;
        }

        var result = engine.AssignMarks(activeGroups);
        var ana    = result.Analysis;

        TxtBadgeNew.Text      = $"{ana.NotNumberedCount} new";
        TxtBadgeKeep.Text     = $"{ana.FullyNumberedCount} kept";
        TxtBadgeComplete.Text = $"{ana.PartiallyNumberedCount} completed";
        TxtBadgeConflict.Text = $"{ana.ConflictingCount} conflicts";

        BadgeComplete.Visibility = ana.PartiallyNumberedCount > 0
            ? Visibility.Visible : Visibility.Collapsed;
        BadgeConflict.Visibility = ana.ConflictingCount > 0
            ? Visibility.Visible : Visibility.Collapsed;

        if (ana.ConflictingCount > 0)
        {
            TxtConflictWarning.Text =
                $"⚠  {ana.ConflictingCount} column position(s) have conflicting marks " +
                "(different floors carry different Mark values). " +
                "The most common mark will be kept for each.";
            ConflictWarning.Visibility = Visibility.Visible;
        }
        else
        {
            ConflictWarning.Visibility = Visibility.Collapsed;
        }

        var familyFilter = GetCheckedFamilies();
        var filterNote   = familyFilter.Count > 0
            ? $"  ·  {familyFilter.Count} famil{(familyFilter.Count == 1 ? "y" : "ies")} selected"
            : "";
        // Building-axis note: only mentioned when something interesting is
        // happening — keeps the summary line clean for the orthogonal common
        // case.  Reports two separate states:
        //   • Multi-zone: how many distinct zones we found and their angles.
        //   • Single non-trivial rotation: the detected angle.
        string axisNote = "";
        if (_perColumnRotation != null && _perColumnRotation.Count > 0)
        {
            // Distinct rotations carried in the per-column map = # of zones.
            var rotations = _perColumnRotation.Values
                .Select(r => Math.Round(r, 1))
                .Distinct()
                .OrderBy(r => r)
                .ToList();
            if (rotations.Count >= 2)
            {
                var listed = string.Join(" / ",
                    rotations.Select(r => $"{r:+0.0;-0.0;0}°"));
                axisNote = $"  ·  {rotations.Count} zones: {listed}";
            }
        }
        else if (Math.Abs(_buildingRotationDegrees) >= 1.0)
        {
            axisNote = $"  ·  building axis: {_buildingRotationDegrees:+0.0;-0.0}°";
        }

        var scopeNote = "";
        if (!string.IsNullOrWhiteSpace(options.SpecificFloorName))
        {
            int willBeWritten = result.Groups.Count - spanOnlyCount;
            scopeNote = spanOnlyCount > 0
                ? $"  ·  floor: {options.SpecificFloorName}  ({willBeWritten} will be numbered, {spanOnlyCount} shown only)"
                : $"  ·  floor: {options.SpecificFloorName}";
        }
        TxtSummary.Text =
            $"{result.Groups.Count} positions  ·  " +
            $"{ana.NotNumberedCount} new  ·  " +
            $"{ana.FullyNumberedCount + ana.PartiallyNumberedCount} kept" +
            scopeNote + filterNote + axisNote;

        // Diagnostic surface: when a SpecificFloor pick yields zero columns we
        // append what the view locator actually found.  This makes silent
        // mismatches (Hebrew encoding, missing structural plan, hidden by
        // template, etc.) immediately visible to the engineer instead of
        // showing an empty preview with no explanation.
        if (!string.IsNullOrWhiteSpace(options.SpecificFloorName) &&
            result.Groups.Count == 0 &&
            _floorDiagnosticByFloor.TryGetValue(options.SpecificFloorName!, out var diag))
        {
            string detail =
                diag.MatchedLevelCount   == 0 ? "no Level matches that name or elevation"
              : diag.CandidateViewCount  == 0 ? $"{diag.MatchedLevelCount} level(s) found but no plan view points at them"
              : diag.ColumnIds.Count     == 0 ? $"{diag.CandidateViewCount} candidate view(s) — none contain visible structural columns"
              :                                  $"chosen view: {diag.ChosenViewName} ({diag.ChosenViewDiscipline}) — but elevation/span filter did not match any column";

            TxtSummary.Text += $"   ·   diagnostic: {detail}";
        }

        PreviewGrid.ItemsSource = result.Groups.Select((g, i) => new ColumnPreviewRow
        {
            Sequence       = i + 1,
            GridRef        = BuildGridRef(g),
            FamilyName     = g.Columns.Count > 0 ? g.Columns[0].FamilyName : "",
            FloorCount     = g.FloorCount,
            CurrentMark    = string.IsNullOrWhiteSpace(g.ExistingMark) ? "—" : g.ExistingMark,
            NewMark        = string.IsNullOrWhiteSpace(g.AssignedMark) ? "—" : g.AssignedMark,
            Status         = g.NumberingStatus,
            FirstElementId = g.Columns.Count > 0 ? g.Columns[0].ElementId : 0,
        }).ToList();
    }

    /// <summary>
    /// Derives the elevation (Revit internal feet) for a named floor level.
    /// All comparisons go through <see cref="EllahColNum.Core.Text.BidiText"/>
    /// so Hebrew level names with invisible RTL markers still match.
    ///
    /// Resolution order (most-reliable first):
    ///   1. BaseLevelElevation of any column whose base is this floor.
    ///   2. TopLevelElevation of any column whose top is this floor.
    ///   3. Direct lookup in the level-elevation dictionary.
    ///   4. BiDi-tolerant scan of the dictionary (rescues the case where the
    ///      dictionary key differs from <paramref name="floorName"/> only by
    ///      invisible BiDi marks).
    /// Returns <see cref="double.NaN"/> when nothing matches.
    /// </summary>
    private double FindFloorElevation(string floorName)
    {
        var baseMatch = _groups.SelectMany(g => g.Columns)
            .FirstOrDefault(c =>
                EllahColNum.Core.Text.BidiText.EqualsIgnoreBidi(c.BaseLevelName, floorName));
        if (baseMatch != null) return baseMatch.BaseLevelElevation;

        var topMatch = _groups.SelectMany(g => g.Columns)
            .FirstOrDefault(c =>
                EllahColNum.Core.Text.BidiText.EqualsIgnoreBidi(c.TopLevelName, floorName));
        if (topMatch != null) return topMatch.TopLevelElevation;

        if (_levelElevations.TryGetValue(floorName, out var direct) && direct > 0)
            return direct;

        foreach (var kvp in _levelElevations)
        {
            if (EllahColNum.Core.Text.BidiText.EqualsIgnoreBidi(kvp.Key, floorName))
                return kvp.Value;
        }

        return double.NaN;
    }

    private static string BuildGridRef(ColumnGroup g)
    {
        if (!string.IsNullOrWhiteSpace(g.GridRow) && !string.IsNullOrWhiteSpace(g.GridColumn))
            return $"{g.GridRow} – {g.GridColumn}";
        return $"X {g.X:F1}  Y {g.Y:F1}";
    }

    // ── Build options ─────────────────────────────────────────────────────────

    private NumberingOptions BuildOptions()
    {
        var sortTag = _isChooseYourWayMode
            ? (CmbChooseWaySort.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            : (CmbSort.SelectedItem          as ComboBoxItem)?.Tag?.ToString();

        // CHOOSE YOUR WAY always overrides — it numbers from scratch.
        // The CmbMode (Smart Continue / Add Only / Override) only applies in SMART NUMBERING.
        var mode = _isChooseYourWayMode
            ? ContinuationMode.Override
            : (CmbMode.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
            {
                "Override" => ContinuationMode.Override,
                "AddOnly"  => ContinuationMode.AddOnly,
                _          => ContinuationMode.SmartContinue,
            };

        var sort = sortTag switch
        {
            "LeftToRight"    => SortDirection.LeftToRight,
            "RightToLeft"    => SortDirection.RightToLeft,
            "BottomToTop"    => SortDirection.BottomToTop,
            "TopToBottom"    => SortDirection.TopToBottom,
            "RightToLeftUp"  => SortDirection.RightToLeftUp,
            "TopBottomLeft"  => SortDirection.TopBottomLeft,
            "BottomTopLeft"  => SortDirection.BottomTopLeft,
            _                => SortDirection.TopLeftToRight,
        };

        int.TryParse(TxtStartNumber.Text, out var startNum);
        if (startNum < 1) startNum = 1;

        var referenceFloor = _isChooseYourWayMode
            ? (CmbChooseWayFloor.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? ""
            : (CmbReferenceFloor.SelectedItem  as ComboBoxItem)?.Tag?.ToString() ?? "";

        // Resolve the reference floor's elevation so the engine can recognise
        // multi-story columns that pass through it.  Uses the same Hebrew-safe
        // fallback chain as the SpecificFloor preview path (column data first,
        // dictionary lookup last).
        double referenceFloorElev = 0;
        if (!string.IsNullOrWhiteSpace(referenceFloor))
        {
            var elev = FindFloorElevation(referenceFloor);
            if (!double.IsNaN(elev) && elev > 0) referenceFloorElev = elev;
        }

        // SPECIFIC FLOOR — pull both the name and the elevation from the
        // ComboBox tag (FloorItem record cached at populate-time).  Carrying
        // the elevation explicitly avoids a dictionary lookup downstream that
        // can fail when Hebrew level names carry BiDi marks.
        string? specificFloor      = null;
        double  specificFloorElev  = 0;
        if (_isSpecificFloor)
        {
            var floorItem = (CmbSpecificFloor.SelectedItem as ComboBoxItem)?.Tag as FloorItem;
            var name      = floorItem?.Name ?? "";
            if (!string.IsNullOrWhiteSpace(name))
            {
                specificFloor     = name;
                specificFloorElev = floorItem!.ElevFeet > 0
                    ? floorItem.ElevFeet
                    : SafeFloorElevation(name);
            }
        }

        return new NumberingOptions
        {
            ContinuationMode        = mode,
            Prefix                  = TxtPrefix.Text ?? "",
            Suffix                  = TxtSuffix.Text ?? "",
            StartNumber             = startNum,
            SortBy                  = sort,
            PositionToleranceFeet   = ParseToleranceInFeet(),
            RowToleranceFeet        = ParseRowToleranceInFeet(),
            ReferenceFloorName      = referenceFloor,
            ReferenceFloorElevation = referenceFloorElev,
            FamilyFilter            = GetCheckedFamilies(),
            TargetParameterName     = (CmbTargetParam.SelectedItem   as ComboBoxItem)?.Tag?.ToString() ?? "",
            TargetCategoryName      = SelectedCategoryName(),
            StartAnchorElementId    = _isChooseYourWayMode ? _anchorElementId : null,
            SpecificFloorName         = specificFloor,
            SpecificFloorElevation    = specificFloorElev,
            BuildingRotationDegrees   = _buildingRotationDegrees,
            ColumnRotationByElementId = _perColumnRotation,
            ColumnZoneByElementId     = _perColumnZone,
        };
    }

    /// <summary>
    /// Wraps <see cref="FindFloorElevation"/> with NaN/zero safety so callers can
    /// store the result directly in <see cref="NumberingOptions.SpecificFloorElevation"/>
    /// without further checking.
    /// </summary>
    private double SafeFloorElevation(string floorName)
    {
        var elev = FindFloorElevation(floorName);
        return double.IsNaN(elev) || elev <= 0 ? 0 : elev;
    }

    private string SelectedCategoryName() =>
        (CmbFamilyCategory.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Structural Columns";

    // ── Tolerance helpers ────────────────────────────────────────────────────

    private double ParseToleranceInFeet()
    {
        if (!double.TryParse(
                TxtTolerance.Text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var raw) || raw < 0)
            raw = 15;

        var unit = (CmbToleranceUnit.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cm";
        return unit switch
        {
            "mm" => raw / 304.8,
            "m"  => raw / 0.3048,
            "ft" => raw,
            _    => raw / 30.48,
        };
    }

    private double ParseRowToleranceInFeet()
    {
        if (!double.TryParse(
                TxtRowTolerance.Text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var raw) || raw < 0)
            raw = 50;

        var unit = (CmbRowToleranceUnit.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cm";
        return unit switch
        {
            "mm" => raw / 304.8,
            "m"  => raw / 0.3048,
            "ft" => raw,
            _    => raw / 30.48,
        };
    }

    /// <summary>
    /// Recomputes the suggested row-grouping tolerance from the model and
    /// applies it to <see cref="TxtRowTolerance"/>.  Triggered whenever a
    /// setting that affects the row-perpendicular axis changes (sort
    /// direction, reference floor, scope) so the user always sees the
    /// optimal value for the current configuration.  The user may still
    /// edit the value manually afterwards.
    /// </summary>
    private void ApplyAutoToleranceIfAvailable()
    {
        if (_suppressRefresh)            return;
        if (TxtRowTolerance     == null) return;
        if (TxtRowToleranceAutoHint == null) return;
        if (_groups == null || _groups.Count < 4)
        {
            TxtRowToleranceAutoHint.Text = "";
            return;
        }

        // Resolve the current sort direction from whichever combo is active
        // for the present mode (Smart vs. Choose-Your-Way).
        var sortTag = _isChooseYourWayMode
            ? (CmbChooseWaySort.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            : (CmbSort.SelectedItem          as ComboBoxItem)?.Tag?.ToString();

        var sort = sortTag switch
        {
            "LeftToRight"    => SortDirection.LeftToRight,
            "RightToLeft"    => SortDirection.RightToLeft,
            "BottomToTop"    => SortDirection.BottomToTop,
            "TopToBottom"    => SortDirection.TopToBottom,
            "RightToLeftUp"  => SortDirection.RightToLeftUp,
            "TopBottomLeft"  => SortDirection.TopBottomLeft,
            "BottomTopLeft"  => SortDirection.BottomTopLeft,
            _                => SortDirection.TopLeftToRight,
        };

        AutoDetectResult result;
        try
        {
            result = ToleranceAutoDetector.Detect(
                _groups,
                sort,
                _perColumnZone,
                _perColumnRotation);
        }
        catch
        {
            // Detector is purely diagnostic — never surface an error to the
            // user.  Keep whatever value the field currently holds.
            TxtRowToleranceAutoHint.Text = "";
            return;
        }

        if (result.SuggestedToleranceFeet is not double feet)
        {
            TxtRowToleranceAutoHint.Text = $"🪄 Auto-detect: {result.Reason}.";
            return;
        }

        // Display in centimetres regardless of unit selection — the suggestion
        // box is always cm-based to match the standard structural workflow.
        double cm = feet * 30.48;
        SelectComboByTag(CmbRowToleranceUnit, "cm");
        TxtRowTolerance.Text = cm.ToString("F0");

        TxtRowToleranceAutoHint.Text = result.IsConfident
            ? $"🪄 Auto-set to {cm:F0} cm — {result.Reason}."
            : $"🪄 Auto-set to {cm:F0} cm — {result.Reason}; manual fine-tuning may help.";
        UpdateToleranceHint();
    }

    private void UpdateToleranceHint()
    {
        if (TxtToleranceHint == null) return;
        var feet = ParseToleranceInFeet();
        var cm   = feet * 30.48;

        string desc = cm switch
        {
            <= 0  => "exact match — columns must sit directly on top of each other",
            <= 3  => "very tight — minor modelling precision errors only",
            <= 15 => "stacked columns with a slight horizontal offset",
            <= 30 => "columns close but NOT directly stacked — use with care",
            _     => "very loose — may group unrelated nearby columns together",
        };

        TxtToleranceHint.Text = $"≈ {feet:F2} ft  ({cm:F1} cm)  —  {desc}";
    }

    // ── Family helpers ───────────────────────────────────────────────────────

    private List<string> GetCheckedFamilies()
    {
        var result = new List<string>();
        foreach (ListBoxItem lbi in FamilyListBox.Items)
            if (lbi.Content is CheckBox cb && cb.IsChecked == true)
                result.Add(cb.Content?.ToString() ?? "");
        return result;
    }

    private void UpdateFamilyCountLabel()
    {
        var count = GetCheckedFamilies().Count;
        TxtFamilyCount.Text = count == 0
            ? "All families included"
            : $"{count} of {FamilyListBox.Items.Count} selected";
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    private void BtnScope_Click(object sender, RoutedEventArgs e)
    {
        _isSpecificFloor = (sender as Button)?.Tag?.ToString() == "SpecificFloor";
        UpdateScopeUI();
        ApplyAutoToleranceIfAvailable();
        RefreshPreview();
    }

    private void BtnMode_Click(object sender, RoutedEventArgs e)
    {
        _isChooseYourWayMode = (sender as Button)?.Tag?.ToString() == "ChooseYourWay";
        UpdateModeUI();
        ApplyAutoToleranceIfAvailable();
        RefreshPreview();
    }

    private void BtnExpandAdvanced_Click(object sender, RoutedEventArgs e)
    {
        bool expanded = PanelAdvancedContent.Visibility == Visibility.Visible;
        PanelAdvancedContent.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        TxtAdvancedArrow.Text           = expanded ? "▸" : "▾";
    }

    private void BtnExpandFamilyFilter_Click(object sender, RoutedEventArgs e)
    {
        bool expanded = PanelFamilyFilterContent.Visibility == Visibility.Visible;
        PanelFamilyFilterContent.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        TxtFamilyFilterArrow.Text           = expanded ? "▸" : "▾";
    }

    private void CmbFloorDiscipline_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRefresh) return;
        var discipline = (CmbFloorDiscipline.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        RefreshReferenceFloorList(discipline);
        RefreshChooseWayFloorList(discipline);
        RefreshPreview();
    }

    private void CmbFamilyCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRefresh) return;
        RefreshFamilyList(SelectedCategoryName());
        RefreshPreview();
    }

    private void CmbSettings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Re-derive the suggested row tolerance whenever a setting that
        // changes the row-perpendicular axis (sort direction or reference
        // floor) is altered.  Other combos pass through harmlessly because
        // ApplyAutoToleranceIfAvailable() only writes to TxtRowTolerance
        // when the detector returns a value.
        if (sender is ComboBox cb &&
            (ReferenceEquals(cb, CmbSort) ||
             ReferenceEquals(cb, CmbChooseWaySort) ||
             ReferenceEquals(cb, CmbReferenceFloor) ||
             ReferenceEquals(cb, CmbChooseWayFloor) ||
             ReferenceEquals(cb, CmbSpecificFloor)))
        {
            ApplyAutoToleranceIfAvailable();
        }
        UpdateToleranceHint();
        RefreshPreview();
    }

    private void TxtSettings_LostFocus(object sender, RoutedEventArgs e)
    {
        UpdateToleranceHint();
        RefreshPreview();
    }

    private void TxtSettings_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { UpdateToleranceHint(); RefreshPreview(); }
    }

    private void FamilyCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateFamilyCountLabel();
        RefreshPreview();
    }

    private void SelectAllFamilies_Click(object sender, RoutedEventArgs e)
    {
        foreach (ListBoxItem lbi in FamilyListBox.Items)
            if (lbi.Content is CheckBox cb) cb.IsChecked = true;
    }

    private void ClearAllFamilies_Click(object sender, RoutedEventArgs e)
    {
        foreach (ListBoxItem lbi in FamilyListBox.Items)
            if (lbi.Content is CheckBox cb) cb.IsChecked = false;
    }

    private void ChooseColumnBtn_Click(object sender, RoutedEventArgs e)
    {
        PickColumnRequested = true;
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Closes the dialog and signals the hosting command to enter the
    /// manual mark-editing loop.  The user will pick columns one at a
    /// time, edit each mark, and press "Save and Done" when finished.
    /// </summary>
    private void EditMarksBtn_Click(object sender, RoutedEventArgs e)
    {
        EditMarksRequested = true;
        DialogResult       = false;
        Close();
    }

    private void PreviewGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_uidoc == null) return;
        if (PreviewGrid.SelectedItem is not ColumnPreviewRow row) return;
        if (row.FirstElementId == 0) return;

        try
        {
            var eid = new Autodesk.Revit.DB.ElementId(row.FirstElementId);
            _uidoc.Selection.SetElementIds([eid]);
        }
        catch { /* ignore selection errors */ }
    }

    private void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        Result = BuildOptions();
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

// ── ViewModel row ────────────────────────────────────────────────────────────

public class ColumnPreviewRow
{
    public int    Sequence       { get; set; }
    public string GridRef        { get; set; } = "";
    public string FamilyName     { get; set; } = "";
    public int    FloorCount     { get; set; }
    public string CurrentMark    { get; set; } = "";
    public string NewMark        { get; set; } = "";
    public long   FirstElementId { get; set; }

    public string NewMarkForeground { get; private set; } = "#CDD6F4";
    public Brush  StatusBackground  { get; private set; } = MakeBrush("#313244");
    public Brush  StatusForeground  { get; private set; } = MakeBrush("#6C7086");
    public Brush  RowBackground     { get; private set; } = MakeBrush("#181825");
    public string StatusText        { get; private set; } = "";

    private GroupNumberingStatus _status;
    public GroupNumberingStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            switch (value)
            {
                case GroupNumberingStatus.NotNumbered:
                    StatusText = "NEW"; StatusBackground = MakeBrush("#1e2f4a");
                    StatusForeground = MakeBrush("#89B4FA"); NewMarkForeground = "#89B4FA";
                    RowBackground = MakeBrush("#181825"); break;
                case GroupNumberingStatus.FullyNumbered:
                    StatusText = "KEEP"; StatusBackground = MakeBrush("#1b3a2a");
                    StatusForeground = MakeBrush("#A6E3A1"); NewMarkForeground = "#A6E3A1";
                    RowBackground = MakeBrush("#191e1a"); break;
                case GroupNumberingStatus.PartiallyNumbered:
                    StatusText = "COMPLETE"; StatusBackground = MakeBrush("#2a1e3a");
                    StatusForeground = MakeBrush("#CBA6F7"); NewMarkForeground = "#CBA6F7";
                    RowBackground = MakeBrush("#1c1a22"); break;
                case GroupNumberingStatus.Conflicting:
                    StatusText = "CONFLICT"; StatusBackground = MakeBrush("#3a2a1b");
                    StatusForeground = MakeBrush("#F9E2AF"); NewMarkForeground = "#F9E2AF";
                    RowBackground = MakeBrush("#221e18"); break;
            }
        }
    }

    private static SolidColorBrush MakeBrush(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }
}
