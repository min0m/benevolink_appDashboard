using System.Data;
using System.Security.Cryptography;
using System.Text;
using BenevoLink.Models;
using MySqlConnector;

namespace BenevoLink.Services;

public sealed class DatabaseCatalog
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, List<ColumnInfo>> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TableInfo> _tables = [];

    public IReadOnlyList<TableInfo> Tables => _tables;
    public IReadOnlyDictionary<string, List<ColumnInfo>> Columns => _columns;
    public bool IsFresh => DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromSeconds(20);

    public async Task EnsureLoadedAsync(XamppMySql db, CancellationToken ct = default)
    {
        if (IsFresh) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (IsFresh) return;
            _tables.Clear();
            _columns.Clear();
            _tables.AddRange(await db.LoadTablesAsync(ct));
            foreach (var table in _tables)
            {
                _columns[table.Name] = await db.LoadColumnsAsync(table.Name, ct);
            }
            _loadedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _lock.Release();
        }
    }

    public bool HasTable(string name) => _tables.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    public string? FindTable(params string[] candidates) => candidates.FirstOrDefault(HasTable);

    public string? FindColumn(string table, params string[] candidates)
    {
        if (!_columns.TryGetValue(table, out var columns)) return null;
        foreach (var candidate in candidates)
        {
            var exact = columns.FirstOrDefault(c => string.Equals(c.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact.Name;
        }
        foreach (var candidate in candidates)
        {
            var loose = Normalize(candidate);
            var match = columns.FirstOrDefault(c => Normalize(c.Name).Contains(loose) || loose.Contains(Normalize(c.Name)));
            if (match is not null) return match.Name;
        }
        return null;
    }

    public IReadOnlyList<string> ColumnNames(string table) => _columns.TryGetValue(table, out var cols) ? cols.Select(c => c.Name).ToList() : [];
    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

public sealed class XamppMySql(IConfiguration configuration, DatabaseCatalog catalog)
{
    public string ConnectionString => configuration.GetConnectionString("BenevoLink")
        ?? "Server=localhost;Port=3306;Database=benevolink;User ID=root;Password=;CharSet=utf8mb4;";

    public string DatabaseName
    {
        get
        {
            var builder = new MySqlConnectionStringBuilder(ConnectionString);
            return string.IsNullOrWhiteSpace(builder.Database) ? "benevolink" : builder.Database;
        }
    }

    public async Task<MySqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public async Task<DatabaseHealth> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            await catalog.EnsureLoadedAsync(this, ct);
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT VERSION();";
            var server = Convert.ToString(await command.ExecuteScalarAsync(ct)) ?? "MySQL/MariaDB";
            return new DatabaseHealth(true, DatabaseName, server, "Connected to your XAMPP benevolink database.", DateTimeOffset.UtcNow, catalog.Tables);
        }
        catch (Exception ex)
        {
            return new DatabaseHealth(false, DatabaseName, "localhost:3306", CleanError(ex.Message), DateTimeOffset.UtcNow, []);
        }
    }

    public async Task<List<TableInfo>> LoadTablesAsync(CancellationToken ct = default)
    {
        var rows = new List<TableInfo>();
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT TABLE_NAME, TABLE_TYPE, COALESCE(TABLE_ROWS,0) AS TABLE_ROWS,
       COALESCE(ENGINE,'VIEW') AS ENGINE,
       COALESCE(ROUND((DATA_LENGTH + INDEX_LENGTH) / 1024, 1), 0) AS SIZE_KB
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_SCHEMA = DATABASE()
ORDER BY TABLE_TYPE, TABLE_NAME;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(0);
            var type = reader.GetString(1).Contains("VIEW", StringComparison.OrdinalIgnoreCase) ? "View" : "Table";
            var rowCount = Convert.ToInt64(reader.GetValue(2));
            var engine = Convert.ToString(reader.GetValue(3)) ?? "InnoDB";
            var size = $"{Convert.ToDecimal(reader.GetValue(4)):N1} KiB";
            rows.Add(new TableInfo(name, type, rowCount, size, engine));
        }
        return rows;
    }

    public async Task<List<ColumnInfo>> LoadColumnsAsync(string table, CancellationToken ct = default)
    {
        var rows = new List<ColumnInfo>();
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_KEY
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table
ORDER BY ORDINAL_POSITION;";
        command.Parameters.AddWithValue("@table", table);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ColumnInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2).Equals("YES", StringComparison.OrdinalIgnoreCase), reader.IsDBNull(3) ? "" : reader.GetString(3)));
        }
        return rows;
    }

    public async Task<List<Dictionary<string, string>>> QueryTableAsync(string table, int limit = 50, string? orderBy = null, CancellationToken ct = default)
    {
        await catalog.EnsureLoadedAsync(this, ct);
        if (!catalog.HasTable(table)) return [];
        var columns = catalog.ColumnNames(table).Take(24).ToList();
        if (columns.Count == 0) return [];
        var sqlColumns = string.Join(",", columns.Select(Quote));
        var orderColumn = orderBy is not null && columns.Any(c => string.Equals(c, orderBy, StringComparison.OrdinalIgnoreCase)) ? Quote(orderBy) : PreferredOrder(columns);
        var sql = $"SELECT {sqlColumns} FROM {Quote(table)}";
        if (!string.IsNullOrWhiteSpace(orderColumn)) sql += $" ORDER BY {orderColumn} DESC";
        sql += " LIMIT @limit";
        return await QueryAsync(sql, new Dictionary<string, object?> { ["@limit"] = Math.Clamp(limit, 1, 500) }, ct);
    }

    public async Task<List<Dictionary<string, string>>> QueryAsync(string sql, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        var rows = new List<Dictionary<string, string>>();
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        }
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i)) ?? "";
                row[reader.GetName(i)] = value;
            }
            rows.Add(row);
        }
        return rows;
    }


    public async Task<int> ExecuteAsync(string sql, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        }
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<object?> ScalarAsync(string sql, Dictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (parameters is not null)
        {
            foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        }
        return await command.ExecuteScalarAsync(ct);
    }

    public async Task<long> CountAsync(string table, CancellationToken ct = default)
    {
        await catalog.EnsureLoadedAsync(this, ct);
        if (!catalog.HasTable(table)) return 0;
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Quote(table)};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public static string Quote(string identifier) => $"`{identifier.Replace("`", "``")}`";
    public static string CleanError(string message) => message.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string PreferredOrder(IReadOnlyList<string> columns)
    {
        var candidate = columns.FirstOrDefault(c => new[] { "created_at", "CreatedAt", "date", "updated_at", "id", "Id" }.Any(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase)));
        return candidate is null ? "" : Quote(candidate);
    }
}

public static class PasswordVerifier
{
    public static bool Verify(string suppliedPassword, string storedValue, string? salt = null)
    {
        if (string.IsNullOrWhiteSpace(storedValue)) return false;
        if (string.Equals(suppliedPassword, storedValue, StringComparison.Ordinal)) return true;

        var normalizedHash = storedValue.StartsWith("$2y$", StringComparison.Ordinal) ? "$2a$" + storedValue[4..] : storedValue;
        if (normalizedHash.StartsWith("$2a$") || normalizedHash.StartsWith("$2b$") || normalizedHash.StartsWith("$2x$"))
        {
            try { if (BCrypt.Net.BCrypt.Verify(suppliedPassword, normalizedHash)) return true; } catch { }
        }

        if (Hex(SHA256.HashData(Encoding.UTF8.GetBytes(suppliedPassword))).Equals(storedValue, StringComparison.OrdinalIgnoreCase)) return true;
        if (Hex(SHA1.HashData(Encoding.UTF8.GetBytes(suppliedPassword))).Equals(storedValue, StringComparison.OrdinalIgnoreCase)) return true;
        if (Hex(MD5.HashData(Encoding.UTF8.GetBytes(suppliedPassword))).Equals(storedValue, StringComparison.OrdinalIgnoreCase)) return true;

        if (!string.IsNullOrWhiteSpace(salt))
        {
            try
            {
                var saltBytes = Convert.FromBase64String(salt);
                var key = Rfc2898DeriveBytes.Pbkdf2(suppliedPassword, saltBytes, 210000, HashAlgorithmName.SHA512, 64);
                if (Convert.ToBase64String(key).Equals(storedValue, StringComparison.Ordinal)) return true;
            }
            catch { }
        }

        return false;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
