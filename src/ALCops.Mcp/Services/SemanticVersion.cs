using System.Diagnostics.CodeAnalysis;

namespace ALCops.Mcp.Services;

internal sealed class SemanticVersion : IComparable<SemanticVersion>
{
    public Version Base { get; }
    public IReadOnlyList<string> PrereleaseIdentifiers { get; }
    public bool IsStable => PrereleaseIdentifiers.Count == 0;
    public string Raw { get; }

    private SemanticVersion(Version @base, IReadOnlyList<string> prereleaseIdentifiers, string raw)
    {
        Base = @base;
        PrereleaseIdentifiers = prereleaseIdentifiers;
        Raw = raw;
    }

    public static bool TryParse(string? raw, [NotNullWhen(true)] out SemanticVersion? result)
    {
        result = null;
        if (string.IsNullOrEmpty(raw))
            return false;

        var input = raw;

        var plusIndex = input.IndexOf('+');
        if (plusIndex >= 0)
            input = input[..plusIndex];

        var dashIndex = input.IndexOf('-');
        string basePart;
        IReadOnlyList<string> identifiers;

        if (dashIndex >= 0)
        {
            basePart = input[..dashIndex];
            identifiers = input[(dashIndex + 1)..].Split('.');
        }
        else
        {
            basePart = input;
            identifiers = [];
        }

        if (!Version.TryParse(basePart, out var version))
            return false;

        version = new Version(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));

        result = new SemanticVersion(version, identifiers, raw);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;

        var baseCompare = Base.CompareTo(other.Base);
        if (baseCompare != 0) return baseCompare;

        if (IsStable && !other.IsStable) return 1;
        if (!IsStable && other.IsStable) return -1;
        if (IsStable) return 0;

        var minLen = Math.Min(PrereleaseIdentifiers.Count, other.PrereleaseIdentifiers.Count);
        for (var i = 0; i < minLen; i++)
        {
            var a = PrereleaseIdentifiers[i];
            var b = other.PrereleaseIdentifiers[i];

            var aIsNumeric = IsNumericIdentifier(a);
            var bIsNumeric = IsNumericIdentifier(b);

            if (aIsNumeric && bIsNumeric)
            {
                var cmp = CompareNumeric(a, b);
                if (cmp != 0) return cmp;
            }
            else if (aIsNumeric != bIsNumeric)
            {
                return aIsNumeric ? -1 : 1;
            }
            else
            {
                var cmp = StringComparer.OrdinalIgnoreCase.Compare(a, b);
                if (cmp != 0) return cmp;
            }
        }

        return PrereleaseIdentifiers.Count.CompareTo(other.PrereleaseIdentifiers.Count);
    }

    private static bool IsNumericIdentifier(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
            if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    private static int CompareNumeric(string a, string b)
    {
        var aStripped = a.AsSpan().TrimStart('0');
        var bStripped = b.AsSpan().TrimStart('0');

        if (aStripped.Length == 0 && bStripped.Length == 0) return 0;

        if (aStripped.Length != bStripped.Length)
            return aStripped.Length.CompareTo(bStripped.Length);

        return aStripped.SequenceCompareTo(bStripped);
    }

    public static readonly IComparer<SemanticVersion?> Comparer =
        Comparer<SemanticVersion?>.Create((x, y) => x is null ? (y is null ? 0 : -1) : y is null ? 1 : x.CompareTo(y));
}
