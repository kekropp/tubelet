using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Tubelet.Data;

internal static class DapperConfig
{
    // Maps snake_case columns onto PascalCase members everywhere, including unit tests
    // that never boot the web host.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => DefaultTypeMap.MatchNamesWithUnderscores = true;
}

/// <summary>Connection factory for the single SQLite file. All access goes through here.</summary>
public sealed class Database
{
    private readonly string _connString;

    public string DbPath { get; }

    public Database(string dbPath)
    {
        DbPath = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        _connString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        conn.Execute(
            "PRAGMA journal_mode=WAL;" +
            "PRAGMA synchronous=NORMAL;" +
            "PRAGMA busy_timeout=5000;" +
            "PRAGMA cache_size=-64000;");
        return conn;
    }

    /// <summary>
    /// Advance and return the global change sequence. Every mutation that the Jellyfin
    /// plugin must observe stamps its row's changed_at with this value; /changes?since=
    /// is then a single indexed range scan.
    /// </summary>
    public static long NextSeq(SqliteConnection conn, IDbTransaction? tx = null) =>
        conn.ExecuteScalar<long>(
            "UPDATE settings SET value = CAST(value AS INTEGER) + 1 WHERE key = 'change_seq' RETURNING CAST(value AS INTEGER)",
            transaction: tx);

    public static long CurrentSeq(SqliteConnection conn) =>
        conn.ExecuteScalar<long>("SELECT CAST(value AS INTEGER) FROM settings WHERE key = 'change_seq'");

    public static string? GetSetting(SqliteConnection conn, string key) =>
        conn.ExecuteScalar<string?>("SELECT value FROM settings WHERE key = @key", new { key });

    public static void SetSetting(SqliteConnection conn, string key, string value, IDbTransaction? tx = null) =>
        conn.Execute(
            "INSERT INTO settings(key, value) VALUES(@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key, value }, tx);
}
