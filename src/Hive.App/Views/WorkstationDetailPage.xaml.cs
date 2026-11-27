using Hive.App.Services;
using Hive.App.ViewModels;
using Hive.Contracts;
using Hive.Domain.RemoteAccess;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Hive.App.Views;

public sealed partial class WorkstationDetailPage : Page
{
    private const string MaskedPasswordPlaceholder = "••••••••••••••••";

    public WorkstationDetailViewModel ViewModel => App.WorkstationDetailViewModel;

    public WorkstationDetailPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (!App.MainViewModel.CanNavigateTo(MainViewModel.PageKeys.WorkstationDetail))
        {
            App.MainViewModel.ReportPermissionDenied(MainViewModel.PageKeys.WorkstationDetail);
            App.MainWindowInstance?.NavigateTo(MainViewModel.PageKeys.Overview);
            return;
        }

        if (e.Parameter is Guid deviceId && deviceId != Guid.Empty)
        {
            await ViewModel.LoadAsync(deviceId);
        }
        else if (App.WorkstationsViewModel.SelectedWorkstation is { } selected)
        {
            await ViewModel.LoadAsync(selected.DeviceId);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindowInstance?.NavigateTo(MainViewModel.PageKeys.Workstations);
    }

    private async void LaunchLanRdpButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var reason = await PromptReasonDialogAsync(
            title: "Launch LAN RDP",
            description: $"Enter the operational reason for initiating a LAN RDP session to {ViewModel.Hostname}.",
            primaryButtonText: "Check Policy");
        if (reason is null)
        {
            return;
        }

        var authResult = await ViewModel.RequestRdpAsync(reason, useEscrowedCredential: false);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            ViewModel.SetRemoteAccessFeedback(
                InfoBarSeverity.Error,
                "RDP Request Rejected",
                authResult.ErrorMessage ?? "Server rejected the RDP authorization request.");
            return;
        }

        var auth = authResult.Value;
        if (!auth.Allowed || string.IsNullOrWhiteSpace(auth.ApprovedHostname))
        {
            ViewModel.SetRemoteAccessFeedback(
                InfoBarSeverity.Warning,
                "RDP Blocked by Policy",
                auth.BlockedReason ?? "Remote access is currently blocked for this workstation.");
            return;
        }

        var confirmed = await ConfirmRdpLaunchDialogAsync(auth, isAssisted: false);
        if (!confirmed)
        {
            return;
        }

        ViewModel.SetRemoteAccessFeedback(
            InfoBarSeverity.Informational,
            "Launching Native RDP",
            $"Starting mstsc.exe for {auth.ApprovedHostname}:{auth.RdpPort} (NLA/TLS enforced, redirection disabled)...");

        var launchResult = await Win32RdpLauncher.LaunchLanRdpAsync(auth.ApprovedHostname, auth.RdpPort);
        if (!launchResult.Launched)
        {
            ViewModel.SetRemoteAccessFeedback(
                InfoBarSeverity.Error,
                "RDP Launch Failed",
                launchResult.ErrorMessage ?? "Unable to launch mstsc.exe.");
            return;
        }

        ViewModel.SetRemoteAccessFeedback(
            InfoBarSeverity.Success,
            "LAN RDP Launched",
            $"Native mstsc.exe launched for {auth.ApprovedHostname}:{auth.RdpPort}.");
    }

    private async void AssistedRdpButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var reason = await PromptReasonDialogAsync(
            title: "Assisted RDP as Escrowed User",
            description: $"Enter the mandatory forensic/operational reason for break-glass assisted RDP to {ViewModel.Hostname}. This checkout is audited.",
            primaryButtonText: "Authorize & Continue");
        if (reason is null)
        {
            return;
        }

        var authResult = await ViewModel.RequestRdpAsync(reason, useEscrowedCredential: true);
        if (!authResult.IsSuccess || authResult.Value is null)
        {
            ViewModel.SetRemoteAccessFeedback(
                InfoBarSeverity.Error,
                "Assisted RDP Rejected",
                authResult.ErrorMessage ?? "Server rejected the assisted RDP authorization request.");
            return;
        }

        var auth = authResult.Value;
        if (!auth.Allowed || string.IsNullOrWhiteSpace(auth.ApprovedHostname))
        {
            ViewModel.SetRemoteAccessFeedback(
                InfoBarSeverity.Warning,
                "Assisted RDP Blocked by Policy",
                auth.BlockedReason ?? "Remote access is currently blocked for this workstation.");
            return;
        }

        var confirmed = await ConfirmRdpLaunchDialogAsync(auth, isAssisted: true);
        if (!confirmed)
        {
            return;
        }

        var revealResult = await ViewModel.RevealEscrowCredentialAsync(reason, EscrowRevealPurposes.AssistedRdp);
        if (!revealResult.IsSuccess || revealResult.Value is null)
        {
            ViewModel.SetRemoteAccessFeedback(
                InfoBarSeverity.Error,
                "Break-Glass Checkout Failed",
                revealResult.ErrorMessage ?? "Unable to retrieve escrowed workstation credential.");
            return;
        }

        var reveal = revealResult.Value;
        ViewModel.SetRemoteAccessFeedback(
            InfoBarSeverity.Informational,
            "Launching Assisted RDP",
            $"Injecting ephemeral TERMSRV/{auth.ApprovedHostname} session credential and starting mstsc.exe...");

        var launchResult = await Win32RdpLauncher.LaunchAssistedRdpAsync(
            auth.ApprovedHostname,
            auth.RdpPort,
            reveal.AccountUsername,
            reveal.Password);

        if (launchResult.Launched)
        {
            ViewModel.SetRemoteAccessFeedback(
                InfoBarSeverity.Success,
                "Assisted RDP Launched",
                $"Launched mstsc.exe for {auth.ApprovedHostname} as {reveal.AccountUsername}; temporary TERMSRV credential deleted.");
            return;
        }

        if (launchResult.CredentialStoreBlocked)
        {
            ViewModel.SetRemoteAccessFeedback(
                InfoBarSeverity.Warning,
                "Credential Store Blocked by OS Policy",
                $"{launchResult.ErrorMessage} Falling back to audited reveal dialog for manual NLA entry.");

            await ShowMaskedRevealDialogAsync(
                reveal,
                fallbackBannerMessage: "Windows Credential Manager / Credential Guard blocked ephemeral TERMSRV injection. Use the audited credential below at the NLA prompt.");
            return;
        }

        ViewModel.SetRemoteAccessFeedback(
            InfoBarSeverity.Error,
            "Assisted RDP Launch Failed",
            launchResult.ErrorMessage ?? "Unable to launch mstsc.exe.");
    }

    private async void RevealPhysicalSignInButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var reason = await PromptReasonDialogAsync(
            title: "Reveal Credential for Physical Sign-In",
            description: $"Enter the mandatory operational reason for revealing {ViewModel.Hostname}'s break-glass credential. This action writes an immutable audit event.",
            primaryButtonText: "Reveal Credential");
        if (reason is null)
        {
            return;
        }

        var revealResult = await ViewModel.RevealEscrowCredentialAsync(reason, EscrowRevealPurposes.PhysicalSignIn);
        if (!revealResult.IsSuccess || revealResult.Value is null)
        {
            ViewModel.SetRemoteAccessFeedback(
                InfoBarSeverity.Error,
                "Break-Glass Reveal Failed",
                revealResult.ErrorMessage ?? "Unable to reveal escrowed workstation credential.");
            return;
        }

        ViewModel.SetRemoteAccessFeedback(
            InfoBarSeverity.Success,
            "Break-Glass Reveal Audited",
            $"Checked out credential for {revealResult.Value.AccountUsername} (Correlation ID: {revealResult.Value.AuditCorrelationId}).");

        await ShowMaskedRevealDialogAsync(revealResult.Value);
    }

    private async void RotateEscrowButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var usernameBox = new TextBox
        {
            Header = "Workstation Login Username",
            PlaceholderText = "e.g. .\\examiner or DOMAIN\\examiner",
            Text = ViewModel.EscrowedAccountUsername
        };
        AutomationProperties.SetName(usernameBox, "Escrow Account Username");

        var passwordBox = new PasswordBox
        {
            Header = "New Workstation Password",
            PlaceholderText = "Enter workstation login password"
        };
        AutomationProperties.SetName(passwordBox, "Escrow New Password");

        var confirmPasswordBox = new PasswordBox
        {
            Header = "Confirm Workstation Password",
            PlaceholderText = "Re-enter workstation password"
        };
        AutomationProperties.SetName(confirmPasswordBox, "Escrow Confirm Password");

        var notesBox = new TextBox
        {
            Header = "Rotation Note (optional)",
            PlaceholderText = "Reason for manual update / rotation"
        };
        AutomationProperties.SetName(notesBox, "Escrow Rotation Notes");

        var errorText = CreateErrorTextBlock();

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Update the encrypted break-glass credential for {ViewModel.Hostname}. Passwords are stored write-only and never returned after saving.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(usernameBox);
        panel.Children.Add(passwordBox);
        panel.Children.Add(confirmPasswordBox);
        panel.Children.Add(notesBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "Update / Rotate Break-Glass Escrow",
            Content = panel,
            PrimaryButtonText = "Save Credential",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var saved = false;
        dialog.PrimaryButtonClick += async (s, args) =>
        {
            var deferral = args.GetDeferral();
            s.IsPrimaryButtonEnabled = false;
            try
            {
                errorText.Visibility = Visibility.Collapsed;
                var username = usernameBox.Text?.Trim() ?? string.Empty;
                var password = passwordBox.Password ?? string.Empty;
                var confirm = confirmPasswordBox.Password ?? string.Empty;

                if (string.IsNullOrWhiteSpace(username))
                {
                    errorText.Text = "Workstation login username is required.";
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }

                if (string.IsNullOrEmpty(password))
                {
                    errorText.Text = "Workstation password is required.";
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }

                if (!string.Equals(password, confirm, StringComparison.Ordinal))
                {
                    errorText.Text = "Passwords do not match.";
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }

                var result = await ViewModel.UpsertEscrowCredentialAsync(username, password, notesBox.Text);
                if (!result.IsSuccess)
                {
                    errorText.Text = result.ErrorMessage ?? "Unable to update break-glass escrow.";
                    errorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                    return;
                }

                saved = true;
            }
            finally
            {
                s.IsPrimaryButtonEnabled = true;
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
        passwordBox.Password = string.Empty;
        confirmPasswordBox.Password = string.Empty;

        if (saved)
        {
            ViewModel.SetRemoteAccessFeedback(
                InfoBarSeverity.Success,
                "Break-Glass Escrow Updated",
                $"Escrowed credential updated ({ViewModel.EscrowStatusText}).");
        }
    }

    private async Task<string?> PromptReasonDialogAsync(
        string title,
        string description,
        string primaryButtonText)
    {
        if (XamlRoot is null)
        {
            return null;
        }

        var reasonBox = new TextBox
        {
            Header = "Operational Reason (required)",
            PlaceholderText = "e.g. Case DFU-2026-041 urgent extraction check",
            AcceptsReturn = false
        };
        AutomationProperties.SetName(reasonBox, "Remote Access Reason");

        var errorText = CreateErrorTextBlock();

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(reasonBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        string? capturedReason = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var trimmed = reasonBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                errorText.Text = "A non-empty operational reason is required for audit attribution.";
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            capturedReason = trimmed;
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? capturedReason : null;
    }

    private async Task<bool> ConfirmRdpLaunchDialogAsync(RdpAuthorizationResponse auth, bool isAssisted)
    {
        if (XamlRoot is null)
        {
            return false;
        }

        var panel = new StackPanel { Spacing = 10, MinWidth = 400 };
        panel.Children.Add(new TextBlock
        {
            Text = isAssisted
                ? $"Target: {auth.ApprovedHostname}:{auth.RdpPort} (Assisted RDP as escrowed user)"
                : $"Target: {auth.ApprovedHostname}:{auth.RdpPort}",
            Style = Application.Current.Resources["BodyStrongTextBlockStyle"] as Style,
            TextWrapping = TextWrapping.Wrap
        });

        if (auth.ActiveSessions.Count > 0)
        {
            var sessionBar = new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = $"Active Interactive Session Detected ({auth.ActiveSessions.Count})",
                Message = isAssisted
                    ? "Signing in as the active workstation user attaches directly to their desktop and locks the physical screen."
                    : "Connecting via RDP may disconnect or lock the currently active interactive user session."
            };
            panel.Children.Add(sessionBar);
        }

        foreach (var warning in auth.Warnings)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"• {warning}",
                TextWrapping = TextWrapping.Wrap
            });
        }

        var dialog = new ContentDialog
        {
            Title = isAssisted ? "Confirm Assisted RDP Launch" : "Confirm LAN RDP Launch",
            Content = panel,
            PrimaryButtonText = "Launch mstsc.exe",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private async Task ShowMaskedRevealDialogAsync(
        RevealEscrowCredentialResponse reveal,
        string? fallbackBannerMessage = null)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var remainingSeconds = reveal.RecommendedClearSeconds > 0
            ? reveal.RecommendedClearSeconds
            : 60;
        var secretValue = reveal.Password;
        var isRevealed = false;
        var isCleared = false;

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };

        if (!string.IsNullOrWhiteSpace(fallbackBannerMessage))
        {
            panel.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = "Ephemeral Credential Store Unavailable",
                Message = fallbackBannerMessage
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = $"Workstation: {reveal.Hostname}",
            Style = Application.Current.Resources["BodyStrongTextBlockStyle"] as Style
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Account Username: {reveal.AccountUsername}",
            IsTextSelectionEnabled = true
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Purpose: {reveal.Purpose} · Checked out {reveal.CheckedOutAtUtc.ToLocalTime():g}",
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Audit Correlation ID: {reveal.AuditCorrelationId}",
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
            IsTextSelectionEnabled = true
        });

        var passwordDisplayBox = new TextBox
        {
            Header = "Escrowed Password",
            Text = MaskedPasswordPlaceholder,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas")
        };
        AutomationProperties.SetName(passwordDisplayBox, "Revealed Escrowed Password");
        panel.Children.Add(passwordDisplayBox);

        var toggleButton = new Button
        {
            Content = "Show Password"
        };
        AutomationProperties.SetName(toggleButton, "Show or Hide Password");

        var copyButton = new Button
        {
            Content = "Copy Password"
        };
        AutomationProperties.SetName(copyButton, "Copy Escrowed Password");

        var actionFeedbackText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
        };

        var actionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        actionsRow.Children.Add(toggleButton);
        actionsRow.Children.Add(copyButton);
        actionsRow.Children.Add(actionFeedbackText);
        panel.Children.Add(actionsRow);

        var countdownText = new TextBlock
        {
            Text = $"Auto-clearing in {remainingSeconds}s",
            Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush
        };
        AutomationProperties.SetName(countdownText, "Auto-clear Countdown");
        panel.Children.Add(countdownText);

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        void ClearSecretFromDialog(string statusMessage)
        {
            timer.Stop();
            isCleared = true;
            isRevealed = false;
            secretValue = string.Empty;
            passwordDisplayBox.Text = string.Empty;
            passwordDisplayBox.PlaceholderText = "Cleared from memory";
            toggleButton.IsEnabled = false;
            copyButton.IsEnabled = false;
            countdownText.Text = statusMessage;
            actionFeedbackText.Text = string.Empty;
        }

        toggleButton.Click += (_, _) =>
        {
            if (isCleared)
            {
                return;
            }

            isRevealed = !isRevealed;
            passwordDisplayBox.Text = isRevealed ? secretValue : MaskedPasswordPlaceholder;
            toggleButton.Content = isRevealed ? "Hide Password" : "Show Password";
        };

        copyButton.Click += (_, _) =>
        {
            if (isCleared || string.IsNullOrEmpty(secretValue))
            {
                return;
            }

            var dataPackage = new DataPackage();
            dataPackage.SetText(secretValue);
            Clipboard.SetContent(dataPackage);
            actionFeedbackText.Text = "Copied to clipboard.";
        };

        timer.Tick += (_, _) =>
        {
            remainingSeconds--;
            if (remainingSeconds <= 0)
            {
                ClearSecretFromDialog("Credential cleared from memory (60s timer expired).");
                return;
            }

            countdownText.Text = $"Auto-clearing in {remainingSeconds}s";
        };

        var dialog = new ContentDialog
        {
            Title = "Audited Break-Glass Credential Reveal",
            Content = panel,
            CloseButtonText = "Close & Clear",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        dialog.Closed += (_, _) => ClearSecretFromDialog("Credential cleared.");

        timer.Start();
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
            brushObj is Brush brush)
        {
            errorText.Foreground = brush;
        }

        return errorText;
    }
}
