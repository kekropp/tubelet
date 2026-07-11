using System.Reflection;
using Dapper;

namespace Tubelet.Data;

/// <summary>
/// user_version-based migrations. Version 1 is the embedded schema.sql;
/// later migrations append to the list below and run in order.
/// </summary>
public static class Migrator
{
    private static readonly (long Version, Func<string> Sql)[] Migrations =
    [
        (1, ReadEmbeddedSchema),
        // Per-job quality profile stamped at enqueue (subscription override); NULL = follow global settings.
        (2, () => "ALTER TABLE jobs ADD COLUMN format TEXT"),
    ];

    public static void Migrate(Database db)
    {
        using var conn = db.Open();
        var current = conn.ExecuteScalar<long>("PRAGMA user_version");
        foreach (var (version, sql) in Migrations)
        {
            if (version <= current) continue;
            using var tx = conn.BeginTransaction();
            conn.Execute(sql(), transaction: tx);
            conn.Execute($"PRAGMA user_version = {version}", transaction: tx);
            tx.Commit();
        }
    }

    private static string ReadEmbeddedSchema()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("Tubelet.Db.schema.sql")
            ?? throw new InvalidOperationException("Embedded schema.sql not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
