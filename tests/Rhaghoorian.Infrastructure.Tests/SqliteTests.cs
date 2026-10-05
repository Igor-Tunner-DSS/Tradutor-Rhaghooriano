using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Translation;
using Rhaghoorian.Infrastructure.SQLite;
using Xunit;

namespace Rhaghoorian.Infrastructure.Tests;

public class SqliteDictionaryTests
{
    private static async Task<(TempDb tmp, SqliteDictionaryRepository repo, int count)> Seeded()
    {
        var tmp = new TempDb();
        var repo = new SqliteDictionaryRepository(tmp.Open(), Data.Ling);
        var entries = Data.RealDictionary().Entries;
        await repo.ReplaceAllAsync(entries, 7);
        return (tmp, repo, entries.Count);
    }

    [Fact]
    public async Task ReplaceAll_StoresEveryEntryAndTheSyncRevision()
    {
        var (tmp, repo, count) = await Seeded();
        using var _ = tmp;
        var state = await repo.GetSyncStateAsync();
        Assert.Equal(7, state.Revision);
        Assert.Equal(count, state.LocalCount);
        Assert.NotNull(state.LastSyncAt);
    }

    [Fact]
    public async Task Search_ByPortugueseRhaghoAndAccentInsensitive()
    {
        var (tmp, repo, _) = await Seeded();
        using var _t = tmp;
        Assert.Contains(await repo.SearchAsync(new DictionaryQuery { Text = "eu" }), e => e.Rhagho == "nuruh");
        Assert.Contains(await repo.SearchAsync(new DictionaryQuery { Text = "nuruh" }), e => e.Portuguese == "eu");

        var a = (await repo.SearchAsync(new DictionaryQuery { Text = "voce" })).Select(e => e.Id).ToHashSet();
        var b = (await repo.SearchAsync(new DictionaryQuery { Text = "você" })).Select(e => e.Id).ToHashSet();
        Assert.NotEmpty(a);
        Assert.True(a.SetEquals(b));
        Assert.Contains(await repo.SearchAsync(new DictionaryQuery { Text = "cacar" }), e => e.Portuguese == "caçar");
        Assert.Contains(await repo.SearchAsync(new DictionaryQuery { Text = "ike'hk" }), e => e.Portuguese == "ver");   // apóstrofo reto acha grafia com apóstrofo tipográfico
    }

    [Fact]
    public async Task Search_TreatsPercentAndUnderscoreLiterally()
    {
        var (tmp, repo, _) = await Seeded();
        using var _t = tmp;
        Assert.Empty(await repo.SearchAsync(new DictionaryQuery { Text = "%" }));
        Assert.Empty(await repo.SearchAsync(new DictionaryQuery { Text = "_" }));
    }

    [Fact]
    public async Task AlternativeFormsSynonymsAbbreviationsAndVerbData_RoundTrip()
    {
        using var tmp = new TempDb();
        var repo = new SqliteDictionaryRepository(tmp.Open(), Data.Ling);
        var w = new WordEntry
        {
            Id = "w1", Rhagho = "mor'hk", Portuguese = "amar", PartOfSpeech = "Verbo", Etymology = "de mor", Example = "nuruh mor'hk",
            AlternativeForms = { new AlternativeForm { Text = "morhk", Note = "sem apóstrofo" } },
            Synonyms = { new Synonym { Text = "adorar", AcceptedFromAi = true } },
            Abbreviations = { "am." },
            Verb = new VerbInfo { Infinitive = "amar", Base = "mor'hk", IsInfinitive = true },
            CreatedBy = "u1", UpdatedBy = "u2", Version = 3, RemoteUpdateTime = "2026-01-01T00:00:00.000000Z"
        };
        await repo.UpsertAsync(w);

        var loaded = (await repo.GetAsync("w1"))!;
        Assert.Equal("de mor", loaded.Etymology);
        Assert.Equal("sem apóstrofo", loaded.AlternativeForms[0].Note);
        Assert.True(loaded.Synonyms[0].AcceptedFromAi);
        Assert.Equal("am.", loaded.Abbreviations[0]);
        Assert.True(loaded.Verb!.IsInfinitive);
        Assert.Equal(3, loaded.Version);
        Assert.Equal("2026-01-01T00:00:00.000000Z", loaded.RemoteUpdateTime);

        Assert.Single(await repo.SearchAsync(new DictionaryQuery { Text = "morhk" }));      // forma alternativa pesquisável
        Assert.Single(await repo.FindByRhaghoAsync("morhk"));
        var forms = (await repo.GetGeneratedFormsAsync("w1"))!;
        Assert.Equal("mor'hk'nux", forms.Imperative);

        loaded.AlternativeForms.Clear();
        await repo.UpsertAsync(loaded);                                                       // update remove filhos antigos
        Assert.Empty(await repo.FindByRhaghoAsync("morhk"));
        await repo.DeleteAsync("w1");
        Assert.Null(await repo.GetAsync("w1"));
    }

    [Fact]
    public async Task FilterSortAndPaging_MatchTheContract()
    {
        var (tmp, repo, _) = await Seeded();
        using var _t = tmp;
        var classes = await repo.GetPartsOfSpeechAsync();
        Assert.Contains("Verbo", classes);
        Assert.Contains("Pronome", classes);

        var verbs = await repo.SearchAsync(new DictionaryQuery { PartOfSpeech = "Verbo", Take = 5000 });
        Assert.True(verbs.Count > 10 && verbs.All(v => v.PartOfSpeech == "Verbo"));
        Assert.DoesNotContain(verbs, e => e.Portuguese == "amei");
        Assert.Contains(await repo.SearchAsync(new DictionaryQuery { Text = "amei" }), e => e.Portuguese == "amei");

        var byLen = await repo.SearchAsync(new DictionaryQuery { Sort = DictionarySort.LetterCount, Take = 3000 });
        var lens = byLen.Select(e => Rhaghoorian.Core.Text.TextNormalizer.CountLetters(e.Rhagho)).ToList();
        Assert.Equal(lens.OrderBy(x => x).ToList(), lens);

        var page1 = await repo.SearchAsync(new DictionaryQuery { Take = 10, Skip = 0 });
        var page2 = await repo.SearchAsync(new DictionaryQuery { Take = 10, Skip = 10 });
        Assert.Equal(10, page1.Count);
        Assert.Empty(page1.Select(e => e.Id).Intersect(page2.Select(e => e.Id)));
        Assert.Equal(await repo.CountAsync(new DictionaryQuery { PartOfSpeech = "Verbo" }), (await repo.SearchAsync(new DictionaryQuery { PartOfSpeech = "Verbo", Take = 5000 })).Count);
    }

    [Fact]
    public async Task RealTranslationEngine_WorksOnTopOfTheSqliteCache()
    {
        var (tmp, repo, _) = await Seeded();
        using var _t = tmp;
        var translator = new TranslationService(repo, Data.Ling, new SuggestionService(repo, Data.Ling));
        var r = await translator.TranslateAsync("nuruh hoari'vil naurn ton ido's", TranslationDirection.RhaghoToPortuguese);
        Assert.Equal("Eu irei caçar 5 vezes", r.Text);
        Assert.Equal("fushkarou'sar", (await translator.TranslateAsync("até nunca mais", TranslationDirection.PortugueseToRhagho)).Text);
        Assert.Equal("mor'hk'vil", (await translator.TranslateAsync("amarei", TranslationDirection.PortugueseToRhagho)).Text);
        var unknown = await translator.TranslateAsync("nuruhh", TranslationDirection.RhaghoToPortuguese);
        Assert.Contains(unknown.UnknownWords[0].Suggestions, s => s.Text == "nuruh");
    }

    [Fact]
    public async Task Data_SurvivesClosingAndReopeningTheApp()
    {
        using var tmp = new TempDb();
        var db1 = tmp.Open();
        await new SqliteDictionaryRepository(db1, Data.Ling).ReplaceAllAsync(Data.RealDictionary().Entries, 3);
        await new SqliteSettingsStore(db1).SetAsync("theme", "dark");
        await new SqliteTranslationHistoryRepository(db1).AddAsync(new TranslationHistoryItem { SourceText = "eu", Direction = TranslationDirection.PortugueseToRhagho, ResultText = "nuruh" });

        var db2 = tmp.Open();   // "novo processo"
        Assert.Equal(3, (await new SqliteDictionaryRepository(db2, Data.Ling).GetSyncStateAsync()).Revision);
        Assert.Equal("dark", await new SqliteSettingsStore(db2).GetAsync("theme"));
        var history = await new SqliteTranslationHistoryRepository(db2).ListAsync();
        Assert.Equal("nuruh", Assert.Single(history).ResultText);
    }

    [Fact]
    public async Task History_IsLocalOrderedNewestFirstAndDeletable()
    {
        using var tmp = new TempDb();
        var repo = new SqliteTranslationHistoryRepository(tmp.Open());
        var t0 = DateTimeOffset.UtcNow;
        var a = await repo.AddAsync(new TranslationHistoryItem { SourceText = "a", ResultText = "A", CreatedAt = t0 });
        await repo.AddAsync(new TranslationHistoryItem { SourceText = "b", ResultText = "B", Direction = TranslationDirection.RhaghoToPortuguese, CreatedAt = t0.AddSeconds(1) });
        Assert.True(a.Id > 0);

        var list = await repo.ListAsync();
        Assert.Equal(new[] { "b", "a" }, list.Select(i => i.SourceText).ToArray());
        Assert.Equal(TranslationDirection.RhaghoToPortuguese, list[0].Direction);

        await repo.DeleteAsync(a.Id);
        Assert.Single(await repo.ListAsync());
        await repo.ClearAsync();
        Assert.Empty(await repo.ListAsync());
    }

    [Fact]
    public async Task Settings_Upsert()
    {
        using var tmp = new TempDb();
        var s = new SqliteSettingsStore(tmp.Open());
        Assert.Null(await s.GetAsync("theme"));
        await s.SetAsync("theme", "light");
        await s.SetAsync("theme", "dark");
        Assert.Equal("dark", await s.GetAsync("theme"));
    }

    [Fact]
    public async Task ImportIssues_AreStoredForLaterReview()
    {
        using var tmp = new TempDb();
        var repo = new SqliteImportIssueRepository(tmp.Open());
        var report = Data.RealDictionary();
        await repo.ReplaceAsync(report.Issues);
        var all = await repo.ListAsync();
        Assert.Equal(report.Issues.Count, all.Count);
        var warnings = await repo.ListAsync("Warning");
        Assert.True(warnings.Count > 0 && warnings.All(i => i.Severity == "Warning"));
        Assert.Contains(all, i => i.Code == "DuplicataExata");
    }

    [Fact]
    public async Task CorruptDatabaseFile_IsMovedAsideAndRecreated()
    {
        using var tmp = new TempDb();
        await File.WriteAllTextAsync(tmp.FilePath, "isto não é um banco sqlite, é lixo ".PadRight(4096, 'x'));
        var db = tmp.Open();
        var repo = new SqliteDictionaryRepository(db, Data.Ling);
        Assert.Equal(0, (await repo.GetSyncStateAsync()).LocalCount);
        Assert.Contains(Directory.GetFiles(tmp.Dir), f => f.Contains(".corrupt-"));
        await repo.UpsertAsync(new WordEntry { Id = "x", Rhagho = "a", Portuguese = "b", PartOfSpeech = "Outros" });
        Assert.NotNull(await repo.GetAsync("x"));
    }
}
