using Rhaghoorian.Core.Abstractions;
using Rhaghoorian.Core.Ai;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Importing;
using Rhaghoorian.Core.Linguistics;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Services;
using Rhaghoorian.Core.Translation;

namespace Rhaghoorian.Core.Tests.Support;

public sealed class FakeAuth : IAuthService
{
    public bool IsAuthenticated { get; set; }
    public string? UserId { get; set; } = "user-1";
    public string? Email { get; set; } = "admin@example.com";
    public event EventHandler? AuthStateChanged { add { } remove { } }
    public Task<bool> TryRestoreSessionAsync(CancellationToken ct = default) => Task.FromResult(IsAuthenticated);
    public Task SignInAsync(string email, string password, CancellationToken ct = default) { IsAuthenticated = true; return Task.CompletedTask; }
    public Task SignOutAsync(CancellationToken ct = default) { IsAuthenticated = false; return Task.CompletedTask; }
    public Task<string?> GetIdTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(IsAuthenticated ? "token" : null);
}

public sealed class FakeConnectivity : IConnectivityService
{
    public bool IsOnline { get; set; } = true;
}

/// <summary>Firestore simulado: updateTime e carimbo de servidor (syncStamp) incrementais, pré-condições, tombstones e contagem de leituras.</summary>
public sealed class FakeRemoteStore : IRemoteDictionaryStore
{
    private readonly Dictionary<string, (WordEntry Entry, long Stamp)> _docs = new();
    private readonly Dictionary<string, long> _tombstones = new();
    private long _stamp = 100;
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public long? Revision { get; set; }
    public int FullListCalls { get; private set; }
    public int IncrementalCalls { get; private set; }
    public int WriteCalls { get; private set; }
    /// <summary>Documentos lidos pelo cliente (o que a cota gratuita conta).</summary>
    public int DocumentsRead { get; private set; }
    public DateTimeOffset? LastSince { get; private set; }
    public int ListCalls => FullListCalls;

    private static DateTimeOffset At(long stamp) => Epoch.AddSeconds(stamp);

    public void Seed(IEnumerable<WordEntry> entries)
    {
        foreach (var e in entries) { var c = e.Clone(); c.RemoteUpdateTime = (++_stamp).ToString(); _docs[c.Id] = (c, _stamp); }
        Revision = (Revision ?? 0) + 1;
    }

    /// <summary>Simula o tempo passando no servidor (para testar a margem do cursor).</summary>
    public void AdvanceClock(int seconds) => _stamp += seconds;

    public Task<long?> GetRevisionAsync(CancellationToken ct = default) => Task.FromResult(Revision);

    public Task<RemoteChanges> ListChangesAsync(DateTimeOffset? since, CancellationToken ct = default)
    {
        LastSince = since;
        if (since is null)
        {
            FullListCalls++;
            DocumentsRead += _docs.Count;
            var all = _docs.Values.Select(d => d.Entry.Clone()).ToList();
            var cursor = _docs.Count == 0 ? (DateTimeOffset?)null : At(_docs.Values.Max(d => d.Stamp));
            return Task.FromResult(new RemoteChanges(true, all, Array.Empty<string>(), cursor));
        }

        IncrementalCalls++;
        var changed = _docs.Values.Where(d => At(d.Stamp) > since.Value).ToList();
        var deleted = _tombstones.Where(t => At(t.Value) > since.Value).ToList();
        DocumentsRead += changed.Count + deleted.Count + 2;   // 2 consultas: mínimo de 1 leitura cada, mesmo vazias
        var stamps = changed.Select(d => d.Stamp).Concat(deleted.Select(t => t.Value)).ToList();
        return Task.FromResult(new RemoteChanges(false, changed.Select(d => d.Entry.Clone()).ToList(),
            deleted.Select(t => t.Key).ToList(), stamps.Count == 0 ? null : At(stamps.Max())));
    }

    public Task<WordEntry?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(id, out var d) ? d.Entry.Clone() : null);

    public Task<WordEntry> CreateAsync(WordEntry entry, CancellationToken ct = default)
    {
        if (_docs.ContainsKey(entry.Id)) throw new ConflictException();
        return Task.FromResult(Write(entry));
    }

    public Task<WordEntry> UpdateAsync(WordEntry entry, CancellationToken ct = default)
    {
        if (!_docs.TryGetValue(entry.Id, out var cur) || cur.Entry.RemoteUpdateTime != entry.RemoteUpdateTime) throw new ConflictException();
        return Task.FromResult(Write(entry));
    }

    public Task DeleteAsync(string id, string? expectedUpdateTime, CancellationToken ct = default)
    {
        if (_docs.TryGetValue(id, out var cur) && expectedUpdateTime is not null && cur.Entry.RemoteUpdateTime != expectedUpdateTime) throw new ConflictException();
        _docs.Remove(id);
        _tombstones[id] = ++_stamp;
        WriteCalls++; Revision = (Revision ?? 0) + 1;
        return Task.CompletedTask;
    }

    public Task<int> CreateMissingAsync(IReadOnlyCollection<WordEntry> entries, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var created = 0;
        DocumentsRead += _docs.Count;   // verificação de existência
        foreach (var e in entries) if (!_docs.ContainsKey(e.Id)) { Write(e); created++; }
        progress?.Report(created);
        return Task.FromResult(created);
    }

    private WordEntry Write(WordEntry entry)
    {
        var c = entry.Clone(); c.RemoteUpdateTime = (++_stamp).ToString();
        _docs[c.Id] = (c, _stamp); _tombstones.Remove(c.Id); WriteCalls++; Revision = (Revision ?? 0) + 1;
        return c.Clone();
    }
}

public sealed class FakeSettings : ISettingsStore
{
    private readonly Dictionary<string, string> _data = new();
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(_data.TryGetValue(key, out var v) ? v : null);
    public Task SetAsync(string key, string value, CancellationToken ct = default) { _data[key] = value; return Task.CompletedTask; }
}

public sealed class FakeKeyStore : IAiKeyStore
{
    public string? Key { get; set; }
    public Task<string?> GetAsync(CancellationToken ct = default) => Task.FromResult(Key);
    public Task SetAsync(string key, CancellationToken ct = default) { Key = key; return Task.CompletedTask; }
    public Task ClearAsync(CancellationToken ct = default) { Key = null; return Task.CompletedTask; }
}

public sealed class FakeHistory : ITranslationHistoryRepository
{
    public List<TranslationHistoryItem> Items { get; } = new();
    public Task<TranslationHistoryItem> AddAsync(TranslationHistoryItem item, CancellationToken ct = default) { item.Id = Items.Count + 1; Items.Add(item); return Task.FromResult(item); }
    public Task<IReadOnlyList<TranslationHistoryItem>> ListAsync(int take = 200, int skip = 0, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TranslationHistoryItem>>(Items.AsEnumerable().Reverse().Skip(skip).Take(take).ToList());
    public Task DeleteAsync(long id, CancellationToken ct = default) { Items.RemoveAll(i => i.Id == id); return Task.CompletedTask; }
    public Task ClearAsync(CancellationToken ct = default) { Items.Clear(); return Task.CompletedTask; }
}

public sealed class FakeAi : IAiClient
{
    public int Calls { get; private set; }
    public string TranslateResponse { get; set; } = "Tradução: resposta";
    public AiTranslationRequest? LastRequest { get; private set; }
    public Task<string> TranslateAsync(AiTranslationRequest request, CancellationToken ct = default) { Calls++; LastRequest = request; return Task.FromResult(TranslateResponse); }
    public Task<VerbConjugation> ConjugateAsync(AiConjugationRequest request, CancellationToken ct = default) { Calls++; return Task.FromResult(new VerbConjugation("a", "b", "c", "d")); }
    public Task<IReadOnlyList<string>> SuggestSynonymsAsync(AiSynonymRequest request, CancellationToken ct = default) { Calls++; return Task.FromResult<IReadOnlyList<string>>(new[] { "x" }); }
}

public static class TestKit
{
    public static readonly IRhaghoLinguisticService Ling = new RhaghoLinguisticService();

    public static string DataPath(string file) => Path.Combine(AppContext.BaseDirectory, "TestData", file);

    private static ImportReport? _real;
    public static ImportReport RealDictionary()
    {
        // Entradas são mutáveis; cada chamada devolve um relatório novo para isolar os testes.
        var text = File.ReadAllText(DataPath("dicionario_rhagho_oriano_atual.txt"));
        return _real = new DictionaryFileParser(Ling).Parse(text);
    }

    public static TranslationService TranslatorFor(IEnumerable<WordEntry> entries)
    {
        var lookup = new InMemoryDictionaryLookup(entries);
        return new TranslationService(lookup, Ling, new SuggestionService(lookup, Ling));
    }

    public static TranslationService RealTranslator() => TranslatorFor(RealDictionary().Entries);

    public static WordEntry Word(string pt, string rh, string pos = "Substantivo", int order = 0) =>
        new() { Portuguese = pt, Rhagho = rh, PartOfSpeech = pos, SortOrder = order };

    public static WordEntry Verb(string infinitive, string baseForm, int order = 0) =>
        new()
        {
            Portuguese = infinitive, Rhagho = baseForm, PartOfSpeech = "Verbo", SortOrder = order,
            Verb = new VerbInfo { Infinitive = infinitive, Base = baseForm, IsInfinitive = true }
        };
}

public sealed class FakeImportIssues : IImportIssueRepository
{
    public List<ImportIssue> Stored { get; private set; } = new();
    public Task ReplaceAsync(IReadOnlyCollection<ImportIssue> issues, CancellationToken ct = default) { Stored = issues.ToList(); return Task.CompletedTask; }
    public Task<IReadOnlyList<ImportIssueRecord>> ListAsync(string? severity = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ImportIssueRecord>>(Stored.Where(i => severity is null || i.Severity.ToString() == severity).Select((i, n) => new ImportIssueRecord(n, i.Severity.ToString(), i.Code, i.Line, i.Key, i.Message, DateTimeOffset.UtcNow)).ToList());
}
