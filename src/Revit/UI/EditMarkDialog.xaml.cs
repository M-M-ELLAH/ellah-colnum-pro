using System.Windows;

namespace EllahColNum.Revit.UI;

/// <summary>
/// Compact popup the user sees after clicking a column in the manual
/// mark-correction loop.  Shows the column's current mark, lets the user
/// type a new value, and exposes three exits:
///   • <b>Save → Pick next</b>  — saves this column's mark and goes back
///     to the picker so the user can edit another column.
///   • <b>Save and Done</b>     — saves this column's mark and finishes
///     the editing session entirely.
///   • <b>Cancel</b>            — discards the input and returns to the
///     picker (no save for this column).
///
/// Keeps no Revit references — communicates via plain string + bool flags
/// so the hosting command owns all model mutations.
/// </summary>
public partial class EditMarkDialog : Window
{
    /// <summary>The text the user typed.  Always trimmed.</summary>
    public string NewMark { get; private set; } = "";

    /// <summary>True when the user clicked "Save and Done".</summary>
    public bool   FinishedAfterSave { get; private set; }

    public EditMarkDialog(string currentMark)
    {
        InitializeComponent();

        CurrentMarkText.Text = string.IsNullOrWhiteSpace(currentMark)
            ? "(empty)"
            : currentMark;

        // Pre-fill the editor with the current mark and select-all so the
        // user can type to overwrite or arrow-key to refine.
        NewMarkBox.Text = currentMark ?? "";
        NewMarkBox.SelectAll();
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        NewMark            = (NewMarkBox.Text ?? "").Trim();
        FinishedAfterSave  = false;
        DialogResult       = true;
        Close();
    }

    private void SaveAndDoneBtn_Click(object sender, RoutedEventArgs e)
    {
        NewMark            = (NewMarkBox.Text ?? "").Trim();
        FinishedAfterSave  = true;
        DialogResult       = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
