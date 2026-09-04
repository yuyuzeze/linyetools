using System.Globalization;

namespace KikuCaption.ComponentManagement.Versioning;

/// <summary>
/// A numeric, dotted version (e.g. "0.1.10", "1", "2.3.4.5") compared FIELD-BY-FIELD as integers —
/// never as strings. This is what makes "1.0.10" correctly greater than "1.0.9" (a lexicographic
/// compare would get it wrong). 1–4 components are accepted; missing trailing fields count as 0, so
/// "1.0" equals "1.0.0". Non-numeric or negative components are rejected.
///
/// STABLE-ONLY NUMERIC VERSION POLICY (R7A.1): this deliberately accepts ONLY plain dotted integers.
/// A "v" prefix ("v0.1.0") and any pre-release / build metadata ("1.0.0-beta", "1.0.0+ci") are
/// rejected. Full SemVer (pre-release ordering, build metadata) is intentionally out of scope for the
/// current stable channel and is NOT to be added here without an explicit requirement change.
/// </summary>
public readonly struct ManifestVersion : IComparable<ManifestVersion>, IEquatable<ManifestVersion>
{
    private readonly int _a, _b, _c, _d;

    private ManifestVersion(int a, int b, int c, int d) { _a = a; _b = b; _c = c; _d = d; }

    public static bool TryParse(string? text, out ManifestVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        var fields = new int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            // Reject signs, whitespace, hex, empty — only plain non-negative integers.
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            fields[i] = value;
        }

        version = new ManifestVersion(fields[0], fields[1], fields[2], fields[3]);
        return true;
    }

    public static ManifestVersion Parse(string text)
        => TryParse(text, out var v) ? v : throw new FormatException($"Invalid version: '{text}'.");

    public int CompareTo(ManifestVersion other)
    {
        int c = _a.CompareTo(other._a); if (c != 0) return c;
        c = _b.CompareTo(other._b); if (c != 0) return c;
        c = _c.CompareTo(other._c); if (c != 0) return c;
        return _d.CompareTo(other._d);
    }

    public bool Equals(ManifestVersion other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is ManifestVersion v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(_a, _b, _c, _d);
    public override string ToString() => $"{_a}.{_b}.{_c}.{_d}";

    public static bool operator >(ManifestVersion x, ManifestVersion y) => x.CompareTo(y) > 0;
    public static bool operator <(ManifestVersion x, ManifestVersion y) => x.CompareTo(y) < 0;
    public static bool operator >=(ManifestVersion x, ManifestVersion y) => x.CompareTo(y) >= 0;
    public static bool operator <=(ManifestVersion x, ManifestVersion y) => x.CompareTo(y) <= 0;
    public static bool operator ==(ManifestVersion x, ManifestVersion y) => x.Equals(y);
    public static bool operator !=(ManifestVersion x, ManifestVersion y) => !x.Equals(y);
}
