using Hive.Server.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Server.IntegrationTests;

public sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 11, 27, 13, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);

    public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
}

public sealed class HiveServerFactory : WebApplicationFactory<Program>
{
    public MutableTimeProvider Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:Provider", "InMemory");
        builder.UseSetting("Authentication:EnableTestingHeaderAuth", "true");
        builder.UseSetting("EscrowVault:MasterKey", "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public HttpClient CreateUserClient(
        string userSid,
        string roles,
        string sites,
        string? displayName = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestingHeaderAuthenticationHandler.UserSidHeader, userSid);
        client.DefaultRequestHeaders.Add(TestingHeaderAuthenticationHandler.UserNameHeader, displayName ?? userSid);
        client.DefaultRequestHeaders.Add(TestingHeaderAuthenticationHandler.RolesHeader, roles);
        client.DefaultRequestHeaders.Add(TestingHeaderAuthenticationHandler.SitesHeader, sites);
        return client;
    }

    public HttpClient CreateDeviceClient(string certificateThumbprint)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestingHeaderAuthenticationHandler.DeviceThumbprintHeader, certificateThumbprint);
        return client;
    }
}
