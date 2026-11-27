using Hive.Contracts;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Hive.App.Services;

/// <summary>
/// Safely wraps Windows App SDK <see cref="AppNotificationManager"/> for native toast delivery.
/// Unpackaged runs without registered app notification identity degrade gracefully without throwing.
/// </summary>
public sealed class AppToastNotifier
{
    private const string NotificationGroup = "cordon-hive-notifications";

    private readonly object _gate = new();
    private readonly HashSet<string> _shownVersionKeys = new(StringComparer.Ordinal);
    private bool _initialized;
    private bool _isAvailable;

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _isAvailable;
            }
        }
    }

    public event EventHandler<Guid>? NotificationActivated;

    public void Initialize()
    {
        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            try
            {
                var manager = AppNotificationManager.Default;
                manager.NotificationInvoked += OnNotificationInvoked;
                manager.Register();
                _isAvailable = true;
            }
            catch
            {
                // Unpackaged dev/test runs without registered COM/app identity must degrade cleanly.
                _isAvailable = false;
            }
        }
    }

    public bool TryShowNotification(UserNotificationDto notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (notification.CancelledAtUtc is not null || notification.ReadAtUtc is not null)
        {
            return false;
        }

        Initialize();

        var dedupeKey = $"{notification.NotificationId:N}:{notification.Revision}";
        lock (_gate)
        {
            if (!_isAvailable || !_shownVersionKeys.Add(dedupeKey))
            {
                return false;
            }
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(SanitizeText(notification.Title, "Cordon Hive Notification"))
                .AddText(SanitizeText(notification.SafeSummary, "Open Cordon Hive for details."))
                .SetTag(notification.NotificationId.ToString("N"))
                .SetGroup(NotificationGroup)
                .AddArgument("notificationId", notification.NotificationId.ToString("D"));

            var appNotification = builder.BuildNotification();
            AppNotificationManager.Default.Show(appNotification);
            return true;
        }
        catch
        {
            lock (_gate)
            {
                _shownVersionKeys.Remove(dedupeKey);
            }

            return false;
        }
    }

    public void ResetSessionTracking()
    {
        lock (_gate)
        {
            _shownVersionKeys.Clear();
        }
    }

    public void Unregister()
    {
        lock (_gate)
        {
            if (!_initialized || !_isAvailable)
            {
                return;
            }

            try
            {
                AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
                AppNotificationManager.Default.Unregister();
            }
            catch
            {
                // Ignore teardown errors on exit.
            }
            finally
            {
                _isAvailable = false;
                _initialized = false;
            }
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments is not null &&
            args.Arguments.TryGetValue("notificationId", out var rawId) &&
            Guid.TryParse(rawId, out var notificationId))
        {
            NotificationActivated?.Invoke(this, notificationId);
        }
    }

    private static string SanitizeText(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
