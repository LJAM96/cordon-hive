using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Cordon.Shared.Windows;

/// <summary>
/// Framework-neutral state for the notification badge and connectivity/staleness warning indicator.
/// Preserves <see cref="UnresolvedCount"/> independently of <see cref="IsConnectivityWarning"/> and <see cref="IsStale"/>.
/// </summary>
public sealed class NotificationBadgeState : INotifyPropertyChanged
{
    private int _unresolvedCount;
    private bool _isConnectivityWarning;
    private bool _isStale;
    private bool _isFlyoutOpen;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int UnresolvedCount
    {
        get => _unresolvedCount;
        set
        {
            var clamped = Math.Max(0, value);
            if (_unresolvedCount == clamped)
            {
                return;
            }

            _unresolvedCount = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BadgeText));
            OnPropertyChanged(nameof(ShowBadge));
            OnPropertyChanged(nameof(AccessibleLabel));
        }
    }

    public bool IsConnectivityWarning
    {
        get => _isConnectivityWarning;
        set
        {
            if (_isConnectivityWarning == value)
            {
                return;
            }

            _isConnectivityWarning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowWarningIndicator));
            OnPropertyChanged(nameof(AccessibleLabel));
        }
    }

    public bool IsStale
    {
        get => _isStale;
        set
        {
            if (_isStale == value)
            {
                return;
            }

            _isStale = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowWarningIndicator));
            OnPropertyChanged(nameof(AccessibleLabel));
        }
    }

    public bool IsFlyoutOpen
    {
        get => _isFlyoutOpen;
        set
        {
            if (_isFlyoutOpen == value)
            {
                return;
            }

            _isFlyoutOpen = value;
            OnPropertyChanged();
        }
    }

    public string BadgeText => BadgeDisplayFormatter.FormatCount(_unresolvedCount);

    public string AccessibleLabel => BadgeDisplayFormatter.FormatAccessibleLabel(_unresolvedCount, ShowWarningIndicator);

    public bool ShowBadge => _unresolvedCount > 0;

    public bool ShowWarningIndicator => _isConnectivityWarning || _isStale;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
