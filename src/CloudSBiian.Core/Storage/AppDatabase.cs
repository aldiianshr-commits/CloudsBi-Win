using Microsoft.Data.Sqlite;

namespace CloudSBiian.Core.Storage;

/// <summary>
/// Thin SQLite wrapper. One connection per operation (pooled by Microsoft.Data.Sqlite),
/// WAL journal so the UI can read while the cache engine writes.
/// </summary>
public sealed class AppDatabase
{
    private readonly string _connectionString;

    public AppDatabase(string path)
    {
        FilePath = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
        Migrate();
    }

    public string FilePath { get; }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public int Execute(string sql, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        using var cmd = Build(c, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public T? Scalar<T>(string sql, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        using var cmd = Build(c, sql, args);
        var v = cmd.ExecuteScalar();
        if (v is null || v is DBNull)
        {
            return default;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(v, target, System.Globalization.CultureInfo.InvariantCulture);
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        using var cmd = Build(c, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read())
        {
            list.Add(map(r));
        }

        return list;
    }

    public void InTransaction(Action<SqliteConnection, SqliteTransaction> work)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        work(c, tx);
        tx.Commit();
    }

    public static SqliteCommand Build(SqliteConnection c, string sql, (string Name, object? Value)[] args, SqliteTransaction? tx = null)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }

    public static string? GetStringOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static long? GetInt64OrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    private void Migrate()
    {
        using var c = Open();
        using (var wal = c.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        long version;
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            version = (long)(cmd.ExecuteScalar() ?? 0L);
        }

        for (var i = (int)version; i < Schema.Migrations.Length; i++)
        {
            using var tx = c.BeginTransaction();
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Schema.Migrations[i];
                cmd.ExecuteNonQuery();
            }

            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"PRAGMA user_version={i + 1};";
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }
}

public static class Clock
{
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);
}
