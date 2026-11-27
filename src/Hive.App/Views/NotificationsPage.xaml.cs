using Hive.App.ViewModels;
using Hive.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Hive.App.Views;

public sealed partial class NotificationsPage : Page
{
    public NotificationsViewModel ViewModel => App.NotificationsViewModel;

    public NotificationsPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private async void MarkReadButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is NotificationItemViewModel item)
        {
            await ViewModel.MarkReadAsync(item);
        }
    }

    private async void AcknowledgeButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is NotificationItemViewModel item)
        {
            await ViewModel.AcknowledgeAsync(item);
        }
    }

    private async void CompleteButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is NotificationItemViewModel item)
        {
            await ViewModel.CompleteAsync(item);
        }
    }

    private void ToggleWidgetButton_Click(object sender, RoutedEventArgs e)
    {
        App.ToggleNotificationWidget();
    }

    private async void ComposeButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var titleBox = new TextBox
        {
            Header = "Title",
            PlaceholderText = "Concise notification headline"
        };
        AutomationProperties.SetName(titleBox, "Notification Title");

        var summaryBox = new TextBox
        {
            Header = "Safe Summary (non-sensitive)",
            PlaceholderText = "Brief safe summary suitable for toast and lock-screen previews",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 68
        };
        AutomationProperties.SetName(summaryBox, "Notification Safe Summary");

        var typeCombo = new ComboBox
        {
            Header = "Notification Type",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var type in Enum.GetValues<NotificationType>())
        {
            typeCombo.Items.Add(new ComboBoxItem
            {
                Content = NotificationPresentation.GetTypeLabel(type.ToString()),
                Tag = type.ToString()
            });
        }
        typeCombo.SelectedIndex = 1; // Announcement default
        AutomationProperties.SetName(typeCombo, "Notification Type");

        var priorityCombo = new ComboBox
        {
            Header = "Priority",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var priority in Enum.GetValues<NotificationPriority>())
        {
            priorityCombo.Items.Add(new ComboBoxItem { Content = priority.ToString(), Tag = priority.ToString() });
        }
        priorityCombo.SelectedIndex = 1; // Normal default
        AutomationProperties.SetName(priorityCombo, "Notification Priority");

        var targetTypeCombo = new ComboBox
        {
            Header = "Target Type",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        targetTypeCombo.Items.Add(new ComboBoxItem { Content = "All Users", Tag = nameof(NotificationTargetType.AllUsers) });
        targetTypeCombo.Items.Add(new ComboBoxItem { Content = "Specific User SIDs", Tag = nameof(NotificationTargetType.User) });
        targetTypeCombo.Items.Add(new ComboBoxItem { Content = "Specific Device UUIDs", Tag = nameof(NotificationTargetType.Device) });
        targetTypeCombo.Items.Add(new ComboBoxItem { Content = "Specific Site IDs", Tag = nameof(NotificationTargetType.Site) });
        targetTypeCombo.SelectedIndex = 0;
        AutomationProperties.SetName(targetTypeCombo, "Notification Target Type");

        var targetKeysBox = new TextBox
        {
            Header = "Target Keys (comma-separated)",
            PlaceholderText = "Not required when All Users is selected",
            IsEnabled = false
        };
        AutomationProperties.SetName(targetKeysBox, "Notification Target Keys");

        targetTypeCombo.SelectionChanged += (_, _) =>
        {
            var selected = (targetTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            var isAllUsers = string.Equals(selected, nameof(NotificationTargetType.AllUsers), StringComparison.Ordinal);
            targetKeysBox.IsEnabled = !isAllUsers;
            if (isAllUsers)
            {
                targetKeysBox.Text = string.Empty;
                targetKeysBox.PlaceholderText = "Not required when All Users is selected";
            }
            else
            {
                targetKeysBox.PlaceholderText = selected switch
                {
                    nameof(NotificationTargetType.User) => "e.g. S-1-5-21-..., S-1-5-21-...",
                    nameof(NotificationTargetType.Device) => "e.g. device UUID(s)",
                    nameof(NotificationTargetType.Site) => "e.g. site-a, site-b",
                    _ => "Comma-separated keys"
                };
            }
        };

        var hasDueCheck = new CheckBox { Content = "Set due date (hours from now)" };
        var dueHoursBox = new NumberBox
        {
            Minimum = 1,
            Maximum = 720,
            Value = 24,
            IsEnabled = false,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        AutomationProperties.SetName(dueHoursBox, "Due In Hours");
        hasDueCheck.Checked += (_, _) => dueHoursBox.IsEnabled = true;
        hasDueCheck.Unchecked += (_, _) => dueHoursBox.IsEnabled = false;

        var errorText = CreateErrorTextBlock();

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(titleBox);
        panel.Children.Add(summaryBox);
        panel.Children.Add(typeCombo);
        panel.Children.Add(priorityCombo);
        panel.Children.Add(targetTypeCombo);
        panel.Children.Add(targetKeysBox);
        panel.Children.Add(hasDueCheck);
        panel.Children.Add(dueHoursBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "Compose Notification",
            Content = panel,
            PrimaryButtonText = "Publish",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        dialog.PrimaryButtonClick += async (s, args) =>
        {
            var deferral = args.GetDeferral();
            s.IsPrimaryButtonEnabled = false;
            try
            {
                errorText.Visibility = Visibility.Collapsed;
                var type = (typeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? nameof(NotificationType.Announcement);
                var priority = (priorityCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? nameof(NotificationPriority.Normal);
                var targetType = (targetTypeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? nameof(NotificationTargetType.AllUsers);

                DateTimeOffset? dueAtUtc = null;
                if (hasDueCheck.IsChecked == true && !double.IsNaN(dueHoursBox.Value))
                {
                    dueAtUtc = DateTimeOffset.UtcNow.AddHours(Math.Clamp(dueHoursBox.Value, 1, 720));
                }

                var error = await ViewModel.PublishNotificationAsync(
                    titleBox.Text,
                    summaryBox.Text,
                    type,
                    priority,
                    targetType,
                    targetKeysBox.Text,
                    dueAtUtc);

                if (error is not null)
                {
                    errorText.Text = error;
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
            }
            finally
            {
                s.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    private async void CancelNotificationButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null || (sender as FrameworkElement)?.Tag is not AdminNotificationSummaryItemViewModel item)
        {
            return;
        }

        var reasonBox = new TextBox
        {
            Header = "Cancellation Reason",
            PlaceholderText = "e.g. Superseded by updated advisory",
            Text = "Cancelled by supervisor/admin"
        };
        AutomationProperties.SetName(reasonBox, "Cancellation Reason");

        var errorText = CreateErrorTextBlock();

        var panel = new StackPanel { Spacing = 10, MinWidth = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Cancel notification \"{item.Title}\" for all recipients?",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(reasonBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "Cancel Notification",
            Content = panel,
            PrimaryButtonText = "Cancel Notification",
            CloseButtonText = "Keep Active",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        dialog.PrimaryButtonClick += async (s, args) =>
        {
            var deferral = args.GetDeferral();
            s.IsPrimaryButtonEnabled = false;
            try
            {
                errorText.Visibility = Visibility.Collapsed;
                var error = await ViewModel.CancelPublishedNotificationAsync(item, reasonBox.Text);
                if (error is not null)
                {
                    errorText.Text = error;
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
            }
            finally
            {
                s.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
    }

    private static TextBlock CreateErrorTextBlock()
    {
        var errorText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        if (Application.Current.Resources.TryGetValue("SystemFillColorCriticalBrush", out var brushObj) &&
            brushObj is Brush criticalBrush)
        {
            errorText.Foreground = criticalBrush;
        }

        return errorText;
    }
}
