using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace EllahColNum.Setup;

public partial class InstallerWindow : Window
{
    private static readonly string[] SupportedVersions = ["2027", "2026", "2025", "2024"];

    private const string TermsText =
        "ELLAH-ColNum Pro — Terms of Use\n\n" +
        "1. This software is licensed for use by the purchasing individual only.\n" +
        "2. Redistribution, resale, or sharing of the license key is prohibited.\n" +
        "3. The plugin writes to Revit parameters only when explicitly triggered.\n" +
        "4. All changes are reversible via Revit's standard Ctrl+Z undo.\n" +
        "5. ELLAH is not liable for any project data affected by plugin use.\n" +
        "6. By installing you agree to these terms.\n\n" +
        "Contact: maor@mm-ellah.com";

    public InstallerWindow()
    {
        InitializeComponent();
        LogLine("Ready to install ELLAH-ColNum Pro.", "#89B4FA");
        LogLine("");

        var detected = DetectRevitVersions();
        if (detected.Count == 0)
        {
            LogLine("WARNING: No Revit installation detected on this PC.", "#F9E2AF");
            LogLine("The plugin will still be installed — open Revit after.", "#6C7086");
        }
        else
        {
            LogLine($"Detected Revit: {string.Join(", ", detected)}", "#A6E3A1");
        }

        var embedded = Assembly.GetExecutingAssembly().GetManifestResourceNames();
        if (!embedded.Contains("EllahColNumPro.dll") || !embedded.Contains("EllahColNumPro.Core.dll"))
        {
            LogLine("");
            LogLine("ERROR: Plugin files not embedded in this installer.", "#F38BA8");
            LogLine("Please use the official installer from ELLAH.", "#F38BA8");
            InstallBtn.IsEnabled = false;
        }
    }

    private void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        InstallBtn.IsEnabled = false;

        Step("Checking Revit is not running...", 10);
        var revitProcs = Process.GetProcessesByName("Revit");
        if (revitProcs.Length > 0)
        {
            LogLine("Revit is currently open. Please close all Revit windows and try again.", "#F38BA8");
            StatusLabel.Text = "Close Revit and retry.";
            InstallBtn.IsEnabled = true;
            return;
        }
        LogLine("Revit is not running.", "#A6E3A1");

        Step("Finding Revit Addins folders...", 25);
        var targets = GetInstallTargets();
        if (targets.Count == 0)
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Autodesk", "Revit", "Addins", "2025");
            targets.Add(fallback);
            LogLine($"No Revit found — installing to default: {fallback}", "#F9E2AF");
        }
        else
        {
            foreach (var t in targets)
                LogLine($"Target: {t}", "#6C7086");
        }

        Step("Extracting plugin files...", 50);
        byte[]? dll     = ReadResource("EllahColNumPro.dll");
        byte[]? coreDll = ReadResource("EllahColNumPro.Core.dll");
        byte[]? addin   = ReadResource("EllahColNumPro.addin");

        if (dll == null || coreDll == null || addin == null)
        {
            LogLine("ERROR: Embedded files could not be read.", "#F38BA8");
            InstallBtn.IsEnabled = true;
            return;
        }
        LogLine("Plugin files extracted from installer.", "#A6E3A1");

        Step("Installing...", 75);
        int installed = 0;
        foreach (var folder in targets)
        {
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "EllahColNumPro.dll"),      dll);
                File.WriteAllBytes(Path.Combine(folder, "EllahColNumPro.Core.dll"), coreDll);
                File.WriteAllBytes(Path.Combine(folder, "EllahColNumPro.addin"),    addin);
                LogLine($"Installed to: {folder}", "#A6E3A1");
                installed++;
            }
            catch (Exception ex)
            {
                LogLine($"Failed: {folder} — {ex.Message}", "#F38BA8");
            }
        }

        Progress.Value = 100;

        if (installed > 0)
        {
            LogLine("");
            LogLine("Installation complete! ✓", "#A6E3A1");
            LogLine("Open Revit → look for the ELLAH-ColNum Pro tab in the ribbon.", "#89B4FA");
            StatusLabel.Text = $"Installed to {installed} Revit version(s).";

            // Swap Install button for a disabled label, show green Done button
            InstallBtn.Content    = "Installed ✓";
            InstallBtn.IsEnabled  = false;
            DoneBtn.Visibility    = Visibility.Visible;
        }
        else
        {
            LogLine("Installation failed — no folders were written.", "#F38BA8");
            InstallBtn.IsEnabled = true;
        }
    }

    private void TermsBtn_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            TermsText,
            "ELLAH-ColNum Pro — Terms of Use",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void DoneBtn_Click(object sender, RoutedEventArgs e) => Close();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Step(string message, int progress)
    {
        Progress.Value = progress;
        LogLine(message);
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void LogLine(string text, string colorHex = "#CDD6F4")
    {
        var run = new Run(text + "\n")
        {
            Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom(colorHex)!
        };
        LogText.Inlines.Add(run);
        LogScroll.ScrollToBottom();
    }

    private static List<string> DetectRevitVersions()
    {
        var found      = new List<string>();
        var appsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk");

        if (!Directory.Exists(appsFolder)) return found;

        foreach (var version in SupportedVersions)
        {
            if (Directory.Exists(Path.Combine(appsFolder, $"Revit {version}")))
                found.Add(version);
        }
        return found;
    }

    private static List<string> GetInstallTargets()
    {
        var versions   = DetectRevitVersions();
        var addinsBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins");

        return versions
            .Select(v => Path.Combine(addinsBase, v))
            .ToList();
    }

    private static byte[]? ReadResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(name);
        if (stream == null) return null;
        var buffer = new byte[stream.Length];
        _ = stream.Read(buffer, 0, buffer.Length);
        return buffer;
    }
}
