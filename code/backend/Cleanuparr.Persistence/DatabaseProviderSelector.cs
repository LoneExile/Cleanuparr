using Cleanuparr.Shared.Helpers;
using Microsoft.EntityFrameworkCore;

namespace Cleanuparr.Persistence;

/// <summary>
/// Provides a common database-provider selection for all Cleanuparr DbContexts.
/// When DATABASE_PROVIDER=postgres, uses Npgsql with a connection string built
/// from POSTGRES_HOST, POSTGRES_PORT, POSTGRES_DB, POSTGRES_USER, POSTGRES_PASSWORD
/// env vars. Alternatively, a full POSTGRES_CONNECTION_STRING can be set to override.
/// Otherwise falls back to the original SQLite file-based store.
/// </summary>
public static class DatabaseProviderSelector
{
    private static readonly Lazy<bool> _usePostgres = new(() =>
    {
        var provider = Environment.GetEnvironmentVariable("DATABASE_PROVIDER");
        return string.Equals(provider, "postgres", StringComparison.OrdinalIgnoreCase);
    });

    public static bool UsePostgres => _usePostgres.Value;

    private static string GetPostgresConnectionString(string dbName)
    {
        // If a full connection string is provided, use it directly.
        var fullConnStr = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(fullConnStr))
        {
            return fullConnStr;
        }

        // Otherwise build from parts — the password comes from a K8s secretKeyRef.
        var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
        var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
        var db   = Environment.GetEnvironmentVariable($"POSTGRES_DB_{dbName.ToUpper()}") ?? dbName;
        var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "cleanuparr";
        var pass = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "";
        return $"Host={host};Port={port};Database={db};Username={user};Password={pass}";
    }

    /// <summary>
    /// Configures the DbContextOptionsBuilder with either Npgsql (Postgres) or SQLite,
    /// and applies the shared naming conventions (lowercase + snake_case).
    /// </summary>
    /// <param name="optionsBuilder">The options builder from OnConfiguring.</param>
    /// <param name="sqliteDbFileName">The SQLite file name (e.g. "cleanuparr.db"); the
    /// basename (without extension) is used as the Postgres DB name when Postgres is active.</param>
    public static void Configure(DbContextOptionsBuilder optionsBuilder, string sqliteDbFileName)
    {
        if (optionsBuilder.IsConfigured)
        {
            return;
        }

        // The db basename: "cleanuparr.db" -> "cleanuparr", "events.db" -> "events".
        var dbName = Path.GetFileNameWithoutExtension(sqliteDbFileName);

        if (UsePostgres)
        {
            var connStr = GetPostgresConnectionString(dbName);
            optionsBuilder
                .UseNpgsql(connStr)
                .UseLowerCaseNamingConvention()
                .UseSnakeCaseNamingConvention()
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        }
        else
        {
            var dbPath = Path.Combine(ConfigurationPathProvider.GetConfigPath(), sqliteDbFileName);
            optionsBuilder
                .UseSqlite($"Data Source={dbPath}")
                .UseLowerCaseNamingConvention()
                .UseSnakeCaseNamingConvention();
        }
    }
}
