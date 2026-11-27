using System.Globalization;

namespace Cordon.Shared.Windows;

public static class BadgeDisplayFormatter
{
    public static string FormatCount(int unresolvedCount) => unresolvedCount switch
    {
        <= 0 => string.Empty,
        <= 99 => unresolvedCount.ToString(CultureInfo.InvariantCulture),
        _ => "99+"
    };

    public static string FormatAccessibleLabel(int unresolvedCount, bool isStaleOrOffline)
    {
        var countText = unresolvedCount switch
        {
            <= 0 => "no pending actions",
            1 => "1 pending action",
            <= 99 => $"{unresolvedCount} pending actions",
            _ => $"99+ ({unresolvedCount}) pending actions"
        };

        return isStaleOrOffline
            ? $"Cordon Hive: {countText}, connection stale or offline"
            : $"Cordon Hive: {countText}";
    }
}
