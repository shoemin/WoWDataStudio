using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WoWData.Core.Index;

namespace WoWData.Tests;

/// <summary>
/// M1 (docs\research\OPEN_OTHER_VERSIONS_DESIGN.md, 5.2): opening other versions must leave the 1.12 index exactly
/// as it was. The digests below are of the index the code before M1 (commit 8c65e9e) built from the Client3 test copy
/// without the server database (rows in order, values as text); this test builds the index with today's code from
/// the same copy and compares the entry, asset and model_variant tables. On another 1.12 copy the digests do not apply
/// and only the absence of the modern tables is checked.
/// </summary>
[Collection(IndexCollection.Name)]
public class Index112UnchangedTests : IDisposable
{
    private const string Client3FingerprintSha256 = "15e279bbc5b460ff2a4629638e2add684bb4bee8fcabcf98ddae6ccc7c273721";

    private static readonly Dictionary<string, string> Before = new()
    {
        ["entry"] = "6cadeca28f78cba708ba65ab5cd6af45447669365fd82cc686011755d023593b",
        ["asset"] = "4af31230781eb6b9c91bc5329c09e8fc28faae9ed8b2138e6d35cbe346885b44",
        ["model_variant"] = "9069e3c82fc928e31150c82e8e6fbd7b9b5968b4d0723cd555446645272b542e",
    };

    private readonly string _dir = TestEnv.TempDir("index112");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>The digest of a table: its rows in rowid order, each value as text (NULL as \0), \x1f between values,
    /// \x1e after each row (the same as the script that measured the "before" digests).</summary>
    private static string Digest(SqliteConnection db, string table)
    {
        using var c = db.CreateCommand();
        c.CommandText = $"SELECT * FROM {table} ORDER BY rowid";
        using var r = c.ExecuteReader();
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var sb = new StringBuilder();
        while (r.Read())
        {
            sb.Clear();
            for (int i = 0; i < r.FieldCount; i++)
            {
                if (i > 0) sb.Append('\x1f');
                sb.Append(r.IsDBNull(i) ? "\0" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append('\x1e');
            h.AppendData(Encoding.UTF8.GetBytes(sb.ToString()));
        }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    [Fact]
    public void TheIndexOfYour112GameIsTheSameAsBeforeOtherVersionsCouldBeOpened()
    {
        var path = Path.Combine(_dir, "index.sqlite");
        IndexBuilder.BuildAsync(TestEnv.Client, path, TestEnv.NoServer).GetAwaiter().GetResult();
        using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        db.Open();
        var tables = new List<string>();
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var r = c.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }
        // none of the modern index's tables, and no "kind" in its meta
        Assert.DoesNotContain("fdid", tables);
        Assert.DoesNotContain("locked", tables);
        Assert.DoesNotContain("named_creature", tables);
        using var ix = new AssetIndex(path);
        Assert.False(ix.IsModern);
        var fp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ix.Meta()["fingerprint"]))).ToLowerInvariant();
        if (fp != Client3FingerprintSha256) return;   // another 1.12 copy: the measured digests do not apply
        foreach (var (table, digest) in Before) Assert.True(Digest(db, table) == digest, $"the {table} table of the 1.12 index changed");
    }
}
