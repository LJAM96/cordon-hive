using Hive.App.Services;
using Hive.App.ViewModels;
using Microsoft.UI.Xaml;

namespace Hive.App;

public partial class App : Application
{
    private Window? _mainWindow;

    public static AppSettings Settings { get; } = AppSettings.Load();
    public static HiveApiClient ApiClient { get; } = new(Settings);
    public static LocalAgentIpcClient LocalAgentIpcClient { get; } = new();
    public static MainViewModel MainViewModel { get; } = new(ApiClient, Settings);
    public static WorkstationsViewModel WorkstationsViewModel { get; } = new(ApiClient, MainViewModel);
    public static WorkstationDetailViewModel WorkstationDetailViewModel { get; } = new(ApiClient, MainViewModel);
    public static ResourcesViewModel ResourcesViewModel { get; } = new(LocalAgentIpcClient, ApiClient, MainViewModel);
    public static SoftwareViewModel SoftwareViewModel { get; } = new(ApiClient, MainViewModel);
    public static MainWindow? MainWindowInstance { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var mainWindow = new MainWindow(MainViewModel);
        _mainWindow = mainWindow;
        MainWindowInstance = mainWindow;
        _mainWindow.Activate();
    }
}
