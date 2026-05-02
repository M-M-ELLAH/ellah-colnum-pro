using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EllahColNum.Core.Licensing;

namespace EllahColNum.Revit.UI;

public partial class ActivationDialog : Window
{
    public bool Activated { get; private set; }

    public ActivationDialog()
    {
        InitializeComponent();
        TxtMachineId.Text = LicenseChecker.GetMachineId();
    }

    // ── Live key validation ──────────────────────────────────────────────────

    private void TxtKey_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var key = TxtKey.Text.Trim();
        ActivateBtn.IsEnabled = key.Length >= 20;
        if (StatusBorder.Visibility == Visibility.Visible)
            HideStatus();
    }

    private void TxtKey_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ActivateBtn.IsEnabled)
            TryActivate();
    }

    // ── Activation ───────────────────────────────────────────────────────────

    private void ActivateBtn_Click(object sender, RoutedEventArgs e) => TryActivate();

    private void TryActivate()
    {
        ActivateBtn.IsEnabled = false;
        var result = LicenseChecker.Activate(TxtKey.Text.Trim());

        switch (result)
        {
            case ActivationResult.Success:
                ShowStatus("✔  Activation successful! The plugin is now unlocked.", success: true);
                Activated = true;
                // Auto-close after short delay
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1.5)
                };
                timer.Tick += (_, _) => { timer.Stop(); DialogResult = true; Close(); };
                timer.Start();
                break;

            case ActivationResult.InvalidKey:
                ShowStatus("✘  Invalid license key. Please check and try again.", success: false);
                ActivateBtn.IsEnabled = true;
                break;

            default:
                ShowStatus("✘  Activation failed. Please contact ELLAH support.", success: false);
                ActivateBtn.IsEnabled = true;
                break;
        }
    }

    // ── Copy machine ID ──────────────────────────────────────────────────────

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(TxtMachineId.Text);
        CopyBtn.Content = "Copied!";
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        timer.Tick += (_, _) => { timer.Stop(); CopyBtn.Content = "Copy"; };
        timer.Start();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ShowStatus(string msg, bool success)
    {
        TxtStatus.Text = msg;
        StatusBorder.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(success ? "#1b3a2a" : "#3a1b1b"));
        TxtStatus.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(success ? "#A6E3A1" : "#F38BA8"));
        StatusBorder.Visibility = Visibility.Visible;
    }

    private void HideStatus() => StatusBorder.Visibility = Visibility.Collapsed;

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
