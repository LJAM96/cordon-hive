using System.Text.Json;
using Hive.Domain.Audit;
using Hive.Domain.Identity;
using Hive.Infrastructure.Repositories;

namespace Hive.Server.Services;

public static class BootstrapAdminInitializer
{
    public static async Task EnsureBootstrapAdminAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var userRepository = services.GetRequiredService<IUserAccountRepository>();
        var auditRepository = services.GetRequiredService<IAuditRepository>();
        var timeProvider = services.GetRequiredService<TimeProvider>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("BootstrapAdminInitializer");

        if (await userRepository.CountAsync(cancellationToken) > 0)
        {
            return;
        }

        var username = configuration["BootstrapAdmin:Username"]?.Trim();
        var password = !string.IsNullOrWhiteSpace(configuration["BootstrapAdmin:Password"])
            ? configuration["BootstrapAdmin:Password"]
            : configuration["hive_bootstrap_admin_password"]?.Trim();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var (isValid, error) = PasswordPolicy.Validate(password);
        if (!isValid)
        {
            throw new InvalidOperationException($"BootstrapAdmin password failed policy validation: {error}");
        }

        var displayName = configuration["BootstrapAdmin:DisplayName"]?.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = "Bootstrap Administrator";
        }

        var nowUtc = timeProvider.GetUtcNow();
        var adminUser = new UserAccount(
            Guid.NewGuid(),
            username,
            UserAccount.NormalizeUsername(username),
            displayName,
            PasswordHasher.HashPassword(password),
            new HashSet<HiveRole> { HiveRole.Administrator },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AllSitesAccess: true,
            IsActive: true,
            MustChangePassword: true,
            FailedLoginCount: 0,
            LockedUntilUtc: null,
            CreatedAtUtc: nowUtc,
            UpdatedAtUtc: nowUtc);

        if (!await userRepository.CreateAsync(adminUser, cancellationToken))
        {
            return;
        }

        await auditRepository.AppendAsync(
            AuditEvent.Create(
                "System",
                "bootstrap",
                "BootstrapAdminCreated",
                "UserAccount",
                adminUser.UserId.ToString("D"),
                "Success",
                null,
                Guid.NewGuid().ToString("N"),
                nowUtc,
                JsonSerializer.Serialize(new { username = adminUser.Username })),
            cancellationToken);

        logger.LogInformation("Created bootstrap administrator account '{Username}'.", adminUser.Username);
    }
}
