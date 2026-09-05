using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NexusApp.Models;
using Serilog;

namespace NexusApp.Data;

/// <summary>
/// Handles database migration (with a safe baseline for pre-migration databases)
/// and seeding of default data.
/// </summary>
internal static class DbSeeder
{
    private const string defaultvalue = "false";

    // All default settings — used both for fresh installs and for adding missing keys to existing DBs.
    private static readonly (string Key, string Value, SettingType Type, string Description)[] DefaultSettings =
    [
        ("TradingMode",     "manual", SettingType.String,  "manual | auto"),
        ("DefaultQuantity", "1",      SettingType.Integer, "Default lots per trade"),
        ("StopLossBuffer",  "0",      SettingType.Decimal, "Buffer added to signal stop-loss"),
        ("Lots.NIFTY",      "0",      SettingType.Integer, "NIFTY lot override (0 = use DefaultQuantity)"),
        ("Lots.BANKNIFTY",  "0",      SettingType.Integer, "BANKNIFTY lot override"),
        ("Lots.FINNIFTY",   "0",      SettingType.Integer, "FINNIFTY lot override"),
        ("Lots.MIDCPNIFTY", "0",      SettingType.Integer, "MIDCPNIFTY lot override"),
        ("Lots.SENSEX",     "0",      SettingType.Integer, "SENSEX lot override"),
        ("Lots.BANKEX",     "0",      SettingType.Integer, "BANKEX lot override"),
        ("Risk.SlMode",     "Points", SettingType.String,  "SL/Target mode: Points | Percent"),
        ("SL.Points.NIFTY",         "20",  SettingType.Decimal, "Default SL points for NIFTY"),
        ("Target.Points.NIFTY",     "35",  SettingType.Decimal, "Default Target points for NIFTY"),
        ("SL.Percent.NIFTY",        "18",  SettingType.Decimal, "Default SL % for NIFTY"),
        ("Target.Percent.NIFTY",    "35",  SettingType.Decimal, "Default Target % for NIFTY"),
        ("SL.Points.BANKNIFTY",     "40",  SettingType.Decimal, "Default SL points for BANKNIFTY"),
        ("Target.Points.BANKNIFTY", "70",  SettingType.Decimal, "Default Target points for BANKNIFTY"),
        ("SL.Percent.BANKNIFTY",    "20",  SettingType.Decimal, "Default SL % for BANKNIFTY"),
        ("Target.Percent.BANKNIFTY","40",  SettingType.Decimal, "Default Target % for BANKNIFTY"),
        ("SL.Points.FINNIFTY",      "20",  SettingType.Decimal, "Default SL points for FINNIFTY"),
        ("Target.Points.FINNIFTY",  "35",  SettingType.Decimal, "Default Target points for FINNIFTY"),
        ("SL.Percent.FINNIFTY",     "18",  SettingType.Decimal, "Default SL % for FINNIFTY"),
        ("Target.Percent.FINNIFTY", "35",  SettingType.Decimal, "Default Target % for FINNIFTY"),
        ("SL.Points.MIDCPNIFTY",    "15",  SettingType.Decimal, "Default SL points for MIDCPNIFTY"),
        ("Target.Points.MIDCPNIFTY","25",  SettingType.Decimal, "Default Target points for MIDCPNIFTY"),
        ("SL.Percent.MIDCPNIFTY",   "15",  SettingType.Decimal, "Default SL % for MIDCPNIFTY"),
        ("Target.Percent.MIDCPNIFTY","30", SettingType.Decimal, "Default Target % for MIDCPNIFTY"),
        ("SL.Points.SENSEX",        "80",  SettingType.Decimal, "Default SL points for SENSEX"),
        ("Target.Points.SENSEX",    "140", SettingType.Decimal, "Default Target points for SENSEX"),
        ("SL.Percent.SENSEX",       "25",  SettingType.Decimal, "Default SL % for SENSEX"),
        ("Target.Percent.SENSEX",   "50",  SettingType.Decimal, "Default Target % for SENSEX"),
        ("SL.Points.BANKEX",        "90",  SettingType.Decimal, "Default SL points for BANKEX"),
        ("Target.Points.BANKEX",    "150", SettingType.Decimal, "Default Target points for BANKEX"),
        ("SL.Percent.BANKEX",       "25",  SettingType.Decimal, "Default SL % for BANKEX"),
        ("Target.Percent.BANKEX",   "50",  SettingType.Decimal, "Default Target % for BANKEX"),
        ("DailyMaxLoss",    "1000",   SettingType.Decimal, "Max loss per day in ₹"),
        ("DailyMaxProfit",  "5000",   SettingType.Decimal, "Max profit target per day in ₹"),
        ("MaxOpenPositions","5",      SettingType.Integer, "Max simultaneous open positions"),
        ("MaxTradesPerDay", "10",     SettingType.Integer, "Max trades per day"),
        ("AllowAfterMarketHours", defaultvalue, SettingType.Boolean, "Allow order placement outside market hours"),
        ("AutoSquareOffEnabled", defaultvalue, SettingType.Boolean, "Automatically square off all open positions at a set time before market close"),
        ("AutoSquareOffTime", "14:50", SettingType.String, "Time (HH:mm IST) at which open positions are auto squared off"),
        ("RiskAutoSquareOffEnabled", defaultvalue, SettingType.Boolean, "Automatically square off all open live positions for an account when its Daily Max Profit or Daily Max Loss is hit"),
        ("EnableEntryPriceCrossingTrigger", defaultvalue, SettingType.Boolean, "Hold execution until CMP crosses entry price"),
        ("RequireEntryCrossFromBelow", "true", SettingType.Boolean, "Only trigger entry after the contract has traded below the entry price and then crosses up (prevents chasing when the signal arrives above entry)"),
        ("DuplicateSignalCooldownMinutes", "2", SettingType.Integer, "Block repeat executions of the same contract from the same channel for this many minutes (0 = disabled)"),
        ("PendingOrderTimeoutMinutes", "5", SettingType.Integer, "Cancel real broker orders pending longer than this many minutes (0 = disabled)"),
        ("SignalTimeWindowEnabled", defaultvalue, SettingType.Boolean, "Only execute signals received within the configured IST time window"),
        ("SignalTimeWindowStart", "09:15", SettingType.String, "Earliest IST time (HH:mm) at which signals may be executed"),
        ("SignalTimeWindowEnd", "15:00", SettingType.String, "Latest IST time (HH:mm) at which signals may be executed"),
        ("SlippageGuardEnabled", defaultvalue, SettingType.Boolean, "Reject live orders when CMP has slipped beyond the allowed % from the signal entry price"),
        ("MaxSlippagePercent", "1.0", SettingType.Decimal, "Maximum allowed adverse slippage between signal entry price and live CMP (percent)"),
        ("PnlHistoryRetentionDays", "90", SettingType.Integer, "Delete durable P&L history older than this many days (0 = keep forever)"),
        ("Watchlist.HiddenSignalIds", "", SettingType.String, "Soft-hidden watchlist signal IDs"),
        ("KillSwitch",      defaultvalue,  SettingType.Boolean, "Emergency kill switch"),
    ];

    // Applies EF Core migrations. Handles the one-time transition from the old
    // EnsureCreated() provisioning to migrations: if the database already has the
    // application tables but no __EFMigrationsHistory, the initial migration is
    // "baselined" (recorded as applied) instead of re-created, so we never try to
    // CREATE tables that already exist. Fresh databases just get all migrations applied.
    public static async Task MigrateAsync(TradingDbContext context)
    {
        var db = context.Database;

        // Configure SQLite for better write concurrency BEFORE any writes.
        // WAL lets a single writer proceed alongside readers, and busy_timeout
        // makes concurrent writers wait for the lock (up to 30s) instead of
        // failing immediately with "database is locked" (SQLite Error 5).
        await ConfigureSqlitePragmasAsync(db);

        var pending = (await db.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0)
            return;

        var applied = (await db.GetAppliedMigrationsAsync()).ToList();

        // Detect a legacy EnsureCreated() database: application tables exist but no
        // migration has ever been recorded. In that case, stamp the initial migration
        // as applied without running its CREATE TABLE statements.
        if (applied.Count == 0 && await LegacyTablesExistAsync(db))
        {
            var initial = pending[0];
            await db.ExecuteSqlRawAsync(
                "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (" +
                "\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, " +
                "\"ProductVersion\" TEXT NOT NULL);");
            await db.ExecuteSqlRawAsync(
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({0}, {1});",
                initial, "10.0.9");
            Log.Information("Baselined existing database at migration {MigrationId}", initial);
        }

        // Apply any remaining (or all, for fresh DBs) migrations.
        await db.MigrateAsync();
    }

    // Returns true if the core application tables already exist (legacy EnsureCreated DB).
    private static async Task<bool> LegacyTablesExistAsync(DatabaseFacade db)
    {
        await using var connection = db.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
            await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ApplicationSettings';";
            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt64(result) > 0;
        }
        finally
        {
            if (wasClosed)
                await connection.CloseAsync();
        }
    }

    // Enables WAL journal mode (persistent) and a per-connection busy timeout so
    // concurrent writers wait for the lock instead of failing with SQLite Error 5
    // ("database is locked"). Safe to run on every startup.
    private static async Task ConfigureSqlitePragmasAsync(DatabaseFacade db)
    {
        if (!db.IsSqlite())
            return;

        await using var connection = db.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
            await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "PRAGMA journal_mode=WAL; " +
                "PRAGMA busy_timeout=30000; " +
                "PRAGMA synchronous=NORMAL;";
            await command.ExecuteNonQueryAsync();
            Log.Information("SQLite configured: journal_mode=WAL, busy_timeout=30000ms, synchronous=NORMAL");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to apply SQLite concurrency PRAGMAs");
        }
        finally
        {
            if (wasClosed)
                await connection.CloseAsync();
        }
    }

    public static async Task SeedAsync(TradingDbContext context)
    {
        // Seed default trading account on fresh install
        if (!await context.TradingAccounts.AnyAsync())
        {
            context.TradingAccounts.Add(new TradingAccount
            {
                Name = "Paper Trading Account",
                BrokerType = "AngelOne",
                ClientId = "PAPER",
                IsEnabled = true,
                IsDefault = true,
                DailyMaxLoss = 1000,
                DailyMaxProfit = 5000,
                MaxOpenPositions = 5,
                MaxTradesPerDay = 10,
                DefaultQuantity = 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        // Upsert all default settings — adds missing keys to existing DBs without overwriting user changes
        var existingKeys = await context.ApplicationSettings
            .Select(s => s.Key)
            .ToHashSetAsync();

        foreach (var (key, value, type, desc) in DefaultSettings)
        {
            if (!existingKeys.Contains(key))
                context.ApplicationSettings.Add(new ApplicationSetting(key, value, type, desc));
        }

        await context.SaveChangesAsync();
    }
}
