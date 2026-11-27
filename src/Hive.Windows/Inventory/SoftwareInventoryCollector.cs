using System.Diagnostics;
using System.Globalization;
using System.Security;
using Hive.Contracts;
using Microsoft.Win32;

namespace Hive.Windows.Inventory;

public sealed record PortableAppRule(
    string DisplayName,
    string FilePath,
    string? Publisher = null);

public sealed record SoftwareInventoryOptions(
    bool IncludeCurrentUserRegistry = false,
    IReadOnlyList<PortableAppRule>? PortableApps = null);

public interface ISoftwareInventoryCollector
{
    Task<InventorySnapshotRequest> CollectAsync(
        long detectionRevision,
        SoftwareInventoryOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only Windows software inventory collector using Registry Uninstall keys,
/// MSIX AppxAllUserStore package keys, and explicitly configured portable file paths.
/// Never queries Win32_Product and never recursively scans drives or user directories.
/// </summary>
public sealed class WindowsSoftwareInventoryCollector : ISoftwareInventoryCollector
{
    private const string UninstallSubKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Wow64UninstallSubKeyPath = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppxAllUserApplicationsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications";

    private static readonly HashSet<string> ValidMsixArchitectures = new(StringComparer.OrdinalIgnoreCase)
    {
        "x86",
        "x64",
        "arm",
        "arm64",
        "neutral"
    };

    private readonly Func<DateTimeOffset> _utcNowProvider;

    public WindowsSoftwareInventoryCollector(Func<DateTimeOffset>? utcNowProvider = null)
    {
        _utcNowProvider = utcNowProvider ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<InventorySnapshotRequest> CollectAsync(
        long detectionRevision,
        SoftwareInventoryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveOptions = options ?? new SoftwareInventoryOptions();
        var observedAtUtc = _utcNowProvider();
        var items = new List<InstalledSoftwareItemDto>();
        var seen = new HashSet<(string DisplayName, string Version, string Source, string Scope)>(InventoryItemKeyComparer.Instance);

        CollectRegistryUninstall(
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            UninstallSubKeyPath,
            detectionSource: "RegistryMachine64",
            installScope: "Machine",
            observedAtUtc,
            items,
            seen,
            cancellationToken);

        CollectRegistryUninstall(
            RegistryHive.LocalMachine,
            RegistryView.Registry64,
            Wow64UninstallSubKeyPath,
            detectionSource: "RegistryMachine32",
            installScope: "Machine",
            observedAtUtc,
            items,
            seen,
            cancellationToken);

        if (effectiveOptions.IncludeCurrentUserRegistry)
        {
            CollectRegistryUninstall(
                RegistryHive.CurrentUser,
                RegistryView.Default,
                UninstallSubKeyPath,
                detectionSource: "RegistryUser",
                installScope: "User",
                observedAtUtc,
                items,
                seen,
                cancellationToken);
        }

        CollectMsixAllUserStore(observedAtUtc, items, seen, cancellationToken);

        if (effectiveOptions.PortableApps is { Count: > 0 } portableRules)
        {
            CollectPortableRules(portableRules, observedAtUtc, items, seen, cancellationToken);
        }

        items.Sort(static (left, right) =>
        {
            var nameCmp = StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
            if (nameCmp != 0)
            {
                return nameCmp;
            }

            var sourceCmp = StringComparer.OrdinalIgnoreCase.Compare(left.DetectionSource, right.DetectionSource);
            if (sourceCmp != 0)
            {
                return sourceCmp;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(
                left.ObservedVersionRaw ?? string.Empty,
                right.ObservedVersionRaw ?? string.Empty);
        });

        var snapshot = new InventorySnapshotRequest(
            DetectionRevision: detectionRevision,
            IsComplete: true,
            Items: items,
            CapturedAtUtc: observedAtUtc);

        return Task.FromResult(snapshot);
    }

    internal static (string DisplayName, string? Version, string Confidence) ParseMsixFullName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return (string.Empty, null, "Unknown");
        }

        var trimmed = fullName.Trim();
        var segments = trimmed.Split('_');
        if (segments.Length < 5)
        {
            return (trimmed, null, "Unknown");
        }

        var publisherId = segments[^1];
        var resourceId = segments[^2];
        var architecture = segments[^3];
        var version = segments[^4];
        var displayName = string.Join('_', segments[..^4]).Trim();

        if (string.IsNullOrWhiteSpace(displayName) ||
            !IsValidMsixVersion(version) ||
            !ValidMsixArchitectures.Contains(architecture) ||
            resourceId.Any(char.IsWhiteSpace) ||
            string.IsNullOrWhiteSpace(publisherId) ||
            !publisherId.All(char.IsLetterOrDigit))
        {
            return (trimmed, null, "Unknown");
        }

        return (displayName, version, "Verified");
    }

    private static void CollectRegistryUninstall(
        RegistryHive hive,
        RegistryView view,
        string subKeyPath,
        string detectionSource,
        string installScope,
        DateTimeOffset observedAtUtc,
        List<InstalledSoftwareItemDto> items,
        HashSet<(string DisplayName, string Version, string Source, string Scope)> seen,
        CancellationToken cancellationToken)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(subKeyPath, writable: false);
            if (uninstallKey is null)
            {
                return;
            }

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var entryKey = uninstallKey.OpenSubKey(subKeyName, writable: false);
                    if (entryKey is null)
                    {
                        continue;
                    }

                    if (IsSystemComponent(entryKey.GetValue("SystemComponent")))
                    {
                        continue;
                    }

                    var parentKeyName = NormalizeOptional(entryKey.GetValue("ParentKeyName")?.ToString());
                    if (parentKeyName is not null)
                    {
                        continue;
                    }

                    var displayName = NormalizeOptional(entryKey.GetValue("DisplayName")?.ToString());
                    if (displayName is null)
                    {
                        continue;
                    }

                    var publisher = NormalizeOptional(entryKey.GetValue("Publisher")?.ToString());
                    var displayVersion = NormalizeOptional(entryKey.GetValue("DisplayVersion")?.ToString());
                    var confidence = displayVersion is null ? "Partial" : "Verified";

                    AddDeduplicated(
                        items,
                        seen,
                        new InstalledSoftwareItemDto(
                            DisplayName: displayName,
                            Publisher: publisher,
                            ObservedVersionRaw: displayVersion,
                            DetectionSource: detectionSource,
                            InstallScope: installScope,
                            ConfidenceState: confidence,
                            ObservedAtUtc: observedAtUtc));
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException)
                {
                    // Skip inaccessible or concurrently deleted registry subkeys.
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            // Registry hive or root uninstall key unavailable; skip cleanly.
        }
    }

    private static void CollectMsixAllUserStore(
        DateTimeOffset observedAtUtc,
        List<InstalledSoftwareItemDto> items,
        HashSet<(string DisplayName, string Version, string Source, string Scope)> seen,
        CancellationToken cancellationToken)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var appxKey = baseKey.OpenSubKey(AppxAllUserApplicationsPath, writable: false);
            if (appxKey is null)
            {
                return;
            }

            foreach (var fullName in appxKey.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (displayName, version, confidence) = ParseMsixFullName(fullName);
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                AddDeduplicated(
                    items,
                    seen,
                    new InstalledSoftwareItemDto(
                        DisplayName: displayName,
                        Publisher: null,
                        ObservedVersionRaw: version,
                        DetectionSource: "MsixAllUserStore",
                        InstallScope: "Machine",
                        ConfidenceState: confidence,
                        ObservedAtUtc: observedAtUtc));
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            // AppxAllUserStore may be inaccessible or absent on some Windows configurations.
        }
    }

    private static void CollectPortableRules(
        IReadOnlyList<PortableAppRule> portableRules,
        DateTimeOffset observedAtUtc,
        List<InstalledSoftwareItemDto> items,
        HashSet<(string DisplayName, string Version, string Source, string Scope)> seen,
        CancellationToken cancellationToken)
    {
        foreach (var rule in portableRules)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var displayName = NormalizeOptional(rule.DisplayName);
            var filePath = NormalizeOptional(rule.FilePath);
            if (displayName is null || filePath is null)
            {
                continue;
            }

            try
            {
                if (!File.Exists(filePath))
                {
                    continue;
                }

                var versionInfo = FileVersionInfo.GetVersionInfo(filePath);
                var observedVersion = NormalizeOptional(versionInfo.ProductVersion)
                    ?? NormalizeOptional(versionInfo.FileVersion);
                var publisher = NormalizeOptional(rule.Publisher)
                    ?? NormalizeOptional(versionInfo.CompanyName);
                var confidence = observedVersion is not null ? "Verified" : "Partial";

                AddDeduplicated(
                    items,
                    seen,
                    new InstalledSoftwareItemDto(
                        DisplayName: displayName,
                        Publisher: publisher,
                        ObservedVersionRaw: observedVersion,
                        DetectionSource: "PortableFile",
                        InstallScope: "Portable",
                        ConfidenceState: confidence,
                        ObservedAtUtc: observedAtUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
            {
                // Skip unreadable or invalid configured portable file paths without failing collection.
            }
        }
    }

    private static void AddDeduplicated(
        List<InstalledSoftwareItemDto> items,
        HashSet<(string DisplayName, string Version, string Source, string Scope)> seen,
        InstalledSoftwareItemDto item)
    {
        var key = (
            item.DisplayName,
            item.ObservedVersionRaw ?? string.Empty,
            item.DetectionSource,
            item.InstallScope);

        if (seen.Add(key))
        {
            items.Add(item);
        }
    }

    private static bool IsSystemComponent(object? rawValue) => rawValue switch
    {
        int intValue => intValue == 1,
        long longValue => longValue == 1L,
        string strValue => int.TryParse(strValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed == 1,
        _ => false
    };

    private static bool IsValidMsixVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        foreach (var ch in version)
        {
            if (!char.IsAsciiDigit(ch) && ch != '.')
            {
                return false;
            }
        }

        return Version.TryParse(version, out _);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class InventoryItemKeyComparer : IEqualityComparer<(string DisplayName, string Version, string Source, string Scope)>
    {
        public static readonly InventoryItemKeyComparer Instance = new();

        public bool Equals(
            (string DisplayName, string Version, string Source, string Scope) x,
            (string DisplayName, string Version, string Source, string Scope) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.DisplayName, y.DisplayName) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Version, y.Version) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Source, y.Source) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Scope, y.Scope);

        public int GetHashCode((string DisplayName, string Version, string Source, string Scope) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.DisplayName),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Version),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Source),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Scope));
    }
}
