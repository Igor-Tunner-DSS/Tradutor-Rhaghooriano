using System.Data.Common;
using System.Globalization;
using System.Text;
using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Linguistics;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Services;
using Rhaghoorian.Core.Text;
using static Rhaghoorian.Infrastructure.SQLite.SqliteDatabase;

namespace Rhaghoorian.Infrastructure.SQLite;

/// <summary>Cache local do dicionário (Firestore ↔ SQLite). Pesquisa e tradução rodam inteiramente aqui.</summary>
public sealed class SqliteDictionaryRepository : IDictionaryRepository
{
    private const string WordColumns =
        "w.id, w.rhagho, w.portuguese, w.part_of_speech, w.etymology, w.example, w.sort_order, w.source_line, " +
        "w.created_at, w.updated_at, w.created_by, w.updated_by, w.version, w.remote_update_time, " +
        "w.verb_infinitive, w.verb_base, w.verb_tense, w.verb_person, w.verb_is_infinitive";

    private readonly SqliteDatabase _db;
    private readonly IRhaghoLinguisticService _ling;

    public SqliteDictionaryRepository(SqliteDatabase db, IRhaghoLinguisticService ling)
    {
        _db = db;
        _ling = ling;
    }

    // ------------------------------------------------------------------ leitura

    public Task<IReadOnlyList<WordEntry>> FindByPortugueseAsync(string normalizedKey, CancellationToken ct = default) =>
        _db.RunAsync(conn => LoadAsync(conn,
            $"SELECT {WordColumns} FROM words w WHERE w.pt_key = @k ORDER BY w.sort_order",
            ct, ("@k", normalizedKey)), ct);

    public Task<IReadOnlyList<WordEntry>> FindByRhaghoAsync(string normalizedKey, CancellationToken ct = default) =>
        _db.RunAsync(conn => LoadAsync(conn,
            $"SELECT {WordColumns} FROM words w WHERE w.id IN (SELECT word_id FROM rh_keys WHERE key = @k) ORDER BY w.sort_order",
            ct, ("@k", normalizedKey)), ct);

    public Task<IReadOnlyList<SuggestionCandidate>> GetCandidateKeysAsync(TranslationDirection direction, CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<SuggestionCandidate>>(async conn =>
        {
            var sql = direction == TranslationDirection.PortugueseToRhagho
                ? "SELECT pt_key, portuguese, id, rhagho FROM words"
                : "SELECT k.key, w.rhagho, w.id, w.portuguese FROM rh_keys k JOIN words w ON w.id = k.word_id";
            return await QueryAsync(conn, sql, r => new SuggestionCandidate(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct);
        }, ct);

    public Task<int> GetMaxExpressionWordsAsync(TranslationDirection direction, CancellationToken ct = default) =>
        _db.RunAsync(async conn =>
        {
            var sql = direction == TranslationDirection.PortugueseToRhagho ? "SELECT MAX(pt_words) FROM words" : "SELECT MAX(words) FROM rh_keys";
            var v = await ScalarAsync(conn, null, sql, ct);
            return v is null ? 1 : Math.Max(1, Convert.ToInt32(v));
        }, ct);

    public Task<IReadOnlyList<WordEntry>> SearchAsync(DictionaryQuery query, CancellationToken ct = default) =>
        _db.RunAsync(async conn =>
        {
            var (where, args) = BuildFilter(query);
            var dir = query.Descending ? "DESC" : "ASC";
            var order = query.Sort switch
            {
                DictionarySort.AlphabeticalRhagho => $"w.rh_key {dir}, w.sort_order",
                DictionarySort.LetterCount => $"w.rh_letters {dir}, w.rh_key, w.sort_order",
                _ => $"w.pt_key {dir}, w.sort_order"
            };
            var all = args.Concat(new (string, object?)[] { ("@take", query.Take), ("@skip", query.Skip) }).ToArray();
            return await LoadAsync(conn, $"SELECT {WordColumns} FROM words w {where} ORDER BY {order} LIMIT @take OFFSET @skip", ct, all);
        }, ct);

    public Task<int> CountAsync(DictionaryQuery query, CancellationToken ct = default) =>
        _db.RunAsync(async conn =>
        {
            var (where, args) = BuildFilter(query);
            return Convert.ToInt32(await ScalarAsync(conn, null, $"SELECT COUNT(*) FROM words w {where}", ct, args) ?? 0);
        }, ct);

    private static (string Where, (string, object?)[] Args) BuildFilter(DictionaryQuery q)
    {
        var clauses = new List<string>();
        var args = new List<(string, object?)>();

        var text = TextNormalizer.Normalize(q.Text);
        if (text.Length > 0)
        {
            clauses.Add("(w.pt_key LIKE @q ESCAPE '\\' OR EXISTS (SELECT 1 FROM rh_keys k WHERE k.word_id = w.id AND k.key LIKE @q ESCAPE '\\'))");
            args.Add(("@q", "%" + EscapeLike(text) + "%"));
        }
        else if (!q.IncludeInflectedForms)
        {
            clauses.Add("w.is_inflected = 0");
        }

        var pos = TextNormalizer.Normalize(q.PartOfSpeech);
        if (pos.Length > 0 && pos != "todas")
        {
            clauses.Add("w.pos_key = @pos");
            args.Add(("@pos", pos));
        }
        return (clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses), args.ToArray());
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public Task<WordEntry?> GetAsync(string id, CancellationToken ct = default) =>
        _db.RunAsync(async conn =>
            (await LoadAsync(conn, $"SELECT {WordColumns} FROM words w WHERE w.id = @id", ct, ("@id", id))).FirstOrDefault(), ct);

    public Task<IReadOnlyList<string>> GetPartsOfSpeechAsync(CancellationToken ct = default) =>
        _db.RunAsync<IReadOnlyList<string>>(conn =>
            QueryAsync(conn, "SELECT MIN(part_of_speech) FROM words WHERE part_of_speech <> '' GROUP BY pos_key ORDER BY pos_key", r => r.GetString(0), ct)
                .ContinueWith(t => (IReadOnlyList<string>)t.Result, ct), ct);

    public Task<SyncState> GetSyncStateAsync(CancellationToken ct = default) =>
        _db.RunAsync(async conn =>
        {
            var rows = await QueryAsync(conn, "SELECT revision, last_sync_at, cursor, last_full_sync_at FROM sync_state WHERE id = 1",
                r => (Rev: r.IsDBNull(0) ? (long?)null : r.GetInt64(0),
                      At: r.IsDBNull(1) ? (DateTimeOffset?)null : ParseDate(r.GetString(1)),
                      Cur: r.IsDBNull(2) ? (DateTimeOffset?)null : ParseDate(r.GetString(2)),
                      Full: r.IsDBNull(3) ? (DateTimeOffset?)null : ParseDate(r.GetString(3))), ct);
            var count = Convert.ToInt32(await ScalarAsync(conn, null, "SELECT COUNT(*) FROM words", ct) ?? 0);
            var row = rows.FirstOrDefault();
            return new SyncState(row.Rev, row.At, count, row.Cur, row.Full);
        }, ct);

    public Task<int> GetMaxSortOrderAsync(CancellationToken ct = default) =>
        _db.RunAsync(async conn => Convert.ToInt32(await ScalarAsync(conn, null, "SELECT COALESCE(MAX(sort_order), 0) FROM words", ct) ?? 0), ct);

    public async Task<VerbConjugation?> GetGeneratedFormsAsync(string entryId, CancellationToken ct = default)
    {
        var entry = await GetAsync(entryId, ct);
        return entry is { IsVerb: true, Verb: { IsInfinitive: true, Base.Length: > 0 } } ? _ling.Conjugate(entry.Verb.Base) : null;
    }

    // ------------------------------------------------------------------ escrita

    public Task UpsertAsync(WordEntry entry, CancellationToken ct = default) =>
        _db.RunAsync(async conn =>
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await WriteWordAsync(conn, tx, entry, ct);
            await tx.CommitAsync(ct);
            return true;
        }, ct);

    public Task DeleteAsync(string id, CancellationToken ct = default) =>
        _db.RunAsync(async conn =>
        {
            await ExecAsync(conn, null, "DELETE FROM words WHERE id = @id", ct, ("@id", id));
            return true;
        }, ct);

    public Task ReplaceAllAsync(IReadOnlyCollection<WordEntry> entries, long remoteRevision, CancellationToken ct = default) =>
        _db.RunAsync(async conn =>
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await ExecAsync(conn, tx, "DELETE FROM words", ct);   // cascata limpa tabelas filhas
            foreach (var e in entries) await WriteWordAsync(conn, tx, e, ct);
            await ExecAsync(conn, tx, "UPDATE sync_state SET revision = @r, last_sync_at = @t, cursor = NULL, last_full_sync_at = NULL WHERE id = 1", ct,
                ("@r", remoteRevision), ("@t", FormatDate(DateTimeOffset.UtcNow)));
            await tx.CommitAsync(ct);
            return true;
        }, ct);

    public Task ApplyChangesAsync(RemoteChanges changes, long remoteRevision, CancellationToken ct = default) =>
        _db.RunAsync(async conn =>
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            var now = FormatDate(DateTimeOffset.UtcNow);

            if (changes.IsFull) await ExecAsync(conn, tx, "DELETE FROM words", ct);
            foreach (var id in changes.DeletedIds)                                   // exclusões primeiro
                await ExecAsync(conn, tx, "DELETE FROM words WHERE id = @id", ct, ("@id", id));
            foreach (var e in changes.Upserts) await WriteWordAsync(conn, tx, e, ct);

            // O cursor só avança (nunca recua), mesmo se uma resposta vier vazia.
            await ExecAsync(conn, tx,
                @"UPDATE sync_state SET revision = @r, last_sync_at = @t,
                         cursor = CASE WHEN @c IS NOT NULL AND (cursor IS NULL OR @c > cursor) THEN @c ELSE cursor END,
                         last_full_sync_at = CASE WHEN @full = 1 THEN @t ELSE last_full_sync_at END
                  WHERE id = 1", ct,
                ("@r", remoteRevision), ("@t", now),
                ("@c", changes.Cursor is { } c ? FormatDate(c) : null), ("@full", changes.IsFull ? 1 : 0));
            await tx.CommitAsync(ct);
            return true;
        }, ct);

    private static async Task WriteWordAsync(DbConnection conn, DbTransaction tx, WordEntry e, CancellationToken ct)
    {
        await ExecAsync(conn, tx, "DELETE FROM words WHERE id = @id", ct, ("@id", e.Id));

        var ptKey = DictionaryKeys.Portuguese(e);
        var rhKeys = DictionaryKeys.RhaghoKeysWithAlternatives(e).Distinct().ToList();
        var v = e.Verb;

        await ExecAsync(conn, tx,
            @"INSERT INTO words (id, rhagho, portuguese, part_of_speech, etymology, example, sort_order, source_line,
                                 created_at, updated_at, created_by, updated_by, version, remote_update_time,
                                 pt_key, rh_key, pos_key, rh_letters, pt_words,
                                 verb_infinitive, verb_base, verb_tense, verb_person, verb_is_infinitive, is_inflected)
              VALUES (@id, @rh, @pt, @pos, @ety, @ex, @ord, @line, @cat, @uat, @cby, @uby, @ver, @rut,
                      @ptk, @rhk, @posk, @letters, @ptw, @vinf, @vbase, @vtense, @vperson, @visinf, @infl)", ct,
            ("@id", e.Id), ("@rh", e.Rhagho), ("@pt", e.Portuguese), ("@pos", e.PartOfSpeech),
            ("@ety", e.Etymology), ("@ex", e.Example), ("@ord", e.SortOrder), ("@line", e.SourceLine),
            ("@cat", FormatDate(e.CreatedAt)), ("@uat", FormatDate(e.UpdatedAt)), ("@cby", e.CreatedBy), ("@uby", e.UpdatedBy),
            ("@ver", e.Version), ("@rut", e.RemoteUpdateTime),
            ("@ptk", ptKey), ("@rhk", DictionaryKeys.Rhagho(e)), ("@posk", TextNormalizer.Normalize(e.PartOfSpeech)),
            ("@letters", TextNormalizer.CountLetters(e.Rhagho)), ("@ptw", DictionaryKeys.WordCount(ptKey)),
            ("@vinf", v?.Infinitive), ("@vbase", v?.Base), ("@vtense", v is null ? null : (int)v.Tense),
            ("@vperson", v?.PersonSlot), ("@visinf", v is null ? null : v.IsInfinitive ? 1 : 0),
            ("@infl", e.IsInflectedVerbForm ? 1 : 0));

        foreach (var key in rhKeys)
            await ExecAsync(conn, tx, "INSERT INTO rh_keys (key, word_id, words) VALUES (@k, @id, @w)", ct,
                ("@k", key), ("@id", e.Id), ("@w", DictionaryKeys.WordCount(key)));

        for (var i = 0; i < e.AlternativeForms.Count; i++)
            await ExecAsync(conn, tx, "INSERT INTO alternative_forms (word_id, position, text, note) VALUES (@id, @p, @t, @n)", ct,
                ("@id", e.Id), ("@p", i), ("@t", e.AlternativeForms[i].Text), ("@n", e.AlternativeForms[i].Note));

        for (var i = 0; i < e.Synonyms.Count; i++)
            await ExecAsync(conn, tx, "INSERT INTO synonyms (word_id, position, text, accepted_from_ai) VALUES (@id, @p, @t, @a)", ct,
                ("@id", e.Id), ("@p", i), ("@t", e.Synonyms[i].Text), ("@a", e.Synonyms[i].AcceptedFromAi ? 1 : 0));

        for (var i = 0; i < e.Abbreviations.Count; i++)
            await ExecAsync(conn, tx, "INSERT INTO abbreviations (word_id, position, text) VALUES (@id, @p, @t)", ct,
                ("@id", e.Id), ("@p", i), ("@t", e.Abbreviations[i]));
    }

    // ------------------------------------------------------------------ mapeamento

    private static async Task<IReadOnlyList<WordEntry>> LoadAsync(DbConnection conn, string sql, CancellationToken ct, params (string, object?)[] args)
    {
        var entries = await QueryAsync(conn, sql, ReadWord, ct, args);
        if (entries.Count == 0) return entries;

        var byId = entries.ToDictionary(e => e.Id);
        foreach (var chunk in entries.Select(e => e.Id).Chunk(400))
        {
            var names = chunk.Select((_, i) => "@i" + i).ToArray();
            var inClause = string.Join(",", names);
            var idArgs = chunk.Select((id, i) => ("@i" + i, (object?)id)).ToArray();

            foreach (var (id, text, note) in await QueryAsync(conn,
                         $"SELECT word_id, text, note FROM alternative_forms WHERE word_id IN ({inClause}) ORDER BY word_id, position",
                         r => (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)), ct, idArgs))
                byId[id].AlternativeForms.Add(new AlternativeForm { Text = text, Note = note });

            foreach (var (id, text, ai) in await QueryAsync(conn,
                         $"SELECT word_id, text, accepted_from_ai FROM synonyms WHERE word_id IN ({inClause}) ORDER BY word_id, position",
                         r => (r.GetString(0), r.GetString(1), r.GetInt64(2) != 0), ct, idArgs))
                byId[id].Synonyms.Add(new Synonym { Text = text, AcceptedFromAi = ai });

            foreach (var (id, text) in await QueryAsync(conn,
                         $"SELECT word_id, text FROM abbreviations WHERE word_id IN ({inClause}) ORDER BY word_id, position",
                         r => (r.GetString(0), r.GetString(1)), ct, idArgs))
                byId[id].Abbreviations.Add(text);
        }
        return entries;
    }

    private static WordEntry ReadWord(DbDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        var entry = new WordEntry
        {
            Id = r.GetString(0), Rhagho = r.GetString(1), Portuguese = r.GetString(2), PartOfSpeech = r.GetString(3),
            Etymology = S(4), Example = S(5), SortOrder = (int)r.GetInt64(6),
            SourceLine = r.IsDBNull(7) ? null : (int)r.GetInt64(7),
            CreatedAt = ParseDate(r.GetString(8)), UpdatedAt = ParseDate(r.GetString(9)),
            CreatedBy = S(10), UpdatedBy = S(11), Version = r.GetInt64(12), RemoteUpdateTime = S(13)
        };
        if (!r.IsDBNull(18))
        {
            entry.Verb = new VerbInfo
            {
                Infinitive = S(14) ?? string.Empty, Base = S(15) ?? string.Empty,
                Tense = r.IsDBNull(16) ? VerbTense.Present : (VerbTense)(int)r.GetInt64(16),
                PersonSlot = r.IsDBNull(17) ? null : (int)r.GetInt64(17),
                IsInfinitive = r.GetInt64(18) != 0
            };
        }
        return entry;
    }

    internal static string FormatDate(DateTimeOffset d) => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    internal static DateTimeOffset ParseDate(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
