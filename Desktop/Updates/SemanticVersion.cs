using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;

namespace MeowChan.Desktop.Updates;

/// <summary>SemVer 2 precedence, including arbitrarily large numeric identifiers.</summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"\A[v]?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly string[] _core;
    private readonly string[] _prerelease;
    private readonly string _text;

    private SemanticVersion(string text, string[] core, string[] prerelease)
    {
        _text = text;
        _core = core;
        _prerelease = prerelease;
    }

    public bool IsPrerelease => _prerelease.Length != 0;

    public static bool TryParse(string? text, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (text is null || text.Length > 256) return false;
        Match match = Pattern.Match(text);
        if (!match.Success) return false;
        string[] prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(part => IsNumeric(part) && part.Length > 1 && part[0] == '0')) return false;
        version = new SemanticVersion(text.TrimStart('v'),
            [match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value], prerelease);
        return true;
    }

    public static SemanticVersion Parse(string text) => TryParse(text, out SemanticVersion? version)
        ? version : throw new FormatException("Expected a SemVer version with optional lowercase v prefix.");

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        for (int index = 0; index < _core.Length; index++)
        {
            int comparison = CompareNumeric(_core[index], other._core[index]);
            if (comparison != 0) return comparison;
        }
        if (!IsPrerelease || !other.IsPrerelease)
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        for (int index = 0; index < Math.Min(_prerelease.Length, other._prerelease.Length); index++)
        {
            string left = _prerelease[index];
            string right = other._prerelease[index];
            bool leftNumeric = IsNumeric(left);
            bool rightNumeric = IsNumeric(right);
            int comparison = leftNumeric && rightNumeric ? CompareNumeric(left, right)
                : leftNumeric != rightNumeric ? leftNumeric ? -1 : 1
                : string.CompareOrdinal(left, right);
            if (comparison != 0) return comparison;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static bool IsNumeric(string text) => text.All(character => character is >= '0' and <= '9');
    private static int CompareNumeric(string left, string right) => left.Length != right.Length
        ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);
    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is SemanticVersion version && Equals(version);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_text.Split('+')[0]);
    public override string ToString() => _text;
}
