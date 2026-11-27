using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Contracts;

namespace Hive.App.Services;

public enum ApiErrorKind
{
    None,
    Unauthorized,
    Forbidden,
    Offline,
    Conflict,
    BadRequest,
    ServerError,
    InvalidResponse
}

public sealed record ApiResult<T>(
    bool IsSuccess,
    T? Value,
    ApiErrorKind ErrorKind,
    HttpStatusCode? StatusCode,
    string? ErrorMessage)
{
    public static ApiResult<T> Success(T value, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(true, value, ApiErrorKind.None, statusCode, null);

    public static ApiResult<T> Failure(
        ApiErrorKind errorKind,
        string errorMessage,
        HttpStatusCode? statusCode = null) =>
        new(false, default, errorKind, statusCode, errorMessage);
}

public sealed class HiveApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AppSettings _settings;
    private readonly DpapiTokenStore _tokenStore;
    private readonly HttpClient _httpClient;
    private readonly object _tokenLock = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _refreshExpiresAtUtc;

    public bool HasStoredSession
    {
        get
        {
            lock (_tokenLock)
            {
                return (!string.IsNullOrWhiteSpace(_accessToken) || !string.IsNullOrWhiteSpace(_refreshToken)) &&
                       _refreshExpiresAtUtc > DateTimeOffset.UtcNow;
            }
        }
    }

    public HiveApiClient(AppSettings settings, DpapiTokenStore? tokenStore = null)
    {
        _settings = settings;
        _tokenStore = tokenStore ?? new DpapiTokenStore();

        var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true,
            PreAuthenticate = true
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(_settings.RequestTimeoutSeconds, 3, 120))
        };

        RestoreSessionFromStore();
    }

    public Task<bool> RestoreSessionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(RestoreSessionFromStore());
    }

    public async Task<ApiResult<LoginResponse>> LoginAsync(
        string username,
        string password,
        CancellationToken ct = default)
    {
        var requestPayload = new LoginRequest(username, password, Environment.MachineName);
        var result = await SendJsonAsync<LoginRequest, LoginResponse>(
            HttpMethod.Post,
            $"{ProtocolConstants.ApiPrefix}/auth/login",
            requestPayload,
            attachBearerToken: false,
            allowRefreshRetry: false,
            includeIdempotencyHeader: false,
            ct).ConfigureAwait(false);

        if (result.IsSuccess && result.Value is not null)
        {
            StoreSessionTokens(
                result.Value.AccessToken,
                result.Value.RefreshToken,
                result.Value.RefreshTokenExpiresAtUtc);
        }

        return result;
    }

    public Task<ApiResult<LoginResponse>> RefreshAsync(CancellationToken ct = default) =>
        RefreshCoreAsync(expectedAccessToken: null, ct);

    public async Task<ApiResult<bool>> LogoutAsync(CancellationToken ct = default)
    {
        var (_, refreshToken) = GetTokensSnapshot();
        try
        {
            var result = await SendNoContentAsync(
                HttpMethod.Post,
                $"{ProtocolConstants.ApiPrefix}/auth/logout",
                new LogoutRequest(refreshToken),
                attachBearerToken: true,
                allowRefreshRetry: false,
                ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            ClearSession();
        }
    }

    public Task<ApiResult<bool>> ChangePasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken ct = default) =>
        SendNoContentAsync(
            HttpMethod.Post,
            $"{ProtocolConstants.ApiPrefix}/auth/change-password",
            new ChangePasswordRequest(currentPassword, newPassword),
            attachBearerToken: true,
            allowRefreshRetry: false,
            ct);

    public Task<ApiResult<MeResponse>> GetMeAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync<MeResponse>($"{ProtocolConstants.ApiPrefix}/me", cancellationToken);

    public Task<ApiResult<IReadOnlyList<DeviceSummaryDto>>> GetWorkstationsAsync(
        CancellationToken cancellationToken = default) =>
        GetListAsync<DeviceSummaryDto>(
            $"{ProtocolConstants.ApiPrefix}/workstations",
            "workstation",
            cancellationToken);

    public Task<ApiResult<IReadOnlyList<DeviceSummaryDto>>> GetMyWorkstationsAsync(
        CancellationToken cancellationToken = default) =>
        GetListAsync<DeviceSummaryDto>(
            $"{ProtocolConstants.ApiPrefix}/me/workstations",
            "workstation",
            cancellationToken);

    public Task<ApiResult<DeviceDetailDto>> GetWorkstationDetailAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default) =>
        GetJsonAsync<DeviceDetailDto>($"{ProtocolConstants.ApiPrefix}/workstations/{deviceId:D}", cancellationToken);

    public Task<ApiResult<DeviceTelemetryResponse>> GetWorkstationTelemetryAsync(
        Guid deviceId,
        int sampleLimit = 60,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(sampleLimit, 1, 500);
        return GetJsonAsync<DeviceTelemetryResponse>(
            $"{ProtocolConstants.ApiPrefix}/workstations/{deviceId:D}/telemetry?sampleLimit={safeLimit}",
            cancellationToken);
    }

    public Task<ApiResult<IReadOnlyList<SoftwareProductDto>>> GetSoftwareProductsAsync(
        CancellationToken cancellationToken = default) =>
        GetListAsync<SoftwareProductDto>(
            $"{ProtocolConstants.ApiPrefix}/software/products",
            "software products",
            cancellationToken);

    public Task<ApiResult<SoftwareProductDto>> CreateSoftwareProductAsync(
        CreateSoftwareProductRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<CreateSoftwareProductRequest, SoftwareProductDto>(
            HttpMethod.Post,
            $"{ProtocolConstants.ApiPrefix}/software/products",
            request,
            attachBearerToken: true,
            allowRefreshRetry: true,
            includeIdempotencyHeader: true,
            cancellationToken);

    public Task<ApiResult<IReadOnlyList<SoftwareReleaseDto>>> GetSoftwareReleasesAsync(
        Guid? productId = null,
        CancellationToken cancellationToken = default)
    {
        var path = productId is { } pid
            ? $"{ProtocolConstants.ApiPrefix}/software/releases?productId={pid:D}"
            : $"{ProtocolConstants.ApiPrefix}/software/releases";
        return GetListAsync<SoftwareReleaseDto>(path, "software releases", cancellationToken);
    }

    public Task<ApiResult<SoftwareReleaseDto>> CreateSoftwareReleaseAsync(
        CreateSoftwareReleaseRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<CreateSoftwareReleaseRequest, SoftwareReleaseDto>(
            HttpMethod.Post,
            $"{ProtocolConstants.ApiPrefix}/software/releases",
            request,
            attachBearerToken: true,
            allowRefreshRetry: true,
            includeIdempotencyHeader: true,
            cancellationToken);

    public Task<ApiResult<SoftwareReleaseDto>> ApproveSoftwareReleaseAsync(
        Guid releaseId,
        ApproveSoftwareReleaseRequest? request = null,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<ApproveSoftwareReleaseRequest, SoftwareReleaseDto>(
            HttpMethod.Post,
            $"{ProtocolConstants.ApiPrefix}/software/releases/{releaseId:D}/approve",
            request ?? new ApproveSoftwareReleaseRequest(),
            attachBearerToken: true,
            allowRefreshRetry: true,
            includeIdempotencyHeader: true,
            cancellationToken);

    public Task<ApiResult<IReadOnlyList<SoftwareExemptionDto>>> GetSoftwareExemptionsAsync(
        Guid? deviceId = null,
        Guid? productId = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>(2);
        if (deviceId is { } did)
        {
            query.Add($"deviceId={did:D}");
        }
        if (productId is { } pid)
        {
            query.Add($"productId={pid:D}");
        }

        var path = query.Count == 0
            ? $"{ProtocolConstants.ApiPrefix}/software/exemptions"
            : $"{ProtocolConstants.ApiPrefix}/software/exemptions?{string.Join("&", query)}";

        return GetListAsync<SoftwareExemptionDto>(path, "software exemptions", cancellationToken);
    }

    public Task<ApiResult<SoftwareExemptionDto>> CreateSoftwareExemptionAsync(
        CreateSoftwareExemptionRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<CreateSoftwareExemptionRequest, SoftwareExemptionDto>(
            HttpMethod.Post,
            $"{ProtocolConstants.ApiPrefix}/software/exemptions",
            request,
            attachBearerToken: true,
            allowRefreshRetry: true,
            includeIdempotencyHeader: true,
            cancellationToken);

    public Task<ApiResult<SoftwareExemptionDto>> RevokeSoftwareExemptionAsync(
        Guid exemptionId,
        RevokeSoftwareExemptionRequest request,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<RevokeSoftwareExemptionRequest, SoftwareExemptionDto>(
            HttpMethod.Post,
            $"{ProtocolConstants.ApiPrefix}/software/exemptions/{exemptionId:D}/revoke",
            request,
            attachBearerToken: true,
            allowRefreshRetry: true,
            includeIdempotencyHeader: true,
            cancellationToken);

    public Task<ApiResult<IReadOnlyList<DeviceComplianceSummaryDto>>> GetFleetComplianceAsync(
        string? siteId = null,
        CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(siteId)
            ? $"{ProtocolConstants.ApiPrefix}/software/compliance"
            : $"{ProtocolConstants.ApiPrefix}/software/compliance?siteId={Uri.EscapeDataString(siteId.Trim())}";
        return GetListAsync<DeviceComplianceSummaryDto>(path, "fleet compliance", cancellationToken);
    }

    public Task<ApiResult<DeviceComplianceSummaryDto>> GetWorkstationComplianceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default) =>
        GetJsonAsync<DeviceComplianceSummaryDto>(
            $"{ProtocolConstants.ApiPrefix}/workstations/{deviceId:D}/compliance",
            cancellationToken);

    public Task<ApiResult<NotificationSyncResponse>> SyncMyNotificationsAsync(
        string? cursor,
        CancellationToken ct = default)
    {
        var path = string.IsNullOrWhiteSpace(cursor)
            ? $"{ProtocolConstants.ApiPrefix}/me/notifications"
            : $"{ProtocolConstants.ApiPrefix}/me/notifications?cursor={Uri.EscapeDataString(cursor.Trim())}";
        return GetJsonAsync<NotificationSyncResponse>(path, ct);
    }

    public Task<ApiResult<AdminNotificationSummaryDto>> PublishNotificationAsync(
        CreateNotificationRequest request,
        CancellationToken ct = default) =>
        SendJsonAsync<CreateNotificationRequest, AdminNotificationSummaryDto>(
            HttpMethod.Post,
            $"{ProtocolConstants.ApiPrefix}/notifications",
            request,
            attachBearerToken: true,
            allowRefreshRetry: true,
            includeIdempotencyHeader: true,
            ct);

    public Task<ApiResult<IReadOnlyList<AdminNotificationSummaryDto>>> GetAdminNotificationsAsync(
        CancellationToken ct = default) =>
        GetListAsync<AdminNotificationSummaryDto>(
            $"{ProtocolConstants.ApiPrefix}/notifications",
            "admin notifications",
            ct);

    public Task<ApiResult<bool>> CancelNotificationAsync(
        Guid notificationId,
        string reason,
        CancellationToken ct = default) =>
        SendNoContentAsync(
            HttpMethod.Post,
            $"{ProtocolConstants.ApiPrefix}/notifications/{notificationId:D}/cancel",
            new CancelNotificationRequest(reason),
            attachBearerToken: true,
            allowRefreshRetry: true,
            ct,
            idempotencyKey: Guid.NewGuid().ToString("N"));

    public Task<ApiResult<RdpAuthorizationResponse>> RequestRdpAsync(
        Guid deviceId,
        RdpRequest request,
        CancellationToken ct = default) =>
        SendJsonAsync<RdpRequest, RdpAuthorizationResponse>(
            HttpMethod.Post,
            $"{ProtocolConstants.ApiPrefix}/workstations/{deviceId:D}/rdp-request",
            request,
            attachBearerToken: true,
            allowRefreshRetry: true,
            includeIdempotencyHeader: true,
            ct);

    public Task<ApiResult<EscrowCredentialStatusDto>> GetEscrowCredentialStatusAsync(
        Guid deviceId,
        CancellationToken ct = default) =>
        GetJsonAsync<EscrowCredentialStatusDto>(
            $"{ProtocolConstants.ApiPrefix}/workstations/{deviceId:D}/escrow-credential",
            ct);

    public async Task<ApiResult<EscrowCredentialStatusDto>> UpsertEscrowCredentialAsync(
        Guid deviceId,
        UpsertEscrowCredentialRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var idempotencyKey = Guid.NewGuid().ToString("N");
            using var response = await SendWithAuthRetryAsync(
                () =>
                {
                    var message = new HttpRequestMessage(
                        HttpMethod.Put,
                        BuildEndpointUri($"{ProtocolConstants.ApiPrefix}/workstations/{deviceId:D}/escrow-credential"));
                    message.Headers.Add(ProtocolConstants.CorrelationIdHeader, Guid.NewGuid().ToString("N"));
                    message.Headers.Add(ProtocolConstants.IdempotencyKeyHeader, idempotencyKey);
                    message.Content = JsonContent.Create(request, options: JsonOptions);
                    return message;
                },
                attachBearerToken: true,
                allowRefreshRetry: true,
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return MapHttpError<EscrowCredentialStatusDto>(
                    response.StatusCode,
                    await SafeReadErrorAsync(response, ct).ConfigureAwait(false));
            }

            if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            {
                return ApiResult<EscrowCredentialStatusDto>.Success(
                    new EscrowCredentialStatusDto(
                        deviceId,
                        HasEscrowedCredential: true,
                        AccountUsername: request.AccountUsername.Trim(),
                        UpdatedBySid: null,
                        UpdatedAtUtc: DateTimeOffset.UtcNow,
                        KeyVersion: 1),
                    response.StatusCode);
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return ApiResult<EscrowCredentialStatusDto>.Success(
                    new EscrowCredentialStatusDto(
                        deviceId,
                        HasEscrowedCredential: true,
                        AccountUsername: request.AccountUsername.Trim(),
                        UpdatedBySid: null,
                        UpdatedAtUtc: DateTimeOffset.UtcNow,
                        KeyVersion: 1),
                    response.StatusCode);
            }

            var status = JsonSerializer.Deserialize<EscrowCredentialStatusDto>(text, JsonOptions);
            if (status is null)
            {
                return ApiResult<EscrowCredentialStatusDto>.Failure(
                    ApiErrorKind.InvalidResponse,
                    "Empty escrow credential status response from Hive server.");
            }

            return ApiResult<EscrowCredentialStatusDto>.Success(status, response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<EscrowCredentialStatusDto>.Failure(
                ApiErrorKind.Offline,
                $"Hive server unreachable on LAN ({ex.Message}).");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiResult<EscrowCredentialStatusDto>.Failure(
                ApiErrorKind.Offline,
                "Hive server request timed out.");
        }
        catch (JsonException)
        {
            return ApiResult<EscrowCredentialStatusDto>.Failure(
                ApiErrorKind.InvalidResponse,
                "Received invalid escrow credential status JSON from Hive server.");
        }
    }

    public async Task<ApiResult<bool>> DeleteEscrowCredentialAsync(
        Guid deviceId,
        CancellationToken ct = default)
    {
        try
        {
            var idempotencyKey = Guid.NewGuid().ToString("N");
            using var response = await SendWithAuthRetryAsync(
                () =>
                {
                    var message = new HttpRequestMessage(
                        HttpMethod.Delete,
                        BuildEndpointUri($"{ProtocolConstants.ApiPrefix}/workstations/{deviceId:D}/escrow-credential"));
                    message.Headers.Add(ProtocolConstants.CorrelationIdHeader, Guid.NewGuid().ToString("N"));
                    message.Headers.Add(ProtocolConstants.IdempotencyKeyHeader, idempotencyKey);
                    return message;
                },
                attachBearerToken: true,
                allowRefreshRetry: true,
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return MapHttpError<bool>(
                    response.StatusCode,
                    await SafeReadErrorAsync(response, ct).ConfigureAwait(false));
            }

            return ApiResult<bool>.Success(true, response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<bool>.Failure(
                ApiErrorKind.Offline,
                $"Hive server unreachable on LAN ({ex.Message}).");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiResult<bool>.Failure(
                ApiErrorKind.Offline,
                "Hive server request timed out.");
        }
    }

    public Task<ApiResult<RevealEscrowCredentialResponse>> RevealEscrowCredentialAsync(
        Guid deviceId,
        RevealEscrowCredentialRequest request,
        CancellationToken ct = default) =>
        SendJsonAsync<RevealEscrowCredentialRequest, RevealEscrowCredentialResponse>(
            HttpMethod.Post,
            $"{ProtocolConstants.ApiPrefix}/workstations/{deviceId:D}/escrow-credential/reveal",
            request,
            attachBearerToken: true,
            allowRefreshRetry: true,
            includeIdempotencyHeader: true,
            ct);

    public Task<ApiResult<bool>> RecordNotificationDeliveryAsync(
        Guid notificationId,
        RecordDeliveryRequest request,
        CancellationToken ct = default) =>
        SendNoContentAsync(
            HttpMethod.Post,
            $"{ProtocolConstants.ApiPrefix}/me/notifications/{notificationId:D}/delivery",
            request,
            attachBearerToken: true,
            allowRefreshRetry: true,
            ct);

    public Task<ApiResult<UserNotificationDto?>> MarkNotificationReadAsync(
        Guid notificationId,
        long expectedVersion,
        string idempotencyKey,
        CancellationToken ct = default) =>
        SendNotificationActionAsync(
            $"{ProtocolConstants.ApiPrefix}/me/notifications/{notificationId:D}/read",
            expectedVersion,
            idempotencyKey,
            ct);

    public Task<ApiResult<UserNotificationDto?>> AcknowledgeNotificationAsync(
        Guid notificationId,
        long expectedVersion,
        string idempotencyKey,
        CancellationToken ct = default) =>
        SendNotificationActionAsync(
            $"{ProtocolConstants.ApiPrefix}/me/notifications/{notificationId:D}/ack",
            expectedVersion,
            idempotencyKey,
            ct);

    public Task<ApiResult<UserNotificationDto?>> CompleteNotificationAsync(
        Guid notificationId,
        long expectedVersion,
        string idempotencyKey,
        CancellationToken ct = default) =>
        SendNotificationActionAsync(
            $"{ProtocolConstants.ApiPrefix}/me/notifications/{notificationId:D}/complete",
            expectedVersion,
            idempotencyKey,
            ct);

    public async Task<ApiResult<DeviceSummaryDto?>> UpdateWorkstationAssignmentAsync(
        Guid deviceId,
        UpdateAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var idempotencyKey = Guid.NewGuid().ToString("N");
            using var response = await SendWithAuthRetryAsync(
                () =>
                {
                    var message = new HttpRequestMessage(
                        HttpMethod.Patch,
                        BuildEndpointUri($"{ProtocolConstants.ApiPrefix}/workstations/{deviceId:D}/assignment"));
                    message.Headers.Add(ProtocolConstants.CorrelationIdHeader, Guid.NewGuid().ToString("N"));
                    message.Headers.Add(ProtocolConstants.IdempotencyKeyHeader, idempotencyKey);
                    message.Content = JsonContent.Create(request, options: JsonOptions);
                    return message;
                },
                attachBearerToken: true,
                allowRefreshRetry: true,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return MapHttpError<DeviceSummaryDto?>(
                    response.StatusCode,
                    await SafeReadErrorAsync(response, cancellationToken).ConfigureAwait(false));
            }

            if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            {
                return ApiResult<DeviceSummaryDto?>.Success(null, response.StatusCode);
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return ApiResult<DeviceSummaryDto?>.Success(null, response.StatusCode);
            }

            var updated = JsonSerializer.Deserialize<DeviceSummaryDto>(text, JsonOptions);
            return ApiResult<DeviceSummaryDto?>.Success(updated, response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<DeviceSummaryDto?>.Failure(ApiErrorKind.Offline, $"Unable to reach Hive server ({ex.Message}).");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult<DeviceSummaryDto?>.Failure(ApiErrorKind.Offline, "Request to Hive server timed out.");
        }
        catch (JsonException)
        {
            return ApiResult<DeviceSummaryDto?>.Failure(ApiErrorKind.InvalidResponse, "Received an invalid response payload from Hive server.");
        }
    }

    private async Task<ApiResult<IReadOnlyList<T>>> GetListAsync<T>(
        string relativePath,
        string resourceLabel,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendWithAuthRetryAsync(
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Get, BuildEndpointUri(relativePath));
                    message.Headers.Add(ProtocolConstants.CorrelationIdHeader, Guid.NewGuid().ToString("N"));
                    return message;
                },
                attachBearerToken: true,
                allowRefreshRetry: true,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return MapHttpError<IReadOnlyList<T>>(
                    response.StatusCode,
                    await SafeReadErrorAsync(response, cancellationToken).ConfigureAwait(false));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            IReadOnlyList<T> items;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                items = doc.RootElement.Deserialize<List<T>>(JsonOptions) ?? [];
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                     doc.RootElement.TryGetProperty("items", out var itemsElement))
            {
                items = itemsElement.Deserialize<List<T>>(JsonOptions) ?? [];
            }
            else
            {
                return ApiResult<IReadOnlyList<T>>.Failure(
                    ApiErrorKind.InvalidResponse,
                    $"Unexpected {resourceLabel} list format from Hive server.");
            }

            return ApiResult<IReadOnlyList<T>>.Success(items, response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<IReadOnlyList<T>>.Failure(
                ApiErrorKind.Offline,
                $"Hive server unreachable on LAN ({ex.Message}).");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult<IReadOnlyList<T>>.Failure(
                ApiErrorKind.Offline,
                "Hive server request timed out.");
        }
        catch (JsonException)
        {
            return ApiResult<IReadOnlyList<T>>.Failure(
                ApiErrorKind.InvalidResponse,
                $"Received invalid {resourceLabel} JSON from Hive server.");
        }
    }

    private async Task<ApiResult<T>> GetJsonAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendWithAuthRetryAsync(
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Get, BuildEndpointUri(relativePath));
                    message.Headers.Add(ProtocolConstants.CorrelationIdHeader, Guid.NewGuid().ToString("N"));
                    return message;
                },
                attachBearerToken: true,
                allowRefreshRetry: true,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return MapHttpError<T>(
                    response.StatusCode,
                    await SafeReadErrorAsync(response, cancellationToken).ConfigureAwait(false));
            }

            var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                return ApiResult<T>.Failure(ApiErrorKind.InvalidResponse, "Empty JSON response from Hive server.");
            }

            return ApiResult<T>.Success(value, response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<T>.Failure(ApiErrorKind.Offline, $"Hive server unreachable on LAN ({ex.Message}).");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult<T>.Failure(ApiErrorKind.Offline, "Hive server request timed out.");
        }
        catch (JsonException)
        {
            return ApiResult<T>.Failure(ApiErrorKind.InvalidResponse, "Received invalid JSON response from Hive server.");
        }
    }

    private async Task<ApiResult<TResponse>> SendJsonAsync<TRequest, TResponse>(
        HttpMethod method,
        string relativePath,
        TRequest payload,
        bool attachBearerToken,
        bool allowRefreshRetry,
        bool includeIdempotencyHeader,
        CancellationToken cancellationToken)
    {
        try
        {
            var idempotencyKey = includeIdempotencyHeader ? Guid.NewGuid().ToString("N") : null;
            using var response = await SendWithAuthRetryAsync(
                () =>
                {
                    var message = new HttpRequestMessage(method, BuildEndpointUri(relativePath));
                    message.Headers.Add(ProtocolConstants.CorrelationIdHeader, Guid.NewGuid().ToString("N"));
                    if (idempotencyKey is not null)
                    {
                        message.Headers.Add(ProtocolConstants.IdempotencyKeyHeader, idempotencyKey);
                    }
                    message.Content = JsonContent.Create(payload, options: JsonOptions);
                    return message;
                },
                attachBearerToken,
                allowRefreshRetry,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return MapHttpError<TResponse>(
                    response.StatusCode,
                    await SafeReadErrorAsync(response, cancellationToken).ConfigureAwait(false));
            }

            var value = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                return ApiResult<TResponse>.Failure(ApiErrorKind.InvalidResponse, "Empty JSON response from Hive server.");
            }

            return ApiResult<TResponse>.Success(value, response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<TResponse>.Failure(ApiErrorKind.Offline, $"Hive server unreachable on LAN ({ex.Message}).");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult<TResponse>.Failure(ApiErrorKind.Offline, "Hive server request timed out.");
        }
        catch (JsonException)
        {
            return ApiResult<TResponse>.Failure(ApiErrorKind.InvalidResponse, "Received invalid JSON response from Hive server.");
        }
    }

    private async Task<ApiResult<UserNotificationDto?>> SendNotificationActionAsync(
        string relativePath,
        long expectedVersion,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var effectiveKey = string.IsNullOrWhiteSpace(idempotencyKey)
                ? Guid.NewGuid().ToString("N")
                : idempotencyKey.Trim();
            var payload = new NotificationActionRequest(expectedVersion);

            using var response = await SendWithAuthRetryAsync(
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, BuildEndpointUri(relativePath));
                    message.Headers.Add(ProtocolConstants.CorrelationIdHeader, Guid.NewGuid().ToString("N"));
                    message.Headers.Add(ProtocolConstants.IdempotencyKeyHeader, effectiveKey);
                    message.Content = JsonContent.Create(payload, options: JsonOptions);
                    return message;
                },
                attachBearerToken: true,
                allowRefreshRetry: true,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return MapHttpError<UserNotificationDto?>(
                    response.StatusCode,
                    await SafeReadErrorAsync(response, cancellationToken).ConfigureAwait(false));
            }

            if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            {
                return ApiResult<UserNotificationDto?>.Success(null, response.StatusCode);
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return ApiResult<UserNotificationDto?>.Success(null, response.StatusCode);
            }

            var updated = JsonSerializer.Deserialize<UserNotificationDto>(text, JsonOptions);
            return ApiResult<UserNotificationDto?>.Success(updated, response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<UserNotificationDto?>.Failure(ApiErrorKind.Offline, $"Hive server unreachable on LAN ({ex.Message}).");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult<UserNotificationDto?>.Failure(ApiErrorKind.Offline, "Hive server request timed out.");
        }
        catch (JsonException)
        {
            return ApiResult<UserNotificationDto?>.Failure(ApiErrorKind.InvalidResponse, "Received invalid notification response from Hive server.");
        }
    }

    private async Task<ApiResult<bool>> SendNoContentAsync<TRequest>(
        HttpMethod method,
        string relativePath,
        TRequest payload,
        bool attachBearerToken,
        bool allowRefreshRetry,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        try
        {
            using var response = await SendWithAuthRetryAsync(
                () =>
                {
                    var message = new HttpRequestMessage(method, BuildEndpointUri(relativePath));
                    message.Headers.Add(ProtocolConstants.CorrelationIdHeader, Guid.NewGuid().ToString("N"));
                    if (!string.IsNullOrWhiteSpace(idempotencyKey))
                    {
                        message.Headers.Add(ProtocolConstants.IdempotencyKeyHeader, idempotencyKey);
                    }
                    message.Content = JsonContent.Create(payload, options: JsonOptions);
                    return message;
                },
                attachBearerToken,
                allowRefreshRetry,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return MapHttpError<bool>(
                    response.StatusCode,
                    await SafeReadErrorAsync(response, cancellationToken).ConfigureAwait(false));
            }

            return ApiResult<bool>.Success(true, response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<bool>.Failure(ApiErrorKind.Offline, $"Hive server unreachable on LAN ({ex.Message}).");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult<bool>.Failure(ApiErrorKind.Offline, "Hive server request timed out.");
        }
    }

    private async Task<HttpResponseMessage> SendWithAuthRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        bool attachBearerToken,
        bool allowRefreshRetry,
        CancellationToken cancellationToken)
    {
        var (tokenUsed, refreshToken) = GetTokensSnapshot();
        using var initialMessage = requestFactory();
        if (attachBearerToken && !string.IsNullOrWhiteSpace(tokenUsed))
        {
            initialMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenUsed);
        }

        var response = await _httpClient.SendAsync(initialMessage, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized ||
            !allowRefreshRetry ||
            string.IsNullOrWhiteSpace(refreshToken))
        {
            return response;
        }

        var refreshResult = await RefreshCoreAsync(tokenUsed, cancellationToken).ConfigureAwait(false);
        if (!refreshResult.IsSuccess)
        {
            return response;
        }

        response.Dispose();

        var (newAccessToken, _) = GetTokensSnapshot();
        using var retryMessage = requestFactory();
        if (attachBearerToken && !string.IsNullOrWhiteSpace(newAccessToken))
        {
            retryMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newAccessToken);
        }

        return await _httpClient.SendAsync(retryMessage, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApiResult<LoginResponse>> RefreshCoreAsync(
        string? expectedAccessToken,
        CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (currentAccessToken, currentRefreshToken) = GetTokensSnapshot();

            // Another concurrent request may have already rotated the tokens while we waited.
            if (expectedAccessToken is not null &&
                !string.IsNullOrWhiteSpace(currentAccessToken) &&
                !string.Equals(currentAccessToken, expectedAccessToken, StringComparison.Ordinal))
            {
                return ApiResult<LoginResponse>.Success(
                    new LoginResponse(
                        currentAccessToken,
                        DateTimeOffset.UtcNow,
                        currentRefreshToken ?? string.Empty,
                        _refreshExpiresAtUtc,
                        false,
                        new MeResponse(string.Empty, string.Empty, [], [], false)));
            }

            if (string.IsNullOrWhiteSpace(currentRefreshToken))
            {
                ClearSession();
                return ApiResult<LoginResponse>.Failure(
                    ApiErrorKind.Unauthorized,
                    "No refresh token is available.",
                    HttpStatusCode.Unauthorized);
            }

            var result = await SendJsonAsync<RefreshTokenRequest, LoginResponse>(
                HttpMethod.Post,
                $"{ProtocolConstants.ApiPrefix}/auth/refresh",
                new RefreshTokenRequest(currentRefreshToken),
                attachBearerToken: false,
                allowRefreshRetry: false,
                includeIdempotencyHeader: false,
                ct).ConfigureAwait(false);

            if (result.IsSuccess && result.Value is not null)
            {
                StoreSessionTokens(
                    result.Value.AccessToken,
                    result.Value.RefreshToken,
                    result.Value.RefreshTokenExpiresAtUtc);
            }
            else if (!ct.IsCancellationRequested)
            {
                ClearSession();
            }

            return result;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool RestoreSessionFromStore()
    {
        var stored = _tokenStore.Load();
        if (stored is null || stored.RefreshExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            ClearSession();
            return false;
        }

        lock (_tokenLock)
        {
            _accessToken = stored.AccessToken;
            _refreshToken = stored.RefreshToken;
            _refreshExpiresAtUtc = stored.RefreshExpiresAtUtc;
        }

        return true;
    }

    private void StoreSessionTokens(
        string accessToken,
        string refreshToken,
        DateTimeOffset refreshExpiresAtUtc)
    {
        lock (_tokenLock)
        {
            _accessToken = accessToken;
            _refreshToken = refreshToken;
            _refreshExpiresAtUtc = refreshExpiresAtUtc;
        }

        _tokenStore.Save(new StoredSessionTokens(accessToken, refreshToken, refreshExpiresAtUtc));
    }

    private void ClearSession()
    {
        lock (_tokenLock)
        {
            _accessToken = null;
            _refreshToken = null;
            _refreshExpiresAtUtc = DateTimeOffset.MinValue;
        }

        _tokenStore.Clear();
    }

    private (string? AccessToken, string? RefreshToken) GetTokensSnapshot()
    {
        lock (_tokenLock)
        {
            return (_accessToken, _refreshToken);
        }
    }

    private Uri BuildEndpointUri(string relativePath)
    {
        var baseUrl = _settings.BaseUrl.TrimEnd('/');
        var path = relativePath.StartsWith('/') ? relativePath : "/" + relativePath;
        return new Uri($"{baseUrl}{path}", UriKind.Absolute);
    }

    private static ApiResult<T> MapHttpError<T>(HttpStatusCode statusCode, string? detail) =>
        statusCode switch
        {
            HttpStatusCode.Unauthorized => ApiResult<T>.Failure(
                ApiErrorKind.Unauthorized,
                detail ?? "Authentication required or session expired (401 Unauthorized).",
                statusCode),
            HttpStatusCode.Forbidden => ApiResult<T>.Failure(
                ApiErrorKind.Forbidden,
                detail ?? "You do not have permission for this action (403 Forbidden).",
                statusCode),
            HttpStatusCode.Conflict => ApiResult<T>.Failure(
                ApiErrorKind.Conflict,
                detail ?? "Record was modified by another user (409 Conflict). Refresh and try again.",
                statusCode),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => ApiResult<T>.Failure(
                ApiErrorKind.BadRequest,
                detail ?? $"Request rejected by server ({(int)statusCode}).",
                statusCode),
            _ => ApiResult<T>.Failure(
                ApiErrorKind.ServerError,
                detail ?? $"Hive server returned HTTP {(int)statusCode}.",
                statusCode)
        };

    private static async Task<string?> SafeReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("detail", out var detail) &&
                    detail.ValueKind == JsonValueKind.String)
                {
                    return detail.GetString();
                }

                if (doc.RootElement.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }

                if (doc.RootElement.TryGetProperty("title", out var title) &&
                    title.ValueKind == JsonValueKind.String)
                {
                    return title.GetString();
                }
            }

            return body.Length <= 240 ? body : body[..240];
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _refreshLock.Dispose();
        _httpClient.Dispose();
    }
}
