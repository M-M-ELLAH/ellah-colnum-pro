namespace EllahColNum.Core.Licensing;

/// <summary>
/// Main entry point for license checking.
/// Call IsLicensed() before allowing the plugin to run.
/// </summary>
public static class LicenseChecker
{
    /// <summary>
    /// Returns true when a valid, machine-bound license exists on this PC.
    /// Fast — only reads and validates a local file.
    /// </summary>
    public static bool IsLicensed()
    {
        var record = LicenseStore.Load();
        if (record == null) return false;
        return LicenseKey.IsValid(record.Key);
    }

    /// <summary>
    /// Attempts to activate with the provided key.
    /// Returns the result of the attempt.
    /// </summary>
    public static ActivationResult Activate(string key)
    {
        var normalized = key?.Trim().ToUpperInvariant() ?? "";

        if (!LicenseKey.IsValid(normalized))
            return ActivationResult.InvalidKey;

        LicenseStore.Save(normalized);
        return ActivationResult.Success;
    }

    /// <summary>Returns the machine ID string to show the user (for manual key requests).</summary>
    public static string GetMachineId() => MachineFingerprint.Get();
}

public enum ActivationResult
{
    Success,
    InvalidKey,
    AlreadyActivated,
}
