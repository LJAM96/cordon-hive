using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Hive.Contracts;
using Hive.Infrastructure.Migrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Hive.Server.IntegrationTests;

public sealed class LocalJwtServerFactory : WebApplicationFactory<Program>
{
    public const string DefaultSigningKey = "integration-test-jwt-signing-key-at-least-32-bytes!";
    public const string BootstrapUsername = "bootstrap-admin";
    public const string BootstrapPassword = "CorrectHorseBatteryStaple!99";

    public MutableTimeProvider Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:Provider", "InMemory");
        builder.UseSetting("Authentication:EnableTestingHeaderAuth", "false");
        builder.UseSetting("Authentication:UserMode", "LocalJwt");
        builder.UseSetting("Authentication:Jwt:SigningKey", DefaultSigningKey);
        builder.UseSetting("BootstrapAdmin:Username", BootstrapUsername);
        builder.UseSetting("BootstrapAdmin:Password", BootstrapPassword);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public HttpClient CreateBearerClient(string accessToken)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }
}

public sealed class LocalAuthIntegrationTests
{
    [Fact]
    public void MigrationResources_EmbedLocalAuthMigrationAfterTelemetry()
    {
        var sqlResources = typeof(PostgreSqlMigrationRunner).Assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(sqlResources, r => r.EndsWith("0003_LocalAuthAccounts.sql", StringComparison.Ordinal));
        var telemetryIdx = Array.FindIndex(sqlResources, r => r.EndsWith("0002_TelemetryVisibility.sql", StringComparison.Ordinal));
        var authIdx = Array.FindIndex(sqlResources, r => r.EndsWith("0003_LocalAuthAccounts.sql", StringComparison.Ordinal));
        Assert.True(telemetryIdx >= 0 && authIdx > telemetryIdx);
    }

    [Fact]
    public void ProductionEnvironment_WithoutPostgreSql_ThrowsFailClosed()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("Storage:Provider", "InMemory");
                builder.UseSetting("Authentication:Jwt:SigningKey", LocalJwtServerFactory.DefaultSigningKey);
            });

        var ex = Assert.Throws<InvalidOperationException>(() => _ = factory.Services);
        Assert.Equal("Production environment requires Storage:Provider=PostgreSQL.", ex.Message);
    }

    [Fact]
    public async Task UnauthenticatedMe_InLocalJwtMode_Returns401()
    {
        await using var factory = new LocalJwtServerFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BootstrapAdmin_LoginAndCallMeAndUsers_SucceedsWithMustChangePassword()
    {
        await using var factory = new LocalJwtServerFactory();
        using var anon = factory.CreateClient();

        var loginResp = await anon.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(LocalJwtServerFactory.BootstrapUsername, LocalJwtServerFactory.BootstrapPassword, "wpf-test"));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);

        var login = await loginResp.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login);
        Assert.True(login.MustChangePassword);
        Assert.False(string.IsNullOrWhiteSpace(login.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(login.RefreshToken));
        Assert.Contains("Administrator", login.User.Roles);
        Assert.True(login.User.AllSitesAccess);

        using var adminClient = factory.CreateBearerClient(login.AccessToken);

        var meResp = await adminClient.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);
        var me = await meResp.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(me);
        Assert.Equal(login.User.UserSid, me.UserSid);
        Assert.Contains("Administrator", me.Roles);
        Assert.True(me.AllSitesAccess);

        var usersResp = await adminClient.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.OK, usersResp.StatusCode);
        var users = await usersResp.Content.ReadFromJsonAsync<List<UserAccountDto>>();
        Assert.NotNull(users);
        var adminUser = Assert.Single(users);
        Assert.Equal(LocalJwtServerFactory.BootstrapUsername, adminUser.Username);
        Assert.True(adminUser.MustChangePassword);
        Assert.True(adminUser.IsActive);
    }

    [Fact]
    public async Task FailedLogins_Return401_ThenLockAccountAfterFiveFailuresWith423()
    {
        await using var factory = new LocalJwtServerFactory();
        using var anon = factory.CreateClient();

        for (var i = 1; i <= 4; i++)
        {
            var resp = await anon.PostAsJsonAsync(
                "/api/v1/auth/login",
                new LoginRequest(LocalJwtServerFactory.BootstrapUsername, "WrongPassword!123"));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }

        // 5th failed attempt locks account and returns 423
        var fifthResp = await anon.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(LocalJwtServerFactory.BootstrapUsername, "WrongPassword!123"));
        Assert.Equal(HttpStatusCode.Locked, fifthResp.StatusCode);

        // Even the correct password is rejected with 423 while locked
        var lockedCorrectResp = await anon.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(LocalJwtServerFactory.BootstrapUsername, LocalJwtServerFactory.BootstrapPassword));
        Assert.Equal(HttpStatusCode.Locked, lockedCorrectResp.StatusCode);
    }

    [Fact]
    public async Task RefreshToken_RotatesAndRejectsReplay()
    {
        await using var factory = new LocalJwtServerFactory();
        using var anon = factory.CreateClient();

        var loginResp = await anon.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(LocalJwtServerFactory.BootstrapUsername, LocalJwtServerFactory.BootstrapPassword));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        var login = (await loginResp.Content.ReadFromJsonAsync<LoginResponse>())!;

        // Refresh rotates token
        var refreshResp = await anon.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshTokenRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, refreshResp.StatusCode);
        var refreshed = (await refreshResp.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.NotEqual(login.RefreshToken, refreshed.RefreshToken);

        // Replay of old refresh token is rejected with 401
        var replayResp = await anon.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshTokenRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replayResp.StatusCode);

        // New refresh token still works
        var secondRefresh = await anon.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshTokenRequest(refreshed.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, secondRefresh.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesRefreshToken()
    {
        await using var factory = new LocalJwtServerFactory();
        using var anon = factory.CreateClient();

        var loginResp = await anon.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(LocalJwtServerFactory.BootstrapUsername, LocalJwtServerFactory.BootstrapPassword));
        var login = (await loginResp.Content.ReadFromJsonAsync<LoginResponse>())!;

        using var authedClient = factory.CreateBearerClient(login.AccessToken);
        var logoutResp = await authedClient.PostAsJsonAsync(
            "/api/v1/auth/logout",
            new LogoutRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.NoContent, logoutResp.StatusCode);

        var refreshAfterLogout = await anon.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshTokenRequest(login.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfterLogout.StatusCode);
    }

    [Fact]
    public async Task ExaminerRoleAndDeactivation_Enforces403AndImmediateJwtRevocation()
    {
        await using var factory = new LocalJwtServerFactory();
        using var anon = factory.CreateClient();

        var adminLoginResp = await anon.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(LocalJwtServerFactory.BootstrapUsername, LocalJwtServerFactory.BootstrapPassword));
        var adminLogin = (await adminLoginResp.Content.ReadFromJsonAsync<LoginResponse>())!;
        using var adminClient = factory.CreateBearerClient(adminLogin.AccessToken);

        // Create Examiner user
        const string examinerPassword = "ExaminerInitialPass!99";
        var createResp = await adminClient.PostAsJsonAsync(
            "/api/v1/users",
            new CreateUserRequest(
                "examiner.jane",
                "Jane Examiner",
                examinerPassword,
                new[] { "Examiner" },
                new[] { "site-a" },
                AllSitesAccess: false));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var createdExaminer = (await createResp.Content.ReadFromJsonAsync<UserAccountDto>())!;
        Assert.True(createdExaminer.MustChangePassword);

        // Examiner logs in
        var examinerLoginResp = await anon.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest("examiner.jane", examinerPassword));
        Assert.Equal(HttpStatusCode.OK, examinerLoginResp.StatusCode);
        var examinerLogin = (await examinerLoginResp.Content.ReadFromJsonAsync<LoginResponse>())!;
        using var examinerClient = factory.CreateBearerClient(examinerLogin.AccessToken);

        // Examiner is forbidden on /api/v1/users and /api/v1/workstations
        var usersResp = await examinerClient.GetAsync("/api/v1/users");
        Assert.Equal(HttpStatusCode.Forbidden, usersResp.StatusCode);

        var workstationsResp = await examinerClient.GetAsync("/api/v1/workstations");
        Assert.Equal(HttpStatusCode.Forbidden, workstationsResp.StatusCode);

        // Examiner can access /api/v1/me before deactivation
        var meBefore = await examinerClient.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, meBefore.StatusCode);

        // Admin deactivates Examiner
        var deactivateResp = await adminClient.PatchAsJsonAsync(
            $"/api/v1/users/{createdExaminer.UserId}",
            new UpdateUserRequest(null, null, null, null, IsActive: false));
        Assert.Equal(HttpStatusCode.OK, deactivateResp.StatusCode);
        var deactivated = (await deactivateResp.Content.ReadFromJsonAsync<UserAccountDto>())!;
        Assert.False(deactivated.IsActive);

        // Existing JWT is immediately rejected with 401
        var meAfter = await examinerClient.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meAfter.StatusCode);

        // Refresh is also rejected with 401
        var refreshAfter = await anon.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new RefreshTokenRequest(examinerLogin.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refreshAfter.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_ValidatesLengthAndClearsMustChangePassword()
    {
        await using var factory = new LocalJwtServerFactory();
        using var anon = factory.CreateClient();

        var loginResp = await anon.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(LocalJwtServerFactory.BootstrapUsername, LocalJwtServerFactory.BootstrapPassword));
        var login = (await loginResp.Content.ReadFromJsonAsync<LoginResponse>())!;
        using var adminClient = factory.CreateBearerClient(login.AccessToken);

        // Reject < 12 character password with 400
        var shortResp = await adminClient.PostAsJsonAsync(
            "/api/v1/auth/change-password",
            new ChangePasswordRequest(LocalJwtServerFactory.BootstrapPassword, "TooShort!1"));
        Assert.Equal(HttpStatusCode.BadRequest, shortResp.StatusCode);

        // Accept valid >= 12 character password with 204
        const string newPassword = "UpdatedStrongPassword!2026";
        var changeResp = await adminClient.PostAsJsonAsync(
            "/api/v1/auth/change-password",
            new ChangePasswordRequest(LocalJwtServerFactory.BootstrapPassword, newPassword));
        Assert.Equal(HttpStatusCode.NoContent, changeResp.StatusCode);

        // Old password no longer works
        var oldLoginResp = await anon.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(LocalJwtServerFactory.BootstrapUsername, LocalJwtServerFactory.BootstrapPassword));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLoginResp.StatusCode);

        // New password works and clears MustChangePassword
        var newLoginResp = await anon.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(LocalJwtServerFactory.BootstrapUsername, newPassword));
        Assert.Equal(HttpStatusCode.OK, newLoginResp.StatusCode);
        var newLogin = (await newLoginResp.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.False(newLogin.MustChangePassword);
    }
}
