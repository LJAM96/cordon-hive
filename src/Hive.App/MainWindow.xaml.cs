using System.Collections.Specialized;
using System.ComponentModel;
using Hive.App.ViewModels;
using Hive.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Hive.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    private bool _suppressSelectionChanged;
    private bool _isAuthDialogOpen;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ViewModel.NavigationItems.CollectionChanged += NavigationItems_CollectionChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        RebuildNavigationMenu();
        UpdateInfoBarSeverity();
        NavigateTo(MainViewModel.PageKeys.Overview);

        Activated += MainWindow_Activated;
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= MainWindow_Activated;
        await ViewModel.InitializeCommand.ExecuteAsync(null);
        if (ViewModel.RequiresSignIn)
        {
            await ShowSignInDialogAsync();
        }
    }

    public async Task<bool> ShowSignInDialogAsync()
    {
        var xamlRoot = Content?.XamlRoot ?? RootNavigationView.XamlRoot;
        if (_isAuthDialogOpen || xamlRoot is null)
        {
            return false;
        }

        _isAuthDialogOpen = true;
        try
        {
            var usernameBox = new TextBox
            {
                Header = "Username",
                PlaceholderText = "Enter username"
            };
            AutomationProperties.SetName(usernameBox, "Sign In Username");

            var passwordBox = new PasswordBox
            {
                Header = "Password",
                PlaceholderText = "Enter password"
            };
            AutomationProperties.SetName(passwordBox, "Sign In Password");

            var errorText = CreateErrorTextBlock();

            var panel = new StackPanel
            {
                Spacing = 12,
                MinWidth = 340
            };
            panel.Children.Add(new TextBlock
            {
                Text = $"Sign in to {ViewModel.ServerBaseUrl} with your local Cordon Hive credentials.",
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(usernameBox);
            panel.Children.Add(passwordBox);
            panel.Children.Add(errorText);

            var dialog = new ContentDialog
            {
                Title = "Sign In to Cordon Hive",
                Content = panel,
                PrimaryButtonText = "Sign In",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            var signedIn = false;
            dialog.PrimaryButtonClick += async (sender, args) =>
            {
                var deferral = args.GetDeferral();
                sender.IsPrimaryButtonEnabled = false;
                try
                {
                    errorText.Visibility = Visibility.Collapsed;
                    var error = await ViewModel.SignInAsync(usernameBox.Text, passwordBox.Password);
                    if (error is not null)
                    {
                        errorText.Text = error;
                        errorText.Visibility = Visibility.Visible;
                        args.Cancel = true;
                        return;
                    }

                    signedIn = true;
                }
                finally
                {
                    sender.IsPrimaryButtonEnabled = true;
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
            passwordBox.Password = string.Empty;
            return signedIn;
        }
        finally
        {
            _isAuthDialogOpen = false;
        }
    }

    public async Task<bool> ShowChangePasswordDialogAsync()
    {
        var xamlRoot = Content?.XamlRoot ?? RootNavigationView.XamlRoot;
        if (_isAuthDialogOpen || xamlRoot is null)
        {
            return false;
        }

        _isAuthDialogOpen = true;
        try
        {
            var currentPasswordBox = new PasswordBox
            {
                Header = "Current Password",
                PlaceholderText = "Enter current password"
            };
            AutomationProperties.SetName(currentPasswordBox, "Current Password");

            var newPasswordBox = new PasswordBox
            {
                Header = "New Password",
                PlaceholderText = "At least 12 characters"
            };
            AutomationProperties.SetName(newPasswordBox, "New Password");

            var confirmPasswordBox = new PasswordBox
            {
                Header = "Confirm New Password",
                PlaceholderText = "Re-enter new password"
            };
            AutomationProperties.SetName(confirmPasswordBox, "Confirm New Password");

            var errorText = CreateErrorTextBlock();

            var panel = new StackPanel
            {
                Spacing = 12,
                MinWidth = 360
            };
            panel.Children.Add(new TextBlock
            {
                Text = "Choose a passphrase of at least 12 characters.",
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(currentPasswordBox);
            panel.Children.Add(newPasswordBox);
            panel.Children.Add(confirmPasswordBox);
            panel.Children.Add(errorText);

            var dialog = new ContentDialog
            {
                Title = "Change Password",
                Content = panel,
                PrimaryButtonText = "Update Password",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot
            };

            var updated = false;
            dialog.PrimaryButtonClick += async (sender, args) =>
            {
                var deferral = args.GetDeferral();
                sender.IsPrimaryButtonEnabled = false;
                try
                {
                    errorText.Visibility = Visibility.Collapsed;
                    if (!string.Equals(newPasswordBox.Password, confirmPasswordBox.Password, StringComparison.Ordinal))
                    {
                        errorText.Text = "New password and confirmation do not match.";
                        errorText.Visibility = Visibility.Visible;
                        args.Cancel = true;
                        return;
                    }

                    var error = await ViewModel.ChangePasswordAsync(
                        currentPasswordBox.Password,
                        newPasswordBox.Password);

                    if (error is not null)
                    {
                        errorText.Text = error;
                        errorText.Visibility = Visibility.Visible;
                        args.Cancel = true;
                        return;
                    }

                    updated = true;
                }
                finally
                {
                    sender.IsPrimaryButtonEnabled = true;
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
            currentPasswordBox.Password = string.Empty;
            newPasswordBox.Password = string.Empty;
            confirmPasswordBox.Password = string.Empty;
            return updated;
        }
        finally
        {
            _isAuthDialogOpen = false;
        }
    }

    private static TextBlock CreateErrorTextBlock()
    {
        var errorText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        if (Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var criticalBrushObj) &&
            criticalBrushObj is Brush criticalBrush)
        {
            errorText.Foreground = criticalBrush;
        }

        return errorText;
    }

    private void NavigationItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildNavigationMenu();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.BannerState))
        {
            UpdateInfoBarSeverity();
        }
    }

    private void RebuildNavigationMenu()
    {
        _suppressSelectionChanged = true;
        try
        {
            RootNavigationView.MenuItems.Clear();
            RootNavigationView.FooterMenuItems.Clear();

            foreach (var item in ViewModel.NavigationItems)
            {
                var navItem = new NavigationViewItem
                {
                    Content = item.Label,
                    Tag = item.PageKey,
                    Icon = new SymbolIcon(item.Icon)
                };
                AutomationProperties.SetName(navItem, item.Label);

                if (item.IsFooter)
                {
                    RootNavigationView.FooterMenuItems.Add(navItem);
                }
                else
                {
                    RootNavigationView.MenuItems.Add(navItem);
                }
            }

            if (!ViewModel.CanNavigateTo(ViewModel.SelectedPageKey))
            {
                NavigateTo(MainViewModel.PageKeys.Overview);
            }
            else
            {
                SyncSelectedNavItem(ViewModel.SelectedPageKey);
            }
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    private void UpdateInfoBarSeverity()
    {
        ConnectivityInfoBar.Severity = ViewModel.BannerState switch
        {
            ConnectivityBannerState.Connected => InfoBarSeverity.Success,
            ConnectivityBannerState.Loading => InfoBarSeverity.Informational,
            ConnectivityBannerState.Offline => InfoBarSeverity.Warning,
            ConnectivityBannerState.AuthError => InfoBarSeverity.Error,
            ConnectivityBannerState.PermissionDenied => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational
        };
    }

    private void RootNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressSelectionChanged)
        {
            return;
        }

        if (args.SelectedItemContainer?.Tag is string pageKey)
        {
            NavigateTo(pageKey);
        }
    }

    public bool NavigateTo(string pageKey, object? parameter = null)
    {
        if (!ViewModel.CanNavigateTo(pageKey))
        {
            ViewModel.ReportPermissionDenied(pageKey);
            SyncSelectedNavItem(ViewModel.SelectedPageKey);
            return false;
        }

        var pageType = ResolvePageType(pageKey);
        if (pageType is null)
        {
            return false;
        }

        if (ContentFrame.CurrentSourcePageType != pageType || parameter is not null)
        {
            ContentFrame.Navigate(pageType, parameter);
        }

        ViewModel.SelectedPageKey = pageKey;
        var navHighlightKey = string.Equals(pageKey, MainViewModel.PageKeys.WorkstationDetail, StringComparison.Ordinal)
            ? MainViewModel.PageKeys.Workstations
            : pageKey;
        SyncSelectedNavItem(navHighlightKey);
        return true;
    }

    public bool NavigateToWorkstationDetail(Guid deviceId)
    {
        if (!ViewModel.CanNavigateTo(MainViewModel.PageKeys.Workstations) ||
            !ViewModel.CanNavigateTo(MainViewModel.PageKeys.WorkstationDetail))
        {
            ViewModel.ReportPermissionDenied(MainViewModel.PageKeys.WorkstationDetail);
            SyncSelectedNavItem(ViewModel.SelectedPageKey);
            return false;
        }

        return NavigateTo(MainViewModel.PageKeys.WorkstationDetail, deviceId);
    }

    private void SyncSelectedNavItem(string pageKey)
    {
        _suppressSelectionChanged = true;
        try
        {
            var match = RootNavigationView.MenuItems
                .OfType<NavigationViewItem>()
                .Concat(RootNavigationView.FooterMenuItems.OfType<NavigationViewItem>())
                .FirstOrDefault(i => string.Equals(i.Tag as string, pageKey, StringComparison.Ordinal));

            if (match is not null)
            {
                RootNavigationView.SelectedItem = match;
            }
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    private async void BannerActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.RequiresSignIn)
        {
            await ShowSignInDialogAsync();
            return;
        }

        await ViewModel.InitializeCommand.ExecuteAsync(null);
    }

    private async void ChangePasswordBannerButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowChangePasswordDialogAsync();
    }

    private static Type? ResolvePageType(string pageKey) => pageKey switch
    {
        MainViewModel.PageKeys.Overview => typeof(OverviewPage),
        MainViewModel.PageKeys.Workstations => typeof(WorkstationsPage),
        MainViewModel.PageKeys.WorkstationDetail => typeof(WorkstationDetailPage),
        MainViewModel.PageKeys.Resources => typeof(ResourcesPage),
        MainViewModel.PageKeys.Software => typeof(SoftwarePage),
        MainViewModel.PageKeys.Deployments => typeof(DeploymentsPage),
        MainViewModel.PageKeys.Updates => typeof(UpdatesPage),
        MainViewModel.PageKeys.Notifications => typeof(NotificationsPage),
        MainViewModel.PageKeys.Audit => typeof(AuditPage),
        MainViewModel.PageKeys.Settings => typeof(SettingsPage),
        _ => null
    };
}
