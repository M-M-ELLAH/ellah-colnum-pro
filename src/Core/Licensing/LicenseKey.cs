using System.Security.Cryptography;
using System.Text;

namespace EllahColNum.Core.Licensing;

/// <summary>
/// Handles license key encoding, decoding and cryptographic validation.
///
/// Key format:  ELLAH-XXXXX-XXXXX-XXXXX-XXXXX
/// Internals:   5 random bytes (seed) + 5 HMAC bytes (verifier) = 10 bytes → 20 hex chars
///
/// The secret salt is baked into the DLL.  Only keys produced with the
/// matching salt (i.e. from our New-LicenseKey tool) will pass validation.
/// </summary>
public static class LicenseKey
{
    // !! Change this salt before first commercial release !!
    private static readonly byte[] _salt = Encoding.UTF8.GetBytes("EllahColNumPro-2026-#Str@ngS@lt!");

    public const string Prefix = "ELLAH-";

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Returns true when the key string is structurally and cryptographically valid.</summary>
    public static bool IsValid(string? key)
    {
        if (!TryDecode(key, out var seed, out var storedHmac)) return false;
        var expected = ComputeHmac(seed);
        return CryptographicOperations.FixedTimeEquals(expected, storedHmac);
    }

    /// <summary>Generates a new, unique, valid license key. Used by the seller tool only.</summary>
    public static string Generate()
    {
        var seed = RandomNumberGenerator.GetBytes(5);
        var hmac = ComputeHmac(seed);
        var raw  = seed.Concat(hmac).ToArray(); // 10 bytes
        return Format(Convert.ToHexString(raw)); // 20 hex chars → ELLAH-XXXXX-XXXXX-XXXXX-XXXXX
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static byte[] ComputeHmac(byte[] seed)
    {
        using var hmac = new HMACSHA256(_salt);
        return hmac.ComputeHash(seed)[..5]; // first 5 bytes → 10 hex chars
    }

    private static bool TryDecode(string? input, out byte[] seed, out byte[] storedHmac)
    {
        seed = []; storedHmac = [];
        if (string.IsNullOrWhiteSpace(input)) return false;

        var raw = input.ToUpperInvariant()
                       .Replace(Prefix, "")
                       .Replace("-", "")
                       .Trim();

        if (raw.Length != 20) return false;

        try
        {
            var bytes = Convert.FromHexString(raw); // 10 bytes
            seed       = bytes[..5];
            storedHmac = bytes[5..];
            return true;
        }
        catch { return false; }
    }

    private static string Format(string hex20)
    {
        // ELLAH-XXXXX-XXXXX-XXXXX-XXXXX
        return $"{Prefix}{hex20[..5]}-{hex20[5..10]}-{hex20[10..15]}-{hex20[15..20]}";
    }
}
