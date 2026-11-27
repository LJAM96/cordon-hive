using Hive.App.Services;
using Hive.App.ViewModels;
using Hive.App.Views;
using Microsoft.UI.Xaml;

namespace Hive.App;

public partial class App : Application
{
    private Window? _mainWindow;
    private static NotificationWidgetWindow? _widgetWindow;

    public static AppSettings Settings { get; } = AppSettings.Load();
    public static HiveApiClient ApiClient { get; } = new(Settings);
    public static LocalAgentIpcClient LocalAgentIpcClient { get; } = new();
    public static UserNotificationCache NotificationCache { get; } = new();
    public static AppToastNotifier ToastNotifier { get; } = new();
    public static MainViewModel MainViewModel { get; } = new(ApiClient, Settings);
    public static WorkstationsViewModel WorkstationsViewModel { get; } = new(ApiClient, MainViewModel);
    public static WorkstationDetailViewModel WorkstationDetailViewModel { get; } = new(ApiClient, MainViewModel);
    public static ResourcesViewModel ResourcesViewModel { get; } = new(LocalAgentIpcClient, ApiClient, MainViewModel);
    public static SoftwareViewModel SoftwareViewModel { get; } = new(ApiClient, MainViewModel);
    public static NotificationsViewModel NotificationsViewModel { get; } = new(ApiClient, MainViewModel, NotificationCache, ToastNotifier);
    public static MainWindow? MainWindowInstance { get; private set; }

    public App()
    {
        InitializeComponent();
        NotificationsViewModel.WidgetStateChanged += (_, _) => SyncWidgetWithViewModel();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ToastNotifier.Initialize();
        ToastNotifier.NotificationActivated += ToastNotifier_NotificationActivated;

        var mainWindow = new MainWindow(MainViewModel);
        _mainWindow = mainWindow;
        MainWindowInstance = mainWindow;
        mainWindow.Closed += MainWindow_Closed;
        _mainWindow.Activate();
    }

    public static void ToggleNotificationWidget()
    {
        if (_widgetWindow is not null)
        {
            CloseNotificationWidget();
            return;
        }

        var widget = new NotificationWidgetWindow();
        widget.OpenNotificationsRequested += (_, _) => ActivateMainWindowToNotifications();
        widget.Closed += (_, _) =>
        {
            if (ReferenceEquals(_widgetWindow, widget))
            {
                _widgetWindow = null;
                NotificationsViewModel.UpdateWidgetStatusLabel(isOpen: false, isTrayFallback: false);
            }
        };

        _widgetWindow = widget;
        SyncWidgetWithViewModel();
        widget.ShowAtTaskbar();
        NotificationsViewModel.UpdateWidgetStatusLabel(isOpen: true, widget.IsUsingTrayFallback);
    }

    public static void CloseNotificationWidget()
    {
        var widget = _widgetWindow;
        _widgetWindow = null;
        if (widget is not null)
        {
            widget.Dispose();
        }

        NotificationsViewModel.UpdateWidgetStatusLabel(isOpen: false, isTrayFallback: false);
    }

    private static void SyncWidgetWithViewModel()
    {
        if (_widgetWindow is null)
        {
            return;
        }

        _widgetWindow.UpdateStatus(
            NotificationsViewModel.UnresolvedCount,
            NotificationsViewModel.IsStaleOrOffline,
            NotificationsViewModel.IsStaleOrOffline,
            NotificationsViewModel.BuildWidgetPreviewItems());
    }

    private static void ActivateMainWindowToNotifications()
    {
        var mainWindow = MainWindowInstance;
        if (mainWindow is null)
        {
            return;
        }

        mainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            mainWindow.Activate();
            mainWindow.NavigateTo(MainViewModel.PageKeys.Notifications);
        });
    }

    private static void ToastNotifier_NotificationActivated(object? sender, Guid notificationId)
    {
        var mainWindow = MainWindowInstance;
        if (mainWindow is null)
        {
            return;
        }

        mainWindow.DispatcherQueue.TryEnqueue(async () =>
        {
            mainWindow.Activate();
            if (mainWindow.NavigateTo(MainViewModel.PageKeys.Notifications))
            {
                await NotificationsViewModel.LoadCommand.ExecuteAsync(null);
            }
        });
    }

    private static void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        CloseNotificationWidget();
        ToastNotifier.NotificationActivated -= ToastNotifier_NotificationActivated;
        ToastNotifier.Unregister();
    }
}
