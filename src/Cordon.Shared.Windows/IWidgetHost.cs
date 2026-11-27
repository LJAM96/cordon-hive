namespace Cordon.Shared.Windows;

public interface IWidgetHost : IDisposable
{
    void UpdateBadge(int unresolvedCount, bool isConnectivityWarning);

    void ShowFlyout();

    void HideFlyout();
}
