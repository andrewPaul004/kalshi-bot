using Microsoft.Data.Sqlite;

namespace KalshiBot.Core.Storage;

/// <summary>
/// Thin SQLite wrapper: one connection, explicit SQL, forward-only migrations.
/// Append-only tables are enforced with triggers so history cannot be rewritten by a bug.
/// </summary>
public sealed class Db : IDisposable
{
    public SqliteConnection Connection { get; }
    public string Path { get; }

    private Db(SqliteConnection connection, string path)
    {
        Connection = connection;
        Path = path;
    }

    /// <param name="maxSchemaVersion">Stop migrating at this version. Tests use it to build an older database and
    /// prove an upgrade; production always opens at the latest version.</param>
    public static Db Open(string path, int maxSchemaVersion = int.MaxValue)
    {
        if (path != ":memory:")
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        conn.Open();
        var db = new Db(conn, path);
        db.Exec("PRAGMA foreign_keys = ON;");
        // Without this, INSERT OR REPLACE deletes the old row without firing the append-only delete triggers.
        db.Exec("PRAGMA recursive_triggers = ON;");
        db.Exec("PRAGMA busy_timeout = 5000;");
        if (path != ":memory:")
        {
            db.Exec("PRAGMA journal_mode = WAL;");
            db.Exec("PRAGMA synchronous = FULL;");
        }
        Schema.Migrate(db, maxSchemaVersion);
        return db;
    }

    public int Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteNonQuery();
    }

    public long Insert(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql + "; SELECT last_insert_rowid();", args);
        return (long)cmd.ExecuteScalar()!;
    }

    public T? Scalar<T>(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        var v = cmd.ExecuteScalar();
        if (v is null || v is DBNull) return default;
        return (T)Convert.ChangeType(v, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public T? QuerySingle<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args) where T : class
        => Query(sql, map, args).FirstOrDefault();

    private SqliteCommand Command(string sql, (string Name, object? Value)[] args)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _ambient; // Microsoft.Data.Sqlite requires commands to name the open transaction.
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private SqliteTransaction? _ambient;

    /// <summary>Runs <paramref name="work"/> atomically. Any exception rolls back every write it made.</summary>
    public T InTransaction<T>(Func<T> work)
    {
        if (_ambient is not null) return work();
        // BEGIN IMMEDIATE: take the write lock up front so concurrent processes serialize their read-check-write
        // sequences (risk reservations depend on this; spec 6.1, AC-06).
        using var tx = Connection.BeginTransaction(deferred: false);
        _ambient = tx;
        try
        {
            var result = work();
            tx.Commit();
            return result;
        }
        finally
        {
            _ambient = null;
        }
    }

    public void InTransaction(Action work) => InTransaction(() => { work(); return 0; });

    public void Dispose() => Connection.Dispose();
}

public static class ReaderExt
{
    public static string? Str(this SqliteDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));
    public static string S(this SqliteDataReader r, string col) => r.GetString(r.GetOrdinal(col));
    public static long L(this SqliteDataReader r, string col) => r.GetInt64(r.GetOrdinal(col));
    public static long? LN(this SqliteDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetInt64(r.GetOrdinal(col));
    public static decimal D(this SqliteDataReader r, string col) => Common.Dec.Parse(r.GetString(r.GetOrdinal(col)));
    public static decimal? DN(this SqliteDataReader r, string col) => Common.Dec.ParseOrNull(r.Str(col));
}
