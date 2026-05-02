using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace EllahColNum.Core.Licensing;

/// <summary>
/// Generates a stable, unique identifier for the current machine.
/// Used to bind a license key to a specific PC.
///
/// Sources used (all available without admin rights):
///   - Windows MachineGuid (set once at OS install)
///   - Machine name
/// Result: first 12 hex chars of SHA256, formatted as XXXX-XXXX-XXXX
/// </summary>
public static class MachineFingerprint
{
    private static string? _cached;

    /// <summary>Returns the machine ID (12 hex chars, e.g. "A1B2-C3D4-E5F6").</summary>
    public static string Get()
    {
        if (_cached != null) return _cached;

        var sb = new StringBuilder();
        sb.Append(Environment.MachineName);
        sb.Append('|');
        sb.Append(GetMachineGuid());

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        var hex  = Convert.ToHexString(hash)[..12]; // 12 chars = 48 bits
        _cached  = $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
        return _cached;
    }

    private static string GetMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
