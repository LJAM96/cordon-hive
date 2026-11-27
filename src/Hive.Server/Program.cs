using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Hive.Domain.Devices;
using Hive.Infrastructure.Migrations;
using Hive.Infrastructure.Repositories;
using Hive.Server.Authentication;
using Hive.Server.Endpoints;
using Hive.Server.Hubs;
using Hive.Server.Middleware;
using Hive.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

var tlsCertPath = builder.Configuration["Tls:CertificatePath"];
var tlsKeyPath = builder.Configuration["Tls:PrivateKeyPath"];
if (!string.IsNullOrWhiteSpace(tlsCertPath) &&
    !string.IsNullOrWhiteSpace(tlsKeyPath) &&
    File.Exists(tlsCertPath) &&
    File.Exists(tlsKeyPath))
{
    builder.WebHost.ConfigureKestrel(kestrelOptions =>
    {
        kestrelOptions.ConfigureHttpsDefaults(httpsOptions =>
        {
            httpsOptions.ServerCertificate = X509Certificate2.CreateFromPemFile(tlsCertPath, tlsKeyPath);
            httpsOptions.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        });
    });
}

builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddSignalR();

builder.Services.Configure<AdRoleMappingOptions>(
    builder.Configuration.GetSection(AdRoleMappingOptions.SectionName));
builder.Services.Configure<JwtAuthOptions>(
    builder.Configuration.GetSection(JwtAuthOptions.SectionName));
builder.Services.AddSingleton<JwtTokenService>();

var storageProvider = builder.Configuration["Storage:Provider"];
var connectionString = builder.Configuration.GetConnectionString("HivePostgres")
    ?? builder.Configuration["Storage:ConnectionString"]
    ?? builder.Configuration["hive_db_connection_string"]?.Trim();

var usePostgres = string.Equals(storageProvider, "PostgreSQL", StringComparison.OrdinalIgnoreCase);

if (builder.Environment.IsProduction() && !usePostgres)
{
    throw new InvalidOperationException("Production environment requires Storage:Provider=PostgreSQL.");
}

if (usePostgres)
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "Storage:Provider is set to 'PostgreSQL', but no connection string was provided in ConnectionStrings:HivePostgres or Storage:ConnectionString.");
    }

    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
    builder.Services.AddSingleton<PostgreSqlMigrationRunner>();
    builder.Services.AddSingleton<IDeviceRepository, PostgresDeviceRepository>();
    builder.Services.AddSingleton<ITelemetryRepository, PostgresTelemetryRepository>();
    builder.Services.AddSingleton<IEnrollmentTokenRepository, PostgresEnrollmentTokenRepository>();
    builder.Services.AddSingleton<IAuditRepository, PostgresAuditRepository>();
    builder.Services.AddSingleton<IOutboxRepository, PostgresOutboxRepository>();
    builder.Services.AddSingleton<IUserAccountRepository, PostgresUserAccountRepository>();
    builder.Services.AddSingleton<ISoftwareRepository, PostgresSoftwareRepository>();
    builder.Services.AddSingleton<INotificationRepository, PostgresNotificationRepository>();
    builder.Services.AddSingleton<IEscrowCredentialRepository, PostgresEscrowCredentialRepository>();
}
else
{
    builder.Services.AddSingleton<InMemoryHiveStore>();
    builder.Services.AddSingleton<IDeviceRepository, InMemoryDeviceRepository>();
    builder.Services.AddSingleton<ITelemetryRepository, InMemoryTelemetryRepository>();
    builder.Services.AddSingleton<IEnrollmentTokenRepository, InMemoryEnrollmentTokenRepository>();
    builder.Services.AddSingleton<IAuditRepository, InMemoryAuditRepository>();
    builder.Services.AddSingleton<IOutboxRepository, InMemoryOutboxRepository>();
    builder.Services.AddSingleton<IUserAccountRepository, InMemoryUserAccountRepository>();
    builder.Services.AddSingleton<ISoftwareRepository, InMemorySoftwareRepository>();
    builder.Services.AddSingleton<INotificationRepository, InMemoryNotificationRepository>();
    builder.Services.AddSingleton<IEscrowCredentialRepository, InMemoryEscrowCredentialRepository>();
}

builder.Services.AddSingleton<EscrowVaultService>();

var useTestingHeaderAuth = builder.Environment.IsEnvironment("Testing") &&
                           builder.Configuration.GetValue<bool>("Authentication:EnableTestingHeaderAuth");

var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = HiveAuthSchemes.User;
    options.DefaultChallengeScheme = HiveAuthSchemes.User;
    options.DefaultForbidScheme = HiveAuthSchemes.User;
});

if (useTestingHeaderAuth)
{
    authBuilder.AddScheme<AuthenticationSchemeOptions, TestingHeaderAuthenticationHandler>(HiveAuthSchemes.User, _ => { });
    authBuilder.AddScheme<AuthenticationSchemeOptions, TestingHeaderAuthenticationHandler>(HiveAuthSchemes.DeviceCert, _ => { });
}
else
{
    var userMode = builder.Configuration["Authentication:UserMode"] ?? "LocalJwt";

    if (string.Equals(userMode, "Negotiate", StringComparison.OrdinalIgnoreCase))
    {
        authBuilder.AddNegotiate(HiveAuthSchemes.User, _ => { });
        builder.Services.AddSingleton<IClaimsTransformation, AdRoleMappingClaimsTransformation>();
    }
    else if (string.Equals(userMode, "LocalJwt", StringComparison.OrdinalIgnoreCase))
    {
        authBuilder.AddJwtBearer(HiveAuthSchemes.User, _ => { });
        builder.Services.AddOptions<JwtBearerOptions>(HiveAuthSchemes.User)
            .Configure<JwtTokenService, TimeProvider>((options, tokenService, timeProvider) =>
            {
                options.TimeProvider = timeProvider;
                options.TokenValidationParameters = tokenService.CreateTokenValidationParameters();
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/state"))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        var subValue = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub)
                            ?? context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                        if (!Guid.TryParse(subValue, out var userId))
                        {
                            context.Fail("Token subject is missing or invalid.");
                            return;
                        }

                        var userRepo = context.HttpContext.RequestServices.GetRequiredService<IUserAccountRepository>();
                        var user = await userRepo.GetByIdAsync(userId, context.HttpContext.RequestAborted);
                        if (user is null || !user.IsActive)
                        {
                            context.Fail("User account is missing or deactivated.");
                            return;
                        }
                    }
                };
            });
    }
    else
    {
        throw new InvalidOperationException(
            $"Unsupported Authentication:UserMode '{userMode}'. Expected 'LocalJwt' or 'Negotiate'.");
    }

    authBuilder.AddCertificate(HiveAuthSchemes.DeviceCert, options =>
    {
        options.AllowedCertificateTypes = CertificateTypes.All;
        options.Events = new CertificateAuthenticationEvents
        {
            OnCertificateValidated = async context =>
            {
                var thumbprint = context.ClientCertificate?.Thumbprint;
                if (string.IsNullOrWhiteSpace(thumbprint))
                {
                    context.Fail("Client certificate thumbprint is missing.");
                    return;
                }

                var deviceRepo = context.HttpContext.RequestServices.GetRequiredService<IDeviceRepository>();
                var device = await deviceRepo.GetByThumbprintAsync(thumbprint, context.HttpContext.RequestAborted);
                if (device is null || device.EnrollmentState != EnrollmentState.Active)
                {
                    context.Fail("Device certificate is unknown or revoked.");
                    return;
                }

                var claims = new[]
                {
                    new Claim(HiveClaimTypes.ActorType, "Device"),
                    new Claim(HiveClaimTypes.DeviceId, device.DeviceId.ToString("D")),
                    new Claim(HiveClaimTypes.Site, device.SiteId),
                    new Claim(ClaimTypes.NameIdentifier, device.DeviceId.ToString("D")),
                    new Claim(ClaimTypes.Name, device.Hostname),
                    new Claim(ClaimTypes.Thumbprint, device.CertificateThumbprint)
                };

                context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, context.Scheme.Name));
                context.Success();
            }
        };
    });
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(HiveAuthPolicies.UserOnly, policy =>
    {
        policy.AddAuthenticationSchemes(HiveAuthSchemes.User);
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => !ctx.User.HasClaim(HiveClaimTypes.ActorType, "Device"));
    });

    options.AddPolicy(HiveAuthPolicies.DeviceOnly, policy =>
    {
        policy.AddAuthenticationSchemes(HiveAuthSchemes.DeviceCert);
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(HiveClaimTypes.DeviceId);
    });
});

builder.Services.AddHostedService<OutboxDispatcherService>();

var app = builder.Build();

if (usePostgres && builder.Configuration.GetValue("Storage:AutoMigrate", true))
{
    var runner = app.Services.GetRequiredService<PostgreSqlMigrationRunner>();
    await runner.MigrateAsync();
}

await BootstrapAdminInitializer.EnsureBootstrapAdminAsync(app.Services);

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

app.MapHiveEndpoints();
app.MapHub<StateHub>("/hubs/state");

app.Run();

public partial class Program;
