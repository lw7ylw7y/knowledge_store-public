using Microsoft.Data.Sqlite;

namespace BookSearch.Core.Data;

/// <summary>Opens connections to library.db with sqlite-vec loaded, WAL and foreign keys on.</summary>
public static class Database
{
    /// <summary>The sqlite-vec version bundled under native/ (pre-1.0, so pinned).</summary>
    public const string PinnedVecVersion = "v0.1.9";

    public const int EmbeddingDimension = 1024;

    public static SqliteConnection Open(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString());
        conn.Open();
        try
        {
            conn.EnableExtensions(true);
            conn.LoadExtension(VecExtensionPath(), "sqlite3_vec_init");
            Exec(conn, "PRAGMA journal_mode = WAL");
            Exec(conn, "PRAGMA synchronous = NORMAL");
            Exec(conn, "PRAGMA foreign_keys = ON");
        }
        catch
        {
            conn.Dispose();
            throw;
        }
        return conn;
    }

    public static string VecVersion(SqliteConnection conn) => Scalar<string>(conn, "SELECT vec_version()")!;

    /// <summary>Runs <paramref name="work"/> in one transaction; any exception rolls everything back.</summary>
    public static T InTransaction<T>(SqliteConnection conn, Func<SqliteTransaction, T> work)
    {
        using var tx = conn.BeginTransaction();
        var result = work(tx);
        tx.Commit();
        return result;
    }

    public static void InTransaction(SqliteConnection conn, Action<SqliteTransaction> work) =>
        InTransaction<object?>(conn, tx => { work(tx); return null; });

    internal static void Exec(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static T? Scalar<T>(SqliteConnection conn, string sql, SqliteTransaction? tx = null,
        params (string name, object? value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        if (result is null or DBNull) return default;
        return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    internal static byte[] ToBlob(float[] vector)
    {
        if (vector.Length != EmbeddingDimension)
            throw new ArgumentException($"Embedding must have {EmbeddingDimension} dimensions, got {vector.Length}.");
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static string VecExtensionPath()
    {
        var (rid, file) = OperatingSystem.IsWindows() ? ("win-x64", "vec0.dll")
            : OperatingSystem.IsLinux() ? ("linux-x64", "vec0.so")
            : throw new PlatformNotSupportedException("sqlite-vec is only bundled for Windows and Linux x64.");
        var path = Path.Combine(AppContext.BaseDirectory, "native", rid, file);
        if (!File.Exists(path)) throw new FileNotFoundException("sqlite-vec extension not found.", path);
        return path;
    }
}
