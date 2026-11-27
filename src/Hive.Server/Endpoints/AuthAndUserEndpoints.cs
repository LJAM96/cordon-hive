using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Hive.Contracts;
using Hive.Domain.Audit;
using Hive.Domain.Enrollment;
using Hive.Domain.Identity;
using Hive.Infrastructure.Repositories;
using Hive.Server.Authentication;
using Hive.Server.Middleware;

namespace Hive.Server.Endpoints;

public static class AuthAndUserEndpoints
{
    private const int MaxFailedLoginAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static RouteGroupBuilder MapAuthAndUserEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/auth/login", Login)
            .AllowAnonymous();

        api.MapPost("/auth/refresh", Refresh)
            .AllowAnonymous();

        api.MapPost("/auth/logout", Logout)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/auth/change-password", ChangePassword)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapGet("/users", ListUsers)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPost("/users", CreateUser)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        api.MapPatch("/users/{id:guid}", UpdateUser)
            .RequireAuthorization(HiveAuthPolicies.UserOnly);

        return api;
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        HttpContext context,
        IUserAccountRepository userRepository,
        IAuditRepository auditRepository,
        JwtTokenService jwtTokenService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return HiveApiEndpoints.ValidationProblem("Username and Password are required.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var correlationId = CorrelationIdMiddleware.GetCorrelationId(context);
        var normalizedUsername = UserAccount.NormalizeUsername(request.Username);
        var user = await userRepository.GetByUsernameAsync(request.Username, cancellationToken);

        if (user is null)
        {
            await auditRepository.AppendAsync(
                AuditEvent.Create(
                    "User",
                    normalizedUsername,
                    "UserLoginFailed",
                    "UserAccount",
                    "unknown",
                    "Failure",
                    null,
                    correlationId,
                    nowUtc,
                    JsonSerializer.Serialize(new { reason = "UserNotFound" })),
                cancellationToken);

            return HiveApiEndpoints.Problem(StatusCodes.Status401Unauthorized, "Invalid credentials", "Invalid username or password.");
        }

        if (!user.IsActive)
        {
            await auditRepository.AppendAsync(
                AuditEvent.Create(
                    "User",
                    user.SubjectId,
                    "UserLoginFailed",
                    "UserAccount",
                    user.UserId.ToString("D"),
                    "Failure",
                    null,
                    correlationId,
                    nowUtc,
                    JsonSerializer.Serialize(new { reason = "AccountDisabled" })),
                cancellationToken);

            return HiveApiEndpoints.Problem(StatusCodes.Status401Unauthorized, "Account disabled", "User account is disabled.");
        }

        if (user.IsLockedAt(nowUtc))
        {
            await auditRepository.AppendAsync(
                AuditEvent.Create(
                    "User",
                    user.SubjectId,
                    "UserLoginFailed",
                    "UserAccount",
                    user.UserId.ToString("D"),
                    "Locked",
                    null,
                    correlationId,
                    nowUtc,
                    JsonSerializer.Serialize(new { reason = "AccountLocked", lockedUntilUtc = user.LockedUntilUtc })),
                cancellationToken);

            return HiveApiEndpoints.Problem(
                StatusCodes.Status423Locked,
                "Account locked",
                "Account is temporarily locked due to repeated failed login attempts.");
        }

        if (!PasswordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            var updatedAfterFailure = await userRepository.RecordLoginFailureAsync(
                user.UserId,
                MaxFailedLoginAttempts,
                LockoutDuration,
                nowUtc,
                cancellationToken);

            var isLockedNow = updatedAfterFailure is not null && updatedAfterFailure.IsLockedAt(nowUtc);

            await auditRepository.AppendAsync(
                AuditEvent.Create(
                    "User",
                    user.SubjectId,
                    "UserLoginFailed",
                    "UserAccount",
                    user.UserId.ToString("D"),
                    isLockedNow ? "Locked" : "Failure",
                    null,
                    correlationId,
                    nowUtc,
                    JsonSerializer.Serialize(new
                    {
                        failedLoginCount = updatedAfterFailure?.FailedLoginCount,
                        lockedUntilUtc = updatedAfterFailure?.LockedUntilUtc
                    })),
                cancellationToken);

            if (isLockedNow)
            {
                return HiveApiEndpoints.Problem(
                    StatusCodes.Status423Locked,
                    "Account locked",
                    "Account is temporarily locked due to repeated failed login attempts.");
            }

            return HiveApiEndpoints.Problem(StatusCodes.Status401Unauthorized, "Invalid credentials", "Invalid username or password.");
        }

        await userRepository.RecordLoginSuccessAsync(user.UserId, nowUtc, cancellationToken);

        var response = await IssueSessionTokensAsync(
            user,
            request.ClientLabel,
            nowUtc,
            userRepository,
            jwtTokenService,
            cancellationToken);

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                user.SubjectId,
                "UserLoginSucceeded",
                "UserAccount",
                user.UserId.ToString("D"),
                "Success",
                null,
                correlationId,
                nowUtc,
                string.IsNullOrWhiteSpace(request.ClientLabel)
                    ? null
                    : JsonSerializer.Serialize(new { clientLabel = request.ClientLabel.Trim() })),
            cancellationToken);

        return Results.Ok(response);
    }

    private static async Task<IResult> Refresh(
        RefreshTokenRequest request,
        IUserAccountRepository userRepository,
        JwtTokenService jwtTokenService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return HiveApiEndpoints.Problem(StatusCodes.Status401Unauthorized, "Invalid refresh token", "Refresh token is required.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var tokenHash = EnrollmentTokenHasher.ComputeSha256Hex(request.RefreshToken);
        var existingToken = await userRepository.GetRefreshTokenByHashAsync(tokenHash, cancellationToken);

        if (existingToken is null || !existingToken.IsActiveAt(nowUtc))
        {
            return HiveApiEndpoints.Problem(StatusCodes.Status401Unauthorized, "Invalid refresh token", "Refresh token is invalid, expired, or revoked.");
        }

        var user = await userRepository.GetByIdAsync(existingToken.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return HiveApiEndpoints.Problem(StatusCodes.Status401Unauthorized, "Account disabled", "User account is missing or disabled.");
        }

        if (!await userRepository.RevokeRefreshTokenAsync(tokenHash, nowUtc, cancellationToken))
        {
            return HiveApiEndpoints.Problem(StatusCodes.Status401Unauthorized, "Refresh token replay", "Refresh token has already been used.");
        }

        var response = await IssueSessionTokensAsync(
            user,
            existingToken.ClientLabel,
            nowUtc,
            userRepository,
            jwtTokenService,
            cancellationToken);

        return Results.Ok(response);
    }

    private static async Task<IResult> Logout(
        LogoutRequest? request,
        HttpContext context,
        IUserAccountRepository userRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!TryGetCallerUserId(context.User, out var userId))
        {
            return HiveApiEndpoints.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", "Caller user identity is invalid.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var actor = context.User.ToActorIdentity();

        if (!string.IsNullOrWhiteSpace(request?.RefreshToken))
        {
            var tokenHash = EnrollmentTokenHasher.ComputeSha256Hex(request.RefreshToken);
            var existingToken = await userRepository.GetRefreshTokenByHashAsync(tokenHash, cancellationToken);
            if (existingToken is not null && existingToken.UserId == userId)
            {
                await userRepository.RevokeRefreshTokenAsync(tokenHash, nowUtc, cancellationToken);
            }
        }
        else
        {
            await userRepository.RevokeAllUserRefreshTokensAsync(userId, nowUtc, cancellationToken);
        }

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "UserLoggedOut",
                "UserAccount",
                userId.ToString("D"),
                "Success",
                null,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc),
            cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> ChangePassword(
        ChangePasswordRequest request,
        HttpContext context,
        IUserAccountRepository userRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!TryGetCallerUserId(context.User, out var userId))
        {
            return HiveApiEndpoints.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", "Caller user identity is invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.CurrentPassword))
        {
            return HiveApiEndpoints.ValidationProblem("CurrentPassword is required.");
        }

        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return HiveApiEndpoints.Problem(StatusCodes.Status401Unauthorized, "Unauthorized", "User account is missing or disabled.");
        }

        if (!PasswordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash))
        {
            return HiveApiEndpoints.Problem(StatusCodes.Status401Unauthorized, "Invalid password", "Current password is incorrect.");
        }

        var (isValid, error) = PasswordPolicy.Validate(request.NewPassword);
        if (!isValid)
        {
            return HiveApiEndpoints.ValidationProblem(error ?? "NewPassword does not meet password policy requirements.");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var newHash = PasswordHasher.HashPassword(request.NewPassword);

        await userRepository.UpdateAsync(
            userId,
            displayName: null,
            roles: null,
            allowedSiteIds: null,
            allSitesAccess: null,
            isActive: null,
            newPasswordHash: newHash,
            mustChangePassword: false,
            updatedAtUtc: nowUtc,
            ct: cancellationToken);

        await userRepository.RevokeAllUserRefreshTokensAsync(userId, nowUtc, cancellationToken);

        var actor = context.User.ToActorIdentity();
        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "UserPasswordChanged",
                "UserAccount",
                userId.ToString("D"),
                "Success",
                null,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc),
            cancellationToken);

        return Results.NoContent();
    }

    private static async Task<IResult> ListUsers(
        HttpContext context,
        IUserAccountRepository userRepository,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanManageUsers(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to manage user accounts.");
        }

        var users = await userRepository.ListAsync(cancellationToken);
        return Results.Ok(users.Select(ToUserAccountDto).ToArray());
    }

    private static async Task<IResult> CreateUser(
        CreateUserRequest request,
        HttpContext context,
        IUserAccountRepository userRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanManageUsers(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to create user accounts.");
        }

        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return HiveApiEndpoints.ValidationProblem("Username and DisplayName are required.");
        }

        var (isValidPassword, passwordError) = PasswordPolicy.Validate(request.InitialPassword);
        if (!isValidPassword)
        {
            return HiveApiEndpoints.ValidationProblem(passwordError ?? "InitialPassword does not meet password policy requirements.");
        }

        if (!TryParseRoles(request.Roles, out var roles, out var roleError))
        {
            return HiveApiEndpoints.ValidationProblem(roleError!);
        }

        var allowedSites = (request.AllowedSiteIds ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var nowUtc = timeProvider.GetUtcNow();
        var newUser = new UserAccount(
            Guid.NewGuid(),
            request.Username.Trim(),
            UserAccount.NormalizeUsername(request.Username),
            request.DisplayName.Trim(),
            PasswordHasher.HashPassword(request.InitialPassword),
            roles,
            allowedSites,
            request.AllSitesAccess,
            IsActive: true,
            MustChangePassword: true,
            FailedLoginCount: 0,
            LockedUntilUtc: null,
            CreatedAtUtc: nowUtc,
            UpdatedAtUtc: nowUtc);

        if (!await userRepository.CreateAsync(newUser, cancellationToken))
        {
            return HiveApiEndpoints.Problem(
                StatusCodes.Status409Conflict,
                "Duplicate username",
                $"A user account with username '{newUser.Username}' already exists.");
        }

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "UserAccountCreated",
                "UserAccount",
                newUser.UserId.ToString("D"),
                "Success",
                null,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    username = newUser.Username,
                    roles = newUser.Roles.Select(r => r.ToString()).OrderBy(r => r, StringComparer.Ordinal).ToArray(),
                    allSitesAccess = newUser.AllSitesAccess
                })),
            cancellationToken);

        return Results.Created(
            $"{ProtocolConstants.ApiPrefix}/users/{newUser.UserId:D}",
            ToUserAccountDto(newUser));
    }

    private static async Task<IResult> UpdateUser(
        Guid id,
        UpdateUserRequest request,
        HttpContext context,
        IUserAccountRepository userRepository,
        IAuditRepository auditRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = context.User.ToActorIdentity();
        if (!ScopeAuthorizationService.CanManageUsers(actor))
        {
            return HiveApiEndpoints.ForbiddenProblem("Caller is not authorized to update user accounts.");
        }

        if (request.DisplayName is not null && string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return HiveApiEndpoints.ValidationProblem("DisplayName cannot be empty.");
        }

        HashSet<HiveRole>? parsedRoles = null;
        if (request.Roles is not null)
        {
            if (!TryParseRoles(request.Roles, out parsedRoles, out var roleError))
            {
                return HiveApiEndpoints.ValidationProblem(roleError!);
            }
        }

        HashSet<string>? parsedSites = request.AllowedSiteIds?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var nowUtc = timeProvider.GetUtcNow();
        var updated = await userRepository.UpdateAsync(
            id,
            request.DisplayName?.Trim(),
            parsedRoles,
            parsedSites,
            request.AllSitesAccess,
            request.IsActive,
            newPasswordHash: null,
            mustChangePassword: null,
            updatedAtUtc: nowUtc,
            ct: cancellationToken);

        if (updated is null)
        {
            return HiveApiEndpoints.Problem(StatusCodes.Status404NotFound, "User not found", $"User '{id}' was not found.");
        }

        if (request.IsActive == false)
        {
            await userRepository.RevokeAllUserRefreshTokensAsync(id, nowUtc, cancellationToken);
        }

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "User",
                actor.UserSid,
                "UserAccountUpdated",
                "UserAccount",
                updated.UserId.ToString("D"),
                "Success",
                null,
                CorrelationIdMiddleware.GetCorrelationId(context),
                nowUtc,
                JsonSerializer.Serialize(new
                {
                    isActive = updated.IsActive,
                    roles = updated.Roles.Select(r => r.ToString()).OrderBy(r => r, StringComparer.Ordinal).ToArray(),
                    allSitesAccess = updated.AllSitesAccess
                })),
            cancellationToken);

        return Results.Ok(ToUserAccountDto(updated));
    }

    private static async Task<LoginResponse> IssueSessionTokensAsync(
        UserAccount user,
        string? clientLabel,
        DateTimeOffset nowUtc,
        IUserAccountRepository userRepository,
        JwtTokenService jwtTokenService,
        CancellationToken cancellationToken)
    {
        var issuedJwt = jwtTokenService.CreateAccessToken(user);

        var rawRefreshToken = EnrollmentTokenHasher.GenerateRawToken();
        var refreshHash = EnrollmentTokenHasher.ComputeSha256Hex(rawRefreshToken);
        var refreshDays = Math.Clamp(jwtTokenService.CurrentOptions.RefreshTokenDays, 1, 90);
        var refreshExpiresAtUtc = nowUtc.AddDays(refreshDays);

        var refreshRecord = new UserRefreshToken(
            Guid.NewGuid(),
            user.UserId,
            refreshHash,
            string.IsNullOrWhiteSpace(clientLabel) ? null : clientLabel.Trim(),
            nowUtc,
            refreshExpiresAtUtc,
            null);

        await userRepository.CreateRefreshTokenAsync(refreshRecord, cancellationToken);

        var me = new MeResponse(
            user.SubjectId,
            user.DisplayName,
            user.Roles.Select(r => r.ToString()).OrderBy(r => r, StringComparer.Ordinal).ToArray(),
            user.AllowedSiteIds.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(),
            user.AllSitesAccess);

        return new LoginResponse(
            issuedJwt.Token,
            issuedJwt.ExpiresAtUtc,
            rawRefreshToken,
            refreshExpiresAtUtc,
            user.MustChangePassword,
            me);
    }

    private static bool TryParseRoles(
        IReadOnlyList<string>? rawRoles,
        out HashSet<HiveRole> roles,
        out string? errorMessage)
    {
        roles = new HashSet<HiveRole>();
        errorMessage = null;

        if (rawRoles is null)
        {
            return true;
        }

        foreach (var rawRole in rawRoles)
        {
            if (string.IsNullOrWhiteSpace(rawRole) || !Enum.TryParse<HiveRole>(rawRole.Trim(), ignoreCase: true, out var parsed))
            {
                errorMessage = $"Unknown role '{rawRole}'.";
                return false;
            }

            roles.Add(parsed);
        }

        return true;
    }

    private static bool TryGetCallerUserId(ClaimsPrincipal principal, out Guid userId)
    {
        var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(sub, out userId))
        {
            return true;
        }

        var userSid = principal.FindFirstValue(HiveClaimTypes.UserSid);
        if (!string.IsNullOrWhiteSpace(userSid) &&
            userSid.StartsWith("hive-user:", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(userSid["hive-user:".Length..], out userId))
        {
            return true;
        }

        userId = Guid.Empty;
        return false;
    }

    private static UserAccountDto ToUserAccountDto(UserAccount user) =>
        new(
            user.UserId,
            user.Username,
            user.DisplayName,
            user.Roles.Select(r => r.ToString()).OrderBy(r => r, StringComparer.Ordinal).ToArray(),
            user.AllowedSiteIds.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(),
            user.AllSitesAccess,
            user.IsActive,
            user.MustChangePassword,
            user.CreatedAtUtc);
}
