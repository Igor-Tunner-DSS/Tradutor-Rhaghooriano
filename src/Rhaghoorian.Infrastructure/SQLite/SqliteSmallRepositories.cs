using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Importing;
using Rhaghoorian.Core.Models;
using static Rhaghoorian.Infrastructure.SQLite.SqliteDatabase;

namespace Rhaghoorian.Infrastructure.SQLite;

/// <summary>Histórico de traduções: somente local, nunca enviado ao Firestore.</summary>
public sealed class SqliteTranslationHistoryRepository : ITranslationHistoryRepository
{
    private const int MaxItems = 1000;
    private readonly SqliteDatabase _db;
    public SqliteTranslationHistoryRepository(SqliteDatabase db) => _db = db;

    public Task<TranslationHistoryItem> AddAsync(TranslationHistoryItem item, CancellationToken ct = default) =>
        _db.RunAsync(async conn =>
        {
            await ExecAsync(conn, null,
                "INSERT INTO translation_history (source_text, direction, result_text, created_at) VALUES (@s, @d, @r, @t)", ct,
                ("@s", item.SourceText), ("@d", (int)item.Direction), ("@r", item.ResultText),
                ("@t", SqliteDictionaryRepository.FormatDate(item.CreatedAt)));
            item.Id = Convert.ToInt64(await ScalarAsync(conn, null, "SELECT last_insert_rowid()", ct) ?? 0L);
            await ExecAsync(conn, null,
                "DELETE FROM translation_history WHERE id NOT IN (SELECT id FROM translation_history ORDER BY created_at DESC, id DESC LIMIT @max)", ct,
                ("@max", MaxItems));
            return item;
        }, ct);

    public Task<IReadOnlyList<TranslationHistoryItem>> ListAsync(int take = 200, int skip = 0, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<TranslationHistoryItem>>(async conn =>
            await QueryAsync(conn,
                "SELECT id, source_text, direction, result_text, created_at FROM translation_history ORDER BY created_at DESC, id DESC LIMIT @take OFFSET @skip",
                r => new TranslationHistoryItem
                {
                    Id = r.GetInt64(0), SourceText = r.GetString(1), Direction = (TranslationDirection)(int)r.GetInt64(2),
                    ResultText = r.GetString(3), CreatedAt = SqliteDictionaryRepository.ParseDate(r.GetString(4))
                }, ct, ("@take", take), ("@skip", skip)), ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) =>
        _db.RunAsync(async conn => { await ExecAsync(conn, null, "DELETE FROM translation_history WHERE id = @id", ct, ("@id", id)); return true; }, ct);

    public Task ClearAsync(CancellationToken ct = default) =>
        _db.RunAsync(async conn => { await ExecAsync(conn, null, "DELETE FROM translation_history", ct); return true; }, ct);
}

/// <summary>Preferências locais (tema, direção da tradução, etc.).</summary>
public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly SqliteDatabase _db;
    public SqliteSettingsStore(SqliteDatabase db) => _db = db;

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        _db.RunAsync(async conn => (await ScalarAsync(conn, null, "SELECT value FROM settings WHERE key = @k", ct, ("@k", key))) as string, ct);

    public Task SetAsync(string key, string value, CancellationToken ct = default) =>
        _db.RunAsync(async conn =>
        {
            await ExecAsync(conn, null,
                "INSERT INTO settings (key, value) VALUES (@k, @v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ct,
                ("@k", key), ("@v", value));
            return true;
        }, ct);
}

/// <summary>Relatório de validação da importação, guardado localmente para revisão posterior.</summary>
public sealed class SqliteImportIssueRepository : IImportIssueRepository
{
    private readonly SqliteDatabase _db;
    public SqliteImportIssueRepository(SqliteDatabase db) => _db = db;

    public Task ReplaceAsync(IReadOnlyCollection<ImportIssue> issues, CancellationToken ct = default) =>
        _db.RunAsync(async conn =>
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await ExecAsync(conn, tx, "DELETE FROM import_issues", ct);
            var now = SqliteDictionaryRepository.FormatDate(DateTimeOffset.UtcNow);
            foreach (var i in issues)
                await ExecAsync(conn, tx,
                    "INSERT INTO import_issues (severity, code, line, key, message, imported_at) VALUES (@s, @c, @l, @k, @m, @t)", ct,
                    ("@s", i.Severity.ToString()), ("@c", i.Code), ("@l", i.Line), ("@k", i.Key), ("@m", i.Message), ("@t", now));
            await tx.CommitAsync(ct);
            return true;
        }, ct);

    public Task<IReadOnlyList<ImportIssueRecord>> ListAsync(string? severity = null, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<ImportIssueRecord>>(async conn =>
            await QueryAsync(conn,
                "SELECT id, severity, code, line, key, message, imported_at FROM import_issues " +
                (severity is null ? "" : "WHERE severity = @sev ") + "ORDER BY id",
                r => new ImportIssueRecord(r.GetInt64(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : (int)r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5),
                    SqliteDictionaryRepository.ParseDate(r.GetString(6))), ct,
                severity is null ? Array.Empty<(string, object?)>() : new (string, object?)[] { ("@sev", severity) }), ct);
}
