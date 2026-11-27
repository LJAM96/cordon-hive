using Hive.App.ViewModels;
using Hive.Domain.Software;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Hive.App.Views;

public sealed partial class SoftwarePage : Page
{
    public SoftwareViewModel ViewModel => App.SoftwareViewModel;

    public SoftwarePage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadCommand.ExecuteAsync(null);
    }

    private async void AddProductButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var nameBox = new TextBox
        {
            Header = "Product Name",
            PlaceholderText = "e.g. Autopsy Forensic Browser"
        };
        AutomationProperties.SetName(nameBox, "Product Name");

        var publisherBox = new TextBox
        {
            Header = "Publisher",
            PlaceholderText = "e.g. Basis Technology"
        };
        AutomationProperties.SetName(publisherBox, "Publisher");

        var schemeCombo = new ComboBox
        {
            Header = "Version Scheme",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        schemeCombo.Items.Add(new ComboBoxItem { Content = "SemVer", Tag = nameof(VersionScheme.SemVer) });
        schemeCombo.Items.Add(new ComboBoxItem { Content = "NumericSegments", Tag = nameof(VersionScheme.NumericSegments) });
        schemeCombo.Items.Add(new ComboBoxItem { Content = "Exact", Tag = nameof(VersionScheme.Exact) });
        schemeCombo.SelectedIndex = 0;
        AutomationProperties.SetName(schemeCombo, "Version Scheme");

        var patternBox = new TextBox
        {
            Header = "Match Pattern (Display Name substring)",
            PlaceholderText = "e.g. Autopsy"
        };
        AutomationProperties.SetName(patternBox, "Match Pattern");

        var processesBox = new TextBox
        {
            Header = "Protected Process Names (optional, comma-separated)",
            PlaceholderText = "e.g. autopsy64.exe, java.exe"
        };
        AutomationProperties.SetName(processesBox, "Protected Process Names");

        var errorText = CreateErrorTextBlock();

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(nameBox);
        panel.Children.Add(publisherBox);
        panel.Children.Add(schemeCombo);
        panel.Children.Add(patternBox);
        panel.Children.Add(processesBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "Add Managed Software Product",
            Content = panel,
            PrimaryButtonText = "Add Product",
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
                var scheme = (schemeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? nameof(VersionScheme.SemVer);
                var error = await ViewModel.AddProductAsync(
                    nameBox.Text,
                    publisherBox.Text,
                    scheme,
                    patternBox.Text,
                    processesBox.Text);

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

    private async void DraftReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        if (ViewModel.Products.Count == 0)
        {
            await ShowSimpleNoticeAsync("No Products Available", "Add a managed software product before drafting a release.");
            return;
        }

        var productCombo = CreateProductComboBox();
        AutomationProperties.SetName(productCombo, "Release Product");

        var versionBox = new TextBox
        {
            Header = "Version",
            PlaceholderText = "e.g. 4.21.0"
        };
        AutomationProperties.SetName(versionBox, "Release Version");

        var validationBox = new TextBox
        {
            Header = "Validation Reference",
            PlaceholderText = "e.g. VAL-2026-042"
        };
        AutomationProperties.SetName(validationBox, "Validation Reference");

        var sha256Box = new TextBox
        {
            Header = "Package SHA-256 (optional)",
            PlaceholderText = "64 hex characters"
        };
        AutomationProperties.SetName(sha256Box, "Package SHA-256");

        var digestBox = new TextBox
        {
            Header = "Manifest Digest (optional)",
            PlaceholderText = "Optional manifest digest"
        };
        AutomationProperties.SetName(digestBox, "Manifest Digest");

        var errorText = CreateErrorTextBlock();

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(productCombo);
        panel.Children.Add(versionBox);
        panel.Children.Add(validationBox);
        panel.Children.Add(sha256Box);
        panel.Children.Add(digestBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "Draft Software Release",
            Content = panel,
            PrimaryButtonText = "Draft Release",
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
                var productId = (productCombo.SelectedItem as ComboBoxItem)?.Tag is Guid pid ? pid : Guid.Empty;
                var error = await ViewModel.DraftReleaseAsync(
                    productId,
                    versionBox.Text,
                    validationBox.Text,
                    sha256Box.Text,
                    digestBox.Text);

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

    private async void AddExemptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        if (ViewModel.Products.Count == 0)
        {
            await ShowSimpleNoticeAsync("No Products Available", "Add a managed software product before creating an exemption.");
            return;
        }

        var productCombo = CreateProductComboBox();
        AutomationProperties.SetName(productCombo, "Exemption Product");

        var scopeCombo = new ComboBox
        {
            Header = "Workstation Scope",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        scopeCombo.Items.Add(new ComboBoxItem { Content = "All Workstations (Global)", Tag = (Guid?)null });
        var selectedIndex = 0;
        var currentIndex = 1;
        foreach (var device in ViewModel.Devices)
        {
            scopeCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{device.Hostname} ({device.SiteId})",
                Tag = (Guid?)device.DeviceId
            });
            if (ViewModel.SelectedDevice?.DeviceId == device.DeviceId)
            {
                selectedIndex = currentIndex;
            }
            currentIndex++;
        }
        scopeCombo.SelectedIndex = selectedIndex;
        AutomationProperties.SetName(scopeCombo, "Exemption Workstation Scope");

        var allowedVersionBox = new TextBox
        {
            Header = "Allowed Version (optional; blank allows any version)",
            PlaceholderText = "e.g. 4.19.0"
        };
        AutomationProperties.SetName(allowedVersionBox, "Allowed Version");

        var reasonBox = new TextBox
        {
            Header = "Reason / Case Reference",
            PlaceholderText = "e.g. Active forensic examination continuity",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(reasonBox, "Exemption Reason");

        var durationBox = new NumberBox
        {
            Header = "Duration (days)",
            Minimum = 1,
            Maximum = 365,
            Value = 7,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };
        AutomationProperties.SetName(durationBox, "Exemption Duration Days");

        var errorText = CreateErrorTextBlock();

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(productCombo);
        panel.Children.Add(scopeCombo);
        panel.Children.Add(allowedVersionBox);
        panel.Children.Add(reasonBox);
        panel.Children.Add(durationBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "Add Time-Bounded Exemption",
            Content = panel,
            PrimaryButtonText = "Create Exemption",
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
                var productId = (productCombo.SelectedItem as ComboBoxItem)?.Tag is Guid pid ? pid : Guid.Empty;
                var deviceId = (scopeCombo.SelectedItem as ComboBoxItem)?.Tag as Guid?;
                var days = double.IsNaN(durationBox.Value) ? 7 : (int)Math.Round(durationBox.Value);

                var error = await ViewModel.AddExemptionAsync(
                    deviceId,
                    productId,
                    allowedVersionBox.Text,
                    reasonBox.Text,
                    days);

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

    private ComboBox CreateProductComboBox()
    {
        var combo = new ComboBox
        {
            Header = "Software Product",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        foreach (var product in ViewModel.Products)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = $"{product.Name} ({product.VersionScheme})",
                Tag = product.ProductId
            });
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }

        return combo;
    }

    private async Task ShowSimpleNoticeAsync(string title, string message)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
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
