using System.Reflection;
using Cleanuparr.Infrastructure.Services;
using Cleanuparr.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cleanuparr.Api;

public static class HostExtensions
{
    public static IHost Init(this WebApplication app)
    {
        ILogger<Program> logger = app.Services.GetRequiredService<ILogger<Program>>();
        AppStatusSnapshot statusSnapshot = app.Services.GetRequiredService<AppStatusSnapshot>();

        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        string? formattedVersion = FormatVersion(version);

        if (statusSnapshot.UpdateCurrentVersion(formattedVersion, out _))
        {
            logger.LogDebug("App status current version set to {Version}", formattedVersion);
        }

        logger.LogInformation(
            version is null
                ? "Cleanuparr version not detected"
                : $"Cleanuparr {formattedVersion}"
        );

        logger.LogInformation("timezone: {tz}", TimeZoneInfo.Local.DisplayName);

        LogGcConfiguration(logger);

        return app;
    }

    private static void LogGcConfiguration(ILogger logger)
    {
        // Surface the active GC settings
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        long totalAvailableMb = info.TotalAvailableMemoryBytes / (1024 * 1024);
        long heapHardLimitMb = info.HighMemoryLoadThresholdBytes / (1024 * 1024);

        logger.LogInformation(
            "Garbage Collector config | server={ServerGC} concurrent={ConcurrentGC} latencyMode={Latency} totalAvailable={TotalMb} MB highMemoryLoadThreshold={HighMb} MB",
            System.Runtime.GCSettings.IsServerGC,
            info.Concurrent,
            System.Runtime.GCSettings.LatencyMode,
            totalAvailableMb,
            heapHardLimitMb);
    }

    private static string? FormatVersion(Version? version)
    {
        if (version is null)
        {
            return null;
        }

        if (version.Build >= 0)
        {
            return $"v{version.Major}.{version.Minor}.{version.Build}";
        }

        return $"v{version.Major}.{version.Minor}";
    }

    public static async Task<WebApplicationBuilder> InitAsync(this WebApplicationBuilder builder)
    {
        // Postgres: use EnsureCreated (migrations are SQLite-specific and can't
        // translate to PG; the model's OnModelCreating creates all tables/seed data).
        // SQLite: use Migrate (existing migrations apply normally).
        if (DatabaseProviderSelector.UsePostgres)
        {
            // Postgres: create tables using the model's GENERATED SQL (not Migrate or
            // EnsureCreated — both have internal queries that fail on PG with bool=int).
            // Use raw Npgsql to execute the script directly.
            var connStr = System.Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING");
            if (string.IsNullOrEmpty(connStr))
            {
                var host = System.Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? "localhost";
                var port = System.Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? "5432";
                var user2 = System.Environment.GetEnvironmentVariable("POSTGRES_USER") ?? "cleanuparr";
                var pass = System.Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? "";
                connStr = $"Host={host};Port={port};Username={user2};Password={pass}";
            }
            static string FixSql(string sql) => sql.Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ").Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ").Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ");
            static async Task ExecuteSchemaAsync(string dbName, Func<string> generateSql, string connStr)
            {
                await using var conn = new Npgsql.NpgsqlConnection(connStr + $";Database={dbName}");
                await conn.OpenAsync();
                var sql = FixSql(generateSql());
                // Execute each statement separately to isolate the failing one
                // and avoid boolean=integer errors from batch parsing.
                foreach (var stmt in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (string.IsNullOrWhiteSpace(stmt)) continue;
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = stmt;
                        await cmd.ExecuteNonQueryAsync();
                        Console.WriteLine($"[schema] OK: {stmt[..Math.Min(80, stmt.Length)]}...");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[schema] FAILED: {stmt[..Math.Min(120, stmt.Length)]}");
                        Console.WriteLine($"[schema] Error: {ex.Message}");
                        throw;
                    }
                }
                await conn.CloseAsync();
            }
            var configDbName = System.Environment.GetEnvironmentVariable("POSTGRES_DB_CLEANUPARR") ?? "cleanuparr";
            var configCtxForSql = DataContext.CreateStaticInstance();
            await ExecuteSchemaAsync(configDbName, () => configCtxForSql.Database.GenerateCreateScript(), connStr);
            var eventsDbName = System.Environment.GetEnvironmentVariable("POSTGRES_DB_EVENTS") ?? "events";
            var eventsCtxForSql = EventsContext.CreateStaticInstance();
            await ExecuteSchemaAsync(eventsDbName, () => eventsCtxForSql.Database.GenerateCreateScript(), connStr);
            var usersDbName = System.Environment.GetEnvironmentVariable("POSTGRES_DB_USERS") ?? "users";
            var usersCtxForSql = UsersContext.CreateStaticInstance();
            await ExecuteSchemaAsync(usersDbName, () => usersCtxForSql.Database.GenerateCreateScript(), connStr);
            // Seed default config rows if empty (the app expects exactly one
            // general_config row + one queue_cleaner_config + etc. for startup).
            await using var seedConn = new Npgsql.NpgsqlConnection(connStr + $";Database={configDbName}");
            await seedConn.OpenAsync();
            using var seedCmd = seedConn.CreateCommand();
            seedCmd.CommandText = """
                INSERT INTO general_configs (id, display_support_banner, dry_run, http_max_retries, http_timeout, http_certificate_validation, status_check_enabled, encryption_key, ignored_downloads, strike_inactivity_window_hours, history_retention_days, auth_disable_auth_for_local_addresses, auth_trust_forwarded_headers, auth_trusted_networks, log_archive_enabled, log_archive_retained_count, log_archive_time_limit_hours, log_level, log_retained_file_count, log_rolling_size_mb, log_time_limit_hours)
                SELECT gen_random_uuid(), true, false, 3, 60, 'enabled', false, 'default-key', '{}', 0, 30, true, false, '', true, 7, 24, 'debug', 5, 1, 12
                WHERE NOT EXISTS (SELECT 1 FROM general_configs);
                INSERT INTO queue_cleaner_configs (id, enabled, cron_expression, use_advanced_scheduling, ignored_downloads, process_no_content_id, downloading_metadata_max_strikes, failed_import_change_category, failed_import_delete_private, failed_import_ignore_private, failed_import_max_strikes, failed_import_pattern_mode, failed_import_patterns, failed_import_skip_if_not_found_in_client)
                SELECT gen_random_uuid(), false, '0 */6 * * *', false, '{}', false, 3, false, false, false, 3, 'wildcard', '{}', true
                WHERE NOT EXISTS (SELECT 1 FROM queue_cleaner_configs);
                INSERT INTO content_blocker_configs (id, enabled, cron_expression, use_advanced_scheduling, ignore_private, delete_private, process_no_content_id, delete_if_any_file_blocked, ignored_downloads, lidarr_blocklist_type, lidarr_enabled, radarr_blocklist_type, radarr_enabled, readarr_blocklist_type, readarr_enabled, sonarr_blocklist_type, sonarr_enabled, whisparr_blocklist_type, whisparr_enabled)
                SELECT gen_random_uuid(), false, '0 */6 * * *', false, false, false, false, true, '{}', 'path_prefix', false, 'path_prefix', false, 'path_prefix', false, 'path_prefix', false, 0, false
                WHERE NOT EXISTS (SELECT 1 FROM content_blocker_configs);
                INSERT INTO download_cleaner_configs (id, enabled, cron_expression, use_advanced_scheduling, ignored_downloads)
                SELECT gen_random_uuid(), false, '0 */6 * * *', false, '{}'
                WHERE NOT EXISTS (SELECT 1 FROM download_cleaner_configs);
                INSERT INTO blacklist_sync_configs (id, enabled, cron_expression, blacklist_path)
                SELECT gen_random_uuid(), false, '0 */6 * * *', 'https://raw.githubusercontent.com/Cleanuparr/Cleanuparr/main/blocklist.txt'
                WHERE NOT EXISTS (SELECT 1 FROM blacklist_sync_configs);
                INSERT INTO seeker_configs (id, search_enabled, search_interval, proactive_search_enabled, selection_strategy, use_round_robin, post_release_grace_hours)
                SELECT gen_random_uuid(), false, 30, false, 'round_robin', false, 0
                -- (created when the user adds a download client in the UI), so skipped here.
                """;
            await seedCmd.ExecuteNonQueryAsync();
            await seedConn.CloseAsync();
        }
        else
        {
            // Apply data db migrations first — events migrations may ATTACH cleanuparr.db
            // and reference its schema, so it must be up to date before events migrate.
            await using var configContext = DataContext.CreateStaticInstance();
            if ((await configContext.Database.GetPendingMigrationsAsync()).Any())
            {
                await configContext.Database.MigrateAsync();
            }
            // Apply events db migrations
            await using var eventsContext = EventsContext.CreateStaticInstance();
            if ((await eventsContext.Database.GetPendingMigrationsAsync()).Any())
            {
                await eventsContext.Database.MigrateAsync();
            }
            // WAL gives better write concurrency and write throughput (SQLite only).
            await eventsContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            // Apply users db migrations
            await using var usersContext = UsersContext.CreateStaticInstance();
            if ((await usersContext.Database.GetPendingMigrationsAsync()).Any())
            {
                await usersContext.Database.MigrateAsync();
            }
        }

        return builder;
    }
}