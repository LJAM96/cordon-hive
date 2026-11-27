using System.Reflection;
using Npgsql;

namespace Hive.Infrastructure.Migrations;

/// <summary>
/// Applies embedded PostgreSQL SQL migrations idempotently in lexical order.
/// </summary>
public sealed class PostgreSqlMigrationRunner
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlMigrationRunner(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using (var initCmd = connection.CreateCommand())
        {
            initCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version TEXT PRIMARY KEY,
                    applied_at_utc TIMESTAMPTZ NOT NULL
                );
                """;
            await initCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var appliedVersions = new HashSet<string>(StringComparer.Ordinal);
        await using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.CommandText = "SELECT version FROM schema_migrations;";
            await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                appliedVersions.Add(reader.GetString(0));
            }
        }

        var assembly = typeof(PostgreSqlMigrationRunner).Assembly;
        var migrationResources = assembly
            .GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        foreach (var resourceName in migrationResources)
        {
            var version = ExtractMigrationVersion(resourceName);
            if (appliedVersions.Contains(version))
            {
                continue;
            }

            await using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Missing embedded migration resource '{resourceName}'.");
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(cancellationToken);

            await using var tx = await connection.BeginTransactionAsync(cancellationToken);

            await using (var applyCmd = connection.CreateCommand())
            {
                applyCmd.Transaction = tx;
                applyCmd.CommandText = sql;
                await applyCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var recordCmd = connection.CreateCommand())
            {
                recordCmd.Transaction = tx;
                recordCmd.CommandText = """
                    INSERT INTO schema_migrations (version, applied_at_utc)
                    VALUES (@version, @applied_at_utc)
                    ON CONFLICT (version) DO NOTHING;
                    """;
                recordCmd.Parameters.AddWithValue("version", version);
                recordCmd.Parameters.AddWithValue("applied_at_utc", DateTimeOffset.UtcNow);
                await recordCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }
    }

    private static string ExtractMigrationVersion(string resourceName)
    {
        const string marker = ".Migrations.";
        var idx = resourceName.IndexOf(marker, StringComparison.Ordinal);
        return idx >= 0 ? resourceName[(idx + marker.Length)..] : resourceName;
    }
}
