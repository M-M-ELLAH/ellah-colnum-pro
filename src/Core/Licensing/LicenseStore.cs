using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EllahColNum.Core.Licensing;

/// <summary>
/// Persists and reads the license activation record from:
///   %APPDATA%\EllahColNumPro\license.dat
///
/// The file is a JSON object signed with HMAC so it cannot be tampered with
/// (e.g. copying a license file from machine A to machine B won't work).
/// </summary>
public static class LicenseStore
{
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EllahColNumPro", "license.dat");

    private static readonly byte[] _fileSalt =
        Encoding.UTF8.GetBytes("EllahLicenseFile-StorageKey-2026");

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Saves an activated license bound to this machine.</summary>
    public static void Save(string licenseKey)
    {
        var record = new LicenseRecord
        {
            Key        = licenseKey.ToUpperInvariant(),
            MachineId  = MachineFingerprint.Get(),
            ActivatedOn = DateTime.UtcNow.ToString("yyyy-MM-dd"),
        };
        record.Signature = Sign(record);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(record,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Tries to load a valid, untampered, machine-matching license.
    /// Returns null if no valid license exists.
    /// </summary>
    public static LicenseRecord? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;

            var record = JsonSerializer.Deserialize<LicenseRecord>(File.ReadAllText(_path));
            if (record == null) return null;

            // Verify file integrity
            var expectedSig = Sign(record);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expectedSig),
                    Encoding.UTF8.GetBytes(record.Signature ?? "")))
                return null;

            // Verify machine match
            if (record.MachineId != MachineFingerprint.Get()) return null;

            return record;
        }
        catch { return null; }
    }

    public static void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Sign(LicenseRecord r)
    {
        var payload = $"{r.Key}|{r.MachineId}|{r.ActivatedOn}";
        using var hmac = new HMACSHA256(_fileSalt);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}

public class LicenseRecord
{
    public string  Key         { get; set; } = "";
    public string  MachineId   { get; set; } = "";
    public string  ActivatedOn { get; set; } = "";
    public string? Signature   { get; set; }
}
