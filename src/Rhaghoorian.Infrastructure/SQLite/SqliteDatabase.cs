using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Rhaghoorian.Core.Errors;

namespace Rhaghoorian.Infrastructure.SQLite;

/// <summary>
/// Acesso ao arquivo SQLite local: criação do esquema, versão, recuperação de arquivo corrompido e helpers ADO.NET.
/// Todo o código dos repositórios usa apenas System.Data.Common; este é o único ponto que conhece o provedor.
/// </summary>
public sealed class SqliteDatabase
{
    public const int SchemaVersion = 1;

    private readonly string _path;
    private readonly string _connectionString;
    private readonly ILogger<SqliteDatabase> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;

    public SqliteDatabase(string path, ILogger<SqliteDatabase> logger)
    {
        _path = path;
        _logger = logger;
        _connectionString = $"Data Source={path}";
    }

    public string Path => _path;

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        return await OpenRawAsync(ct);
    }

    private async Task<DbConnection> OpenRawAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
            await ExecAsync(conn, null, "PRAGMA foreign_keys = ON;", ct);
            return conn;
        }
        catch (DbException ex)
        {
            conn.Dispose();
            throw new LocalDatabaseException(inner: ex);
        }
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            try { await CreateSchemaAsync(ct); }
            catch (DbException ex) when (File.Exists(_path))
            {
                // O cache é reconstruível a partir do Firestore; o arquivo problemático é preservado ao lado, não apagado.
                var aside = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
                _logger.LogError(ex, "Banco local ilegível; movendo para {Aside} e recriando.", aside);
                SqliteConnection.ClearAllPools();
                File.Move(_path, aside);
                try { await CreateSchemaAsync(ct); }
                catch (DbException ex2) { throw new LocalDatabaseException(inner: ex2); }
            }
            catch (DbException ex) { throw new LocalDatabaseException(inner: ex); }

            _initialized = true;
        }
        finally { _initGate.Release(); }
    }

    private async Task CreateSchemaAsync(CancellationToken ct)
    {
        await using var conn = await OpenRawAsync(ct);
        var current = Convert.ToInt32(await ScalarAsync(conn, null, "PRAGMA user_version;", ct) ?? 0);
        if (current >= SchemaVersion) return;

        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var statement in SqliteSchema.Statements) await ExecAsync(conn, tx, statement, ct);
        await ExecAsync(conn, tx, $"PRAGMA user_version = {SchemaVersion};", ct);
        await tx.CommitAsync(ct);
    }

    // ------------------------------------------------------------------ helpers

    public static DbCommand Command(DbConnection conn, DbTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    public static async Task<int> ExecAsync(DbConnection conn, DbTransaction? tx, string sql, CancellationToken ct, params (string, object?)[] args)
    {
        await using var cmd = Command(conn, tx, sql, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public static async Task<object?> ScalarAsync(DbConnection conn, DbTransaction? tx, string sql, CancellationToken ct, params (string, object?)[] args)
    {
        await using var cmd = Command(conn, tx, sql, args);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is DBNull ? null : value;
    }

    public static async Task<List<T>> QueryAsync<T>(DbConnection conn, string sql, Func<DbDataReader, T> map, CancellationToken ct, params (string, object?)[] args)
    {
        var list = new List<T>();
        await using var cmd = Command(conn, null, sql, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(map(reader));
        return list;
    }

    /// <summary>Executa uma operação de repositório convertendo falhas do provedor em mensagens de usuário.</summary>
    public async Task<T> RunAsync<T>(Func<DbConnection, Task<T>> work, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await OpenAsync(ct);
            return await work(conn);
        }
        catch (LocalDatabaseException) { throw; }
        catch (DbException ex)
        {
            _logger.LogError(ex, "Falha no SQLite local.");
            throw new LocalDatabaseException(inner: ex);
        }
    }
}
