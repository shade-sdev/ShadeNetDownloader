using Microsoft.Data.Sqlite;
using ShadeNet.Core.Models;
using ShadeNet.Core.Storage;

namespace ShadeNet.Core.Tests;

/// <summary>
/// One database, two builds of ShadeNet (a development build and the installed one share the data folder): opening it never fails on a
/// column the other build added already, and a newer build's version is never lowered.
/// </summary>
public class SchemaMigrationTests
{
    static string NewDb() => Path.Combine(Path.GetTempPath(), "sn-schema-" + Guid.NewGuid().ToString("N")[..8], "shadenet.db");

    static void Raw(string db, string sql)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static long Version(string db)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public void A_database_whose_version_an_older_build_lowered_opens_with_the_columns_it_has()
    {
        var db = NewDb();
        try
        {
            using (new JobStore(db)) { }     // the newer build: every column, version 3
            Raw(db, "PRAGMA user_version=2;");    // the older build, started on the same database, wrote its own version

            using (var store = new JobStore(db))  // was: "duplicate column name: torrent_data", and ShadeNet did not start
            {
                store.SaveJob(new DownloadJob { Url = "magnet:?xt=urn:btih:00", FileName = "x", SaveDirectory = Path.GetTempPath(), Kind = DownloadKind.Torrent });
                Assert.Single(store.LoadAll());
            }

            Assert.Equal(3, Version(db));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(db)!, true);
        }
    }

    [Fact]
    public void A_newer_builds_version_is_not_lowered()
    {
        var db = NewDb();
        try
        {
            using (new JobStore(db)) { }
            Raw(db, "PRAGMA user_version=7;");   // a later ShadeNet used it
            using (new JobStore(db)) { }
            Assert.Equal(7, Version(db));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(db)!, true);
        }
    }
}
