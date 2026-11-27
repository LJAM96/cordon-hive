using System.Text.RegularExpressions;

namespace Hive.Domain.Software;

public enum ComplianceStatus
{
    Compliant,
    UpdateRequired,
    AheadUnapproved,
    NotInstalled,
    Exempt,
    Unknown,
    PendingReboot
}

public enum VersionScheme
{
    NumericSegments,
    SemVer,
    Exact
}

public enum VersionComparisonOutcome
{
    Equal,
    Older,
    Newer,
    NotEqual,
    Unknown
}

/// <summary>
/// Product-aware version comparison. Never falls back to lexicographic comparison
/// on arbitrary strings; unparseable versions yield Unknown (fail-safe).
/// </summary>
public static partial class SoftwareVersionComparer
{
    [GeneratedRegex(@"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemVerRegex();

    [GeneratedRegex(@"^\d+(?:\.\d+)*$")]
    private static partial Regex NumericSegmentsRegex();

    public static VersionComparisonOutcome Compare(string? observedRaw, string? targetRaw, VersionScheme scheme)
    {
        if (string.IsNullOrWhiteSpace(observedRaw) || string.IsNullOrWhiteSpace(targetRaw))
        {
            return VersionComparisonOutcome.Unknown;
        }

        var observed = observedRaw.Trim();
        var target = targetRaw.Trim();

        return scheme switch
        {
            VersionScheme.Exact => string.Equals(observed, target, StringComparison.OrdinalIgnoreCase)
                ? VersionComparisonOutcome.Equal
                : VersionComparisonOutcome.NotEqual,
            VersionScheme.NumericSegments => CompareNumericSegments(observed, target),
            VersionScheme.SemVer => CompareSemVer(observed, target),
            _ => VersionComparisonOutcome.Unknown
        };
    }

    private static VersionComparisonOutcome CompareNumericSegments(string observed, string target)
    {
        if (!NumericSegmentsRegex().IsMatch(observed) || !NumericSegmentsRegex().IsMatch(target))
        {
            return VersionComparisonOutcome.Unknown;
        }

        var obsParts = observed.Split('.').Select(long.Parse).ToArray();
        var tgtParts = target.Split('.').Select(long.Parse).ToArray();
        var maxLen = Math.Max(obsParts.Length, tgtParts.Length);

        for (var i = 0; i < maxLen; i++)
        {
            var o = i < obsParts.Length ? obsParts[i] : 0;
            var t = i < tgtParts.Length ? tgtParts[i] : 0;
            if (o < t) return VersionComparisonOutcome.Older;
            if (o > t) return VersionComparisonOutcome.Newer;
        }

        return VersionComparisonOutcome.Equal;
    }

    private static VersionComparisonOutcome CompareSemVer(string observed, string target)
    {
        var obsMatch = SemVerRegex().Match(observed);
        var tgtMatch = SemVerRegex().Match(target);
        if (!obsMatch.Success || !tgtMatch.Success)
        {
            return VersionComparisonOutcome.Unknown;
        }

        var coreCmp = CompareNumericTuple(
            long.Parse(obsMatch.Groups["major"].Value),
            long.Parse(obsMatch.Groups["minor"].Value),
            long.Parse(obsMatch.Groups["patch"].Value),
            long.Parse(tgtMatch.Groups["major"].Value),
            long.Parse(tgtMatch.Groups["minor"].Value),
            long.Parse(tgtMatch.Groups["patch"].Value));

        if (coreCmp != 0)
        {
            return coreCmp < 0 ? VersionComparisonOutcome.Older : VersionComparisonOutcome.Newer;
        }

        var obsPre = obsMatch.Groups["pre"].Value;
        var tgtPre = tgtMatch.Groups["pre"].Value;

        if (string.IsNullOrEmpty(obsPre) && string.IsNullOrEmpty(tgtPre))
        {
            return VersionComparisonOutcome.Equal;
        }

        if (string.IsNullOrEmpty(obsPre)) return VersionComparisonOutcome.Newer;
        if (string.IsNullOrEmpty(tgtPre)) return VersionComparisonOutcome.Older;

        var preCmp = ComparePrerelease(obsPre, tgtPre);
        return preCmp switch
        {
            < 0 => VersionComparisonOutcome.Older,
            > 0 => VersionComparisonOutcome.Newer,
            _ => VersionComparisonOutcome.Equal
        };
    }

    private static int CompareNumericTuple(long o1, long o2, long o3, long t1, long t2, long t3)
    {
        var c = o1.CompareTo(t1);
        if (c != 0) return c;
        c = o2.CompareTo(t2);
        return c != 0 ? c : o3.CompareTo(t3);
    }

    private static int ComparePrerelease(string left, string right)
    {
        var lIds = left.Split('.');
        var rIds = right.Split('.');
        var len = Math.Min(lIds.Length, rIds.Length);

        for (var i = 0; i < len; i++)
        {
            var lIsNum = long.TryParse(lIds[i], out var lNum);
            var rIsNum = long.TryParse(rIds[i], out var rNum);

            if (lIsNum && rIsNum)
            {
                var c = lNum.CompareTo(rNum);
                if (c != 0) return c;
            }
            else if (lIsNum != rIsNum)
            {
                return lIsNum ? -1 : 1;
            }
            else
            {
                var c = string.CompareOrdinal(lIds[i], rIds[i]);
                if (c != 0) return c;
            }
        }

        return lIds.Length.CompareTo(rIds.Length);
    }
}
