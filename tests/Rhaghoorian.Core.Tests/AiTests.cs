using Rhaghoorian.Core.Ai;
using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Services;
using Rhaghoorian.Core.Tests.Support;
using Rhaghoorian.Core.Translation;
using Xunit;

namespace Rhaghoorian.Core.Tests;

public class AiSanitizerTests
{
    [Theory]
    [InlineData("Tradução: Eu irei caçar", "Eu irei caçar")]
    [InlineData("A tradução aproximada é: Eu irei", "Eu irei")]
    [InlineData("```\nEu irei caçar\n```", "Eu irei caçar")]
    [InlineData("<think>raciocínio</think>Eu irei", "Eu irei")]
    [InlineData("\"Eu irei\"", "Eu irei")]
    public void CleanTranslation_StripsLabelsFencesAndReasoning(string raw, string expected)
    {
        Assert.Equal(expected, AiResponseSanitizer.CleanTranslation(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"translation\":\"x\"}")]
    [InlineData("[\"a\",\"b\"]")]
    public void CleanTranslation_RejectsEmptyOrJson(string raw) =>
        Assert.Throws<InvalidAiResponseException>(() => AiResponseSanitizer.CleanTranslation(raw));

    [Fact]
    public void ParseConjugation_AcceptsLabeledOrPlainLines_AndRejectsOtherShapes()
    {
        var c = AiResponseSanitizer.ParseConjugation("presente: mor'hk\npassado: mor'hk'ta\nfuturo: mor'hk'vil\nimperativo: mor'hk'nux");
        Assert.Equal("mor'hk'vil", c.Future);
        var plain = AiResponseSanitizer.ParseConjugation("a\nb\nc\nd");
        Assert.Equal("d", plain.Imperative);
        Assert.Throws<InvalidAiResponseException>(() => AiResponseSanitizer.ParseConjugation("só duas\nlinhas"));
        Assert.Throws<InvalidAiResponseException>(() => AiResponseSanitizer.ParseConjugation("uma explicação longa aqui\nb\nc\nd"));
    }

    [Fact]
    public void ParseSynonyms_ReturnsCleanDistinctList()
    {
        var list = AiResponseSanitizer.ParseSynonyms("- lar\n- moradia\n3. Lar\n- residência, habitação");
        Assert.Equal(new[] { "lar", "moradia", "residência", "habitação" }, list.ToArray());
    }
}

public class AiRulesTests
{
    [Fact]
    public void AiSuggestion_NeverOverridesTheDeterministicVerbRules()
    {
        var wrongAi = new VerbConjugation("mor'hk", "mor'hk-TA", "mor'hk'will", "mor'hk'nox");
        var check = AiOutputValidator.CheckConjugation(TestKit.Ling, "mor'hk", wrongAi);
        Assert.Equal("mor'hk'ta", check.Official.Past);
        Assert.Equal("mor'hk'vil", check.Official.Future);
        Assert.False(check.AiAgrees);

        var rightAi = TestKit.Ling.Conjugate("mor'hk");
        Assert.True(AiOutputValidator.CheckConjugation(TestKit.Ling, "mor'hk", rightAi).AiAgrees);
    }

    [Fact]
    public async Task AiTranslationToRhagho_RejectsInventedVocabulary()
    {
        var lookup = new InMemoryDictionaryLookup(TestKit.RealDictionary().Entries);
        await AiOutputValidator.EnsureOnlyKnownRhaghoWordsAsync("nuruh hoari'vil", lookup, TestKit.Ling);
        var ex = await Assert.ThrowsAsync<InvalidAiResponseException>(() =>
            AiOutputValidator.EnsureOnlyKnownRhaghoWordsAsync("nuruh blorgx", lookup, TestKit.Ling));
        Assert.Contains("blorgx", ex.Message);
    }
}

public class WorkflowTests
{
    private static (TranslationWorkflow wf, FakeAi ai, FakeHistory history, FakeConnectivity net) Build(IEnumerable<WordEntry>? entries = null)
    {
        var all = (entries ?? TestKit.RealDictionary().Entries).ToList();
        var lookup = new InMemoryDictionaryLookup(all);
        var translator = new TranslationService(lookup, TestKit.Ling, new SuggestionService(lookup, TestKit.Ling));
        var ai = new FakeAi(); var history = new FakeHistory(); var net = new FakeConnectivity();
        var wf = new TranslationWorkflow(translator, history, ai, new AiContextBuilder(lookup, TestKit.Ling), lookup, TestKit.Ling, net);
        return (wf, ai, history, net);
    }

    [Fact]
    public async Task LocalTranslation_NeverCallsTheAi_AndIsSavedToLocalHistory()
    {
        var (wf, ai, history, _) = Build();
        var r = await wf.TranslateAsync("nuruh hoari'vil naurn ton ido's", TranslationDirection.RhaghoToPortuguese);
        Assert.Equal("Eu irei caçar 5 vezes", r.Text);
        Assert.Equal(0, ai.Calls);
        var item = Assert.Single(history.Items);
        Assert.Equal("nuruh hoari'vil naurn ton ido's", item.SourceText);
        Assert.Equal(TranslationDirection.RhaghoToPortuguese, item.Direction);
        Assert.Equal("Eu irei caçar 5 vezes", item.ResultText);
    }

    [Fact]
    public async Task RefineWithAi_IsSkippedWhenTheLocalEngineAlreadyResolvedEverything()
    {
        var (wf, ai, _, _) = Build();
        var r = await wf.TranslateAsync("nuruh mor'hk marhf", TranslationDirection.RhaghoToPortuguese);
        var again = await wf.RefineWithAiAsync(r);
        Assert.Equal(0, ai.Calls);
        Assert.Same(r, again);
    }

    [Fact]
    public async Task RefineWithAi_SendsOnlyRelevantEntries_AndCleansTheAnswer()
    {
        var (wf, ai, history, _) = Build();
        ai.TranslateResponse = "Tradução: Eu amo zzzx";
        var local = await wf.TranslateAsync("nuruh mor'hk zzzx", TranslationDirection.RhaghoToPortuguese);
        Assert.True(local.CanBenefitFromAi);

        var refined = await wf.RefineWithAiAsync(local);
        Assert.Equal("Eu amo zzzx", refined.Text);
        Assert.True(refined.UsedAi);
        Assert.Equal(1, ai.Calls);

        var ctx = ai.LastRequest!.Context;
        Assert.True(ctx.Entries.Count < 40);                       // nunca o dicionário inteiro (>800 entradas)
        Assert.Contains(ctx.Entries, e => e.Rhagho == "nuruh");
        Assert.Contains("zzzx", ctx.UnknownWords);
        Assert.Equal(2, history.Items.Count);
    }

    [Fact]
    public async Task RefineWithAi_Offline_FailsClearly()
    {
        var (wf, ai, _, net) = Build();
        var local = await wf.TranslateAsync("zzzx", TranslationDirection.RhaghoToPortuguese);
        net.IsOnline = false;
        await Assert.ThrowsAsync<OfflineException>(() => wf.RefineWithAiAsync(local));
        Assert.Equal(0, ai.Calls);
    }

    [Fact]
    public async Task PortugueseToRhagho_AiAnswerWithInventedWord_IsDiscarded()
    {
        var (wf, ai, _, _) = Build();
        ai.TranslateResponse = "nuruh palavrainventada";
        var local = await wf.TranslateAsync("eu zzzx", TranslationDirection.PortugueseToRhagho);
        await Assert.ThrowsAsync<InvalidAiResponseException>(() => wf.RefineWithAiAsync(local));
    }
}

public class WordAiAssistantTests
{
    private static (WordAiAssistant svc, FakeAuth auth, FakeConnectivity net, FakeAi ai) Build()
    {
        var auth = new FakeAuth { IsAuthenticated = true }; var net = new FakeConnectivity(); var ai = new FakeAi();
        return (new WordAiAssistant(ai, auth, net, TestKit.Ling), auth, net, ai);
    }

    [Fact]
    public async Task SynonymSuggestions_RequireLogin_AndNeverPersistAnything()
    {
        var (svc, auth, _, ai) = Build();
        auth.IsAuthenticated = false;
        await Assert.ThrowsAsync<NotAuthenticatedException>(() => svc.SuggestSynonymsAsync(new WordEntry { Portuguese = "casa" }));
        Assert.Equal(0, ai.Calls);

        auth.IsAuthenticated = true;
        var draft = new WordEntry { Portuguese = "casa", Synonyms = { new Synonym { Text = "X" } } };
        var list = await svc.SuggestSynonymsAsync(draft);       // FakeAi devolve "x", que já existe => filtrado
        Assert.Empty(list);
        Assert.Single(draft.Synonyms);                           // o rascunho não foi alterado
    }

    [Fact]
    public async Task ConjugationCheck_AlwaysReturnsTheOfficialForms_AndFlagsDisagreement()
    {
        var (svc, _, net, _) = Build();
        var check = await svc.CheckConjugationAsync("amar", "mor'hk");   // FakeAi devolve a,b,c,d
        Assert.Equal("mor'hk'vil", check.Official.Future);
        Assert.False(check.AiAgrees);
        await Assert.ThrowsAsync<InvalidInputException>(() => svc.CheckConjugationAsync("amar", "mor'hk'ta"));
        net.IsOnline = false;
        await Assert.ThrowsAsync<OfflineException>(() => svc.CheckConjugationAsync("amar", "mor'hk"));
    }
}

public class HistoryViaWorkflowTests
{
    [Fact]
    public async Task History_CanBeListedDeletedAndCleared_ThroughTheWorkflow()
    {
        var all = TestKit.RealDictionary().Entries.ToList();
        var lookup = new InMemoryDictionaryLookup(all);
        var history = new FakeHistory();
        var wf = new TranslationWorkflow(new TranslationService(lookup, TestKit.Ling, new SuggestionService(lookup, TestKit.Ling)), history,
            new FakeAi(), new AiContextBuilder(lookup, TestKit.Ling), lookup, TestKit.Ling, new FakeConnectivity());
        await wf.TranslateAsync("eu", TranslationDirection.PortugueseToRhagho);
        await wf.TranslateAsync("nuruh", TranslationDirection.RhaghoToPortuguese);
        var items = await wf.GetHistoryAsync();
        Assert.Equal(2, items.Count);
        await wf.DeleteHistoryItemAsync(items[0].Id);
        Assert.Single(await wf.GetHistoryAsync());
        await wf.ClearHistoryAsync();
        Assert.Empty(await wf.GetHistoryAsync());
    }
}

public class PersonalKeyTests
{
    [Theory]
    [InlineData("  sk-or-v1-0123456789abcdef0123456789abcdef  ")]
    [InlineData("\"sk-or-v1-0123456789abcdef0123456789abcdef\"")]
    public void ValidKeys_AreNormalized(string raw) =>
        Assert.Equal("sk-or-v1-0123456789abcdef0123456789abcdef", AiKeyFormat.Normalize(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AIzaSyA-chave-do-firebase-0123456789")]
    [InlineData("sk-or-v1-com espaço no meio 0123456789")]
    [InlineData("sk-or-curta")]
    public void InvalidKeys_AreRejectedWithAFriendlyMessage(string raw) =>
        Assert.Throws<InvalidInputException>(() => AiKeyFormat.Normalize(raw));

    [Fact]
    public void Mask_NeverRevealsMoreThanTheLastFourCharacters()
    {
        var key = "sk-or-v1-0123456789abcdefSECRETXYZ9";
        var masked = AiKeyFormat.Mask(key);
        Assert.Equal("sk-or-…XYZ9", masked);
        Assert.DoesNotContain("SECRET", masked);
    }

    [Fact]
    public async Task DailyLimit_IsEnforcedLocally_PersistsAndResetsAtUtcMidnight()
    {
        var settings = new FakeSettings();
        var clock = new DateTimeOffset(2026, 10, 5, 23, 59, 0, TimeSpan.Zero);
        var limiter = new SettingsAiUsageLimiter(settings, 20, () => clock);
        for (var i = 0; i < 20; i++) Assert.True(await limiter.TryAcquireAsync());
        Assert.False(await limiter.TryAcquireAsync());
        Assert.Equal(new AiUsage(20, 20), await limiter.GetUsageAsync());
        Assert.Equal(0, (await limiter.GetUsageAsync()).Remaining);

        var reopened = new SettingsAiUsageLimiter(settings, 20, () => clock);     // "fechar e reabrir"
        Assert.False(await reopened.TryAcquireAsync());

        clock = clock.AddMinutes(2);                                                // passou da meia-noite UTC
        Assert.True(await reopened.TryAcquireAsync());
        Assert.Equal(1, (await reopened.GetUsageAsync()).UsedToday);
    }

    [Fact]
    public void Prompts_CarryTheOfficialRules_TreatUserTextAsData_AndRejectOversizedInput()
    {
        var req = new AiTranslationRequest("nuruh hoari'vil", TranslationDirection.RhaghoToPortuguese,
            new AiContext(new[] { new AiContextEntry("nuruh", "eu", "Pronome", null) }, new[] { "zzz" }), "Eu irei");
        var msgs = AiPrompts.Translation(req);
        Assert.Equal("system", msgs[0].Role);
        Assert.Contains("SOMENTE", msgs[0].Content);
        Assert.Contains("'ta", msgs[0].Content);
        Assert.Contains("'vil", msgs[0].Content);
        Assert.Contains("'nux", msgs[0].Content);
        Assert.Contains("NUNCA invente", msgs[0].Content);
        Assert.Contains("- nuruh = eu (Pronome)", msgs[1].Content);
        Assert.Contains("<<<\nnuruh hoari'vil\n>>>", msgs[1].Content);
        Assert.Contains("zzz", msgs[1].Content);

        var big = req with { Text = new string('a', AiPrompts.MaxTextChars + 1) };
        Assert.Throws<InvalidInputException>(() => AiPrompts.Translation(big));
        Assert.Contains("quatro linhas", AiPrompts.Conjugation(new AiConjugationRequest("amar", "mor'hk"))[0].Content);
        Assert.Contains("no máximo 8", AiPrompts.Synonyms(new AiSynonymRequest("casa", null, null, new[] { "lar" }))[0].Content);
    }
}
