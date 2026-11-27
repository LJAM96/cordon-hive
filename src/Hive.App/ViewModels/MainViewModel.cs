using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hive.App.Services;
using Hive.Contracts;
using Hive.Domain.Identity;
using Microsoft.UI.Xaml.Controls;

namespace Hive.App.ViewModels;

public enum ConnectivityBannerState
{
    Loading,
    Connected,
    Offline,
    AuthError,
    PermissionDenied
}

public sealed record NavItemDescriptor(
    string PageKey,
    string Label,
    Symbol Icon,
    bool IsFooter = false);

public sealed partial class MainViewModel : ObservableObject
{
    public static class PageKeys
    {
        public const string Overview = "Overview";
        public const string Workstations = "Workstations";
        public const string WorkstationDetail = "WorkstationDetail";
        public const string Resources = "Resources";
        public const string Software = "Software";
        public const string Deployments = "Deployments";
        public const string Updates = "Updates";
        public const string Notifications = "Notifications";
        public const string Audit = "Audit";
        public const string Settings = "Settings";
    }

    private static readonly IReadOnlySet<string> SupervisorPageKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        PageKeys.Workstations,
        PageKeys.WorkstationDetail,
        PageKeys.Deployments,
        PageKeys.Audit
    };

    private static readonly IReadOnlySet<string> ExaminerPageKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        PageKeys.Overview,
        PageKeys.Resources,
        PageKeys.Software,
        PageKeys.Updates,
        PageKeys.Notifications,
        PageKeys.Settings
    };

    private readonly HiveApiClient _apiClient;
    private readonly AppSettings _settings;
    private readonly HashSet<string> _allowedPageKeys = new(StringComparer.Ordinal)
    {
        PageKeys.Overview,
        PageKeys.Settings
    };

    [ObservableProperty]
    public partial MeResponse? CurrentUser { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignOut))]
    [NotifyPropertyChangedFor(nameof(SignInButtonLabel))]
    public partial bool IsSignedIn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannerActionLabel))]
    public partial bool RequiresSignIn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSignOut))]
    public partial bool HasStoredSession { get; set; }

    [ObservableProperty]
    public partial bool MustChangePasswordBanner { get; set; }

    [ObservableProperty]
    public partial string SignedInUserSummary { get; set; } = "Not signed in";

    [ObservableProperty]
    public partial ConnectivityBannerState BannerState { get; set; } = ConnectivityBannerState.Loading;

    [ObservableProperty]
    public partial string BannerTitle { get; set; } = "Connecting to Cordon Hive";

    [ObservableProperty]
    public partial string BannerMessage { get; set; } = "Verifying identity and site permissions on the LAN server...";

    [ObservableProperty]
    public partial bool IsBannerOpen { get; set; } = true;

    [ObservableProperty]
    public partial string SelectedPageKey { get; set; } = PageKeys.Overview;

    [ObservableProperty]
    public partial string LastCheckedLocalText { get; set; } = "Not yet checked";

    [ObservableProperty]
    public partial string UserSummaryText { get; set; } = "Not authenticated";

    [ObservableProperty]
    public partial string RolesSummaryText { get; set; } = "None";

    [ObservableProperty]
    public partial string ScopeSummaryText { get; set; } = "Unavailable";

    [ObservableProperty]
    public partial bool HasSupervisorScope { get; set; }

    public bool CanSignOut => IsSignedIn || HasStoredSession;

    public string BannerActionLabel => RequiresSignIn ? "Sign In" : "Retry";

    public string SignInButtonLabel => IsSignedIn ? "Switch Account" : "Sign In";

    public ObservableCollection<NavItemDescriptor> NavigationItems { get; } = [];

    public string ServerBaseUrl => _settings.BaseUrl;

    public MainViewModel(HiveApiClient apiClient, AppSettings settings)
    {
        _apiClient = apiClient;
        _settings = settings;
        HasStoredSession = _apiClient.HasStoredSession;
        RebuildNavigationItems(roles: new HashSet<HiveRole>());
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        BannerState = ConnectivityBannerState.Loading;
        BannerTitle = "Connecting to Cordon Hive";
        BannerMessage = _apiClient.HasStoredSession
            ? $"Restoring session with {_settings.BaseUrl}..."
            : $"Contacting {_settings.BaseUrl}...";
        IsBannerOpen = true;

        var result = await _apiClient.GetMeAsync();
        LastCheckedLocalText = DateTimeOffset.Now.ToString("g");
        HasStoredSession = _apiClient.HasStoredSession;

        if (result.IsSuccess && result.Value is not null)
        {
            ApplyAuthenticatedUser(result.Value);
            return;
        }

        // Fail closed when server is unreachable or identity is rejected.
        ResetUnauthenticatedState("Unauthenticated", "None (fail-closed)", "Restricted");

        switch (result.ErrorKind)
        {
            case ApiErrorKind.Unauthorized:
                RequiresSignIn = true;
                MustChangePasswordBanner = false;
                BannerState = ConnectivityBannerState.AuthError;
                BannerTitle = "Sign-In Required";
                BannerMessage = result.ErrorMessage ?? "Authentication required (401). Sign in with your Cordon Hive account.";
                break;
            case ApiErrorKind.Forbidden:
                RequiresSignIn = false;
                MustChangePasswordBanner = false;
                BannerState = ConnectivityBannerState.PermissionDenied;
                BannerTitle = "Access Denied";
                BannerMessage = result.ErrorMessage ?? "Your account has no active Cordon Hive role assignments (403).";
                break;
            default:
                RequiresSignIn = false;
                BannerState = ConnectivityBannerState.Offline;
                BannerTitle = "Hive Server Offline";
                BannerMessage = $"{result.ErrorMessage} Supervisor routes remain locked until identity is verified.";
                break;
        }

        IsBannerOpen = true;
    }

    public async Task<string?> SignInAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return "Username and password are required.";
        }

        var loginResult = await _apiClient.LoginAsync(username.Trim(), password);
        HasStoredSession = _apiClient.HasStoredSession;

        if (!loginResult.IsSuccess || loginResult.Value is null)
        {
            return loginResult.ErrorMessage ?? "Sign-in failed. Verify your username and password.";
        }

        MustChangePasswordBanner = loginResult.Value.MustChangePassword;

        await InitializeAsync();
        if (!IsSignedIn)
        {
            return BannerMessage;
        }

        return null;
    }

    public async Task<string?> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(currentPassword))
        {
            return "Current password is required.";
        }

        var (isValid, policyError) = PasswordPolicy.Validate(newPassword);
        if (!isValid)
        {
            return policyError ?? "New password does not meet policy requirements.";
        }

        var result = await _apiClient.ChangePasswordAsync(currentPassword, newPassword);
        if (!result.IsSuccess)
        {
            return result.ErrorMessage ?? "Unable to update password.";
        }

        MustChangePasswordBanner = false;
        ReportBanner(
            ConnectivityBannerState.Connected,
            "Password Updated",
            "Your account password has been changed.");
        return null;
    }

    [RelayCommand]
    public async Task SignOutAsync()
    {
        await _apiClient.LogoutAsync();
        HasStoredSession = _apiClient.HasStoredSession;
        MustChangePasswordBanner = false;
        RequiresSignIn = true;

        ResetUnauthenticatedState("Signed out", "None", "Restricted");

        BannerState = ConnectivityBannerState.AuthError;
        BannerTitle = "Signed Out";
        BannerMessage = "You have signed out. Click Sign In to authenticate.";
        IsBannerOpen = true;
    }

    public bool CanNavigateTo(string pageKey)
    {
        if (string.IsNullOrWhiteSpace(pageKey))
        {
            return false;
        }

        if (SupervisorPageKeys.Contains(pageKey) && !HasSupervisorScope)
        {
            return false;
        }

        return _allowedPageKeys.Contains(pageKey);
    }

    public void ReportPermissionDenied(string attemptedPageKey)
    {
        BannerState = ConnectivityBannerState.PermissionDenied;
        BannerTitle = "Route Restricted";
        BannerMessage = $"Access to '{attemptedPageKey}' requires Supervisor, Administrator, or Auditor role scope.";
        IsBannerOpen = true;
    }

    public void ReportBanner(ConnectivityBannerState state, string title, string message)
    {
        BannerState = state;
        BannerTitle = title;
        BannerMessage = message;
        IsBannerOpen = true;
    }

    private void ResetUnauthenticatedState(string userSummary, string rolesSummary, string scopeSummary)
    {
        CurrentUser = null;
        IsSignedIn = false;
        HasSupervisorScope = false;
        UserSummaryText = userSummary;
        SignedInUserSummary = "Not signed in";
        RolesSummaryText = rolesSummary;
        ScopeSummaryText = scopeSummary;
        RebuildNavigationItems(roles: new HashSet<HiveRole>());
    }

    private void ApplyAuthenticatedUser(MeResponse me)
    {
        CurrentUser = me;
        IsSignedIn = true;
        RequiresSignIn = false;

        var parsedRoles = ParseRoles(me.Roles);
        HasSupervisorScope = parsedRoles.Any(r =>
            r is HiveRole.Supervisor or HiveRole.Administrator or HiveRole.Auditor);

        UserSummaryText = $"{me.DisplayName} ({me.UserSid})";
        RolesSummaryText = parsedRoles.Count > 0
            ? string.Join(", ", parsedRoles.Select(r => r.ToString()))
            : "None";
        SignedInUserSummary = $"{me.DisplayName} ({me.UserSid}) — {RolesSummaryText}";
        ScopeSummaryText = me.AllSitesAccess
            ? "All sites"
            : me.AllowedSiteIds.Count > 0
                ? string.Join(", ", me.AllowedSiteIds)
                : "No site assignments";

        RebuildNavigationItems(parsedRoles);

        if (parsedRoles.Count == 0)
        {
            BannerState = ConnectivityBannerState.PermissionDenied;
            BannerTitle = "No Hive Roles Assigned";
            BannerMessage = "Authenticated, but no Hive role is assigned. Supervisor views remain locked.";
            IsBannerOpen = true;
            return;
        }

        BannerState = ConnectivityBannerState.Connected;
        BannerTitle = "Connected (LAN)";
        BannerMessage = $"Signed in as {me.DisplayName} ({RolesSummaryText}).";
        IsBannerOpen = true;
    }

    private void RebuildNavigationItems(IReadOnlySet<HiveRole> roles)
    {
        NavigationItems.Clear();
        _allowedPageKeys.Clear();

        // Fail closed: unauthenticated or role-less sessions only see Overview & Settings.
        if (roles.Count == 0)
        {
            AddNavItem(PageKeys.Overview, "Overview", Symbol.Home);
            AddNavItem(PageKeys.Settings, "Settings", Symbol.Setting, isFooter: true);
            return;
        }

        var isSupervisorOrHigher = roles.Any(r =>
            r is HiveRole.Supervisor or HiveRole.Administrator or HiveRole.Auditor);

        AddNavItem(PageKeys.Overview, "Overview", Symbol.Home);

        if (isSupervisorOrHigher)
        {
            AddNavItem(PageKeys.Workstations, "Workstations", Symbol.AllApps);
            _allowedPageKeys.Add(PageKeys.WorkstationDetail);
        }

        AddNavItem(PageKeys.Resources, "Resources", Symbol.Manage);
        AddNavItem(PageKeys.Software, "Software", Symbol.Library);

        if (isSupervisorOrHigher)
        {
            AddNavItem(PageKeys.Deployments, "Deployments", Symbol.Upload);
        }

        AddNavItem(PageKeys.Updates, "Updates", Symbol.Sync);
        AddNavItem(PageKeys.Notifications, "Notifications", Symbol.Mail);

        if (isSupervisorOrHigher)
        {
            AddNavItem(PageKeys.Audit, "Audit", Symbol.Document);
        }

        AddNavItem(PageKeys.Settings, "Settings", Symbol.Setting, isFooter: true);

        // Ensure examiner base pages remain permitted for authenticated users.
        foreach (var key in ExaminerPageKeys)
        {
            _allowedPageKeys.Add(key);
        }
    }

    private void AddNavItem(string key, string label, Symbol icon, bool isFooter = false)
    {
        NavigationItems.Add(new NavItemDescriptor(key, label, icon, isFooter));
        _allowedPageKeys.Add(key);
    }

    private static HashSet<HiveRole> ParseRoles(IReadOnlyList<string> rawRoles)
    {
        var set = new HashSet<HiveRole>();
        foreach (var raw in rawRoles)
        {
            if (Enum.TryParse<HiveRole>(raw, ignoreCase: true, out var role))
            {
                set.Add(role);
            }
        }
        return set;
    }
}
