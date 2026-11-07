using System.Text;
using Microsoft.Data.Sqlite;

namespace Customers.Core;

/// <summary>Строка результата запроса.</summary>
public sealed class Row
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    internal void Set(string name, object? value) => _values[name] = value;

    public object? this[string name] => _values.GetValueOrDefault(name);

    public string? Str(string name) => _values.GetValueOrDefault(name) is { } v and not DBNull ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;

    public long? Int(string name) => _values.GetValueOrDefault(name) is { } v and not DBNull ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) : null;

    public double? Dbl(string name) => _values.GetValueOrDefault(name) is { } v and not DBNull ? Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture) : null;

    public bool Bool(string name) => (Int(name) ?? 0) != 0;
}

public sealed class DatabaseException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Обёртка над SQLite (SQLCipher): одно соединение, все операции под замком, позиционные параметры «?».
/// С ключом база хранится зашифрованной.
/// </summary>
public sealed class Db : IDisposable
{
    private static readonly object InitLock = new();
    private static bool _initialized;
    private readonly object _gate = new();
    private SqliteConnection? _conn;
    private int _depth;

    private Db(SqliteConnection conn, string path)
    {
        _conn = conn;
        Path = path;
    }

    public string Path { get; }

    public static void Initialize()
    {
        lock (InitLock)
        {
            if (!_initialized)
            {
                SQLitePCL.Batteries_V2.Init();
                _initialized = true;
            }
        }
    }

    /// <summary>Открывает файл. <paramref name="key"/> — ключ SQLCipher; null — без шифрования.</summary>
    public static Db Open(string path, string? key = null, bool readOnly = false)
    {
        Initialize();
        var csb = new SqliteConnectionStringBuilder {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        if (key is not null)
        {
            csb.Password = key;
        }

        var conn = new SqliteConnection(csb.ToString());
        try
        {
            conn.Open();
            // Проверка ключа и файла: на чужом или повреждённом файле здесь будет ошибка.
            using var probe = conn.CreateCommand();
            probe.CommandText = "SELECT count(*) FROM sqlite_master";
            probe.ExecuteScalar();
        }
        catch (SqliteException e)
        {
            conn.Dispose();
            throw new DatabaseException(key is null
                ? "не удалось открыть базу данных: " + e.Message
                : "не удалось открыть базу: неверный ключ шифрования или файл повреждён", e);
        }

        return new Db(conn, path);
    }

    /// <summary>Файл начинается с «SQLite format 3» — значит, он не зашифрован.</summary>
    public static bool IsPlainSqlite(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var header = new byte[16];
            var read = f.Read(header, 0, 16);
            return read == 16 && Encoding.ASCII.GetString(header, 0, 15) == "SQLite format 3";
        }
        catch (IOException)
        {
            return false;
        }
    }

    private SqliteConnection Conn => _conn ?? throw new ObjectDisposedException(nameof(Db));

    private static string Positional(string sql, out int count)
    {
        var sb = new StringBuilder(sql.Length + 8);
        count = 0;
        var quote = '\0';
        foreach (var c in sql)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                sb.Append(c);
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                sb.Append(c);
            }
            else if (c == '?')
            {
                sb.Append("$p").Append(count++);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private SqliteCommand Command(string sql, object?[] args)
    {
        var cmd = Conn.CreateCommand();
        cmd.CommandText = Positional(sql, out var n);
        if (n != args.Length)
        {
            cmd.Dispose();
            throw new ArgumentException($"SQL ждёт {n} параметров, передано {args.Length}");
        }

        for (var i = 0; i < args.Length; i++)
        {
            var v = args[i] switch {
                null => DBNull.Value,
                bool b => b ? 1L : 0L,
                DateTimeOffset d => ISODate.Format(d),
                _ => args[i],
            };
            cmd.Parameters.AddWithValue("$p" + i, v);
        }

        return cmd;
    }

    public void Exec(string sql)
    {
        lock (_gate)
        {
            using var cmd = Conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Выполняет команду, возвращает число изменённых строк.</summary>
    public int Run(string sql, params object?[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            return cmd.ExecuteNonQuery();
        }
    }

    public List<Row> Query(string sql, params object?[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            using var r = cmd.ExecuteReader();
            var rows = new List<Row>();
            while (r.Read())
            {
                var row = new Row();
                for (var i = 0; i < r.FieldCount; i++)
                {
                    row.Set(r.GetName(i), r.IsDBNull(i) ? null : r.GetValue(i));
                }

                rows.Add(row);
            }

            return rows;
        }
    }

    public Row? First(string sql, params object?[] args) => Query(sql, args).FirstOrDefault();

    public object? Scalar(string sql, params object?[] args)
    {
        lock (_gate)
        {
            using var cmd = Command(sql, args);
            var v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }
    }

    public bool TableExists(string name) =>
        Scalar("SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", name) is not null;

    /// <summary>Транзакция. Вложенные вызовы входят в общую; при исключении откатывается всё.</summary>
    public void Transaction(Action body)
    {
        lock (_gate)
        {
            if (_depth > 0)
            {
                body();
                return;
            }

            using var tx = Conn.BeginTransaction();
            _depth++;
            try
            {
                body();
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
            finally
            {
                _depth--;
            }
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _conn?.Dispose();
            _conn = null;
            SqliteConnection.ClearAllPools();
        }
    }

    public void Dispose() => Close();
}
