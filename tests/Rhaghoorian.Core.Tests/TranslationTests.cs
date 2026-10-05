using Rhaghoorian.Core.Errors;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Tests.Support;
using Rhaghoorian.Core.Translation;
using Xunit;

namespace Rhaghoorian.Core.Tests;

public class TranslationTests
{
    private static async Task<string> Pt(string text) =>
        (await TestKit.RealTranslator().TranslateAsync(text, TranslationDirection.PortugueseToRhagho)).Text;

    private static async Task<string> Rh(string text) =>
        (await TestKit.RealTranslator().TranslateAsync(text, TranslationDirection.RhaghoToPortuguese)).Text;

    [Fact]
    public async Task RequiredSentence_IsTranslatedToPortuguese() =>
        Assert.Equal("Eu irei caçar 5 vezes", await Rh("nuruh hoari'vil naurn ton ido's"));

    [Theory]
    [InlineData("eu", "nuruh")]
    [InlineData("voce", "ra'ri")]
    [InlineData("você", "ra'ri")]   // com acento: mesma palavra
    [InlineData("Eu", "Nuruh")]     // caixa da origem é respeitada
    [InlineData("amar", "mor'hk")]
    public async Task PortugueseToRhagho_WordByWord(string input, string expected) =>
        Assert.Equal(expected, await Pt(input));

    [Theory]
    [InlineData("amo", "mor'hk")]
    [InlineData("amei", "mor'hk'ta")]
    [InlineData("amarei", "mor'hk'vil")]
    [InlineData("ame", "mor'hk'nux")]
    public async Task PortugueseVerbForms_FollowTheOfficialRules(string input, string expected) =>
        Assert.Equal(expected, await Pt(input));

    [Fact]
    public async Task Expressions_AreTranslatedAsASingleUnit()
    {
        Assert.Equal("fushkarou'sar", await Pt("até nunca mais"));
        Assert.Equal("fushkarou'sar", await Pt("ate nunca mais"));
        Assert.Equal("nar'hirh", await Pt("por que"));
        var r = await TestKit.RealTranslator().TranslateAsync("eu, até nunca mais!", TranslationDirection.PortugueseToRhagho);
        Assert.Equal("nuruh, fushkarou'sar!", r.Text);
        Assert.Contains(r.Segments, s => s.Kind == SegmentKind.Expression);
    }

    [Fact]
    public async Task PunctuationQuotesAndNewLines_ArePreserved() =>
        Assert.Equal("\"nuruh\", morfh!\nra'ri?", await Pt("\"eu\", ele!\nvocê?"));

    [Fact]
    public async Task DigitsAreMappedThroughTheNumbersSection() =>
        Assert.Equal("ton", await Pt("5"));

    [Fact]
    public async Task RhaghoToPortuguese_UsesSubjectPronounForVerbPerson()
    {
        Assert.Equal("Eu amo ela", await Rh("nuruh mor'hk marhf"));
        Assert.Equal("Eu amei", await Rh("nuruh mor'hk'ta"));
        Assert.Equal("Amar", await Rh("mor'hk"));
    }

    [Fact]
    public async Task InflectedFormNotRegisteredIndividually_IsRecognizedFromTheBase()
    {
        var t = TestKit.TranslatorFor(new[]
        {
            TestKit.Word("eu", "nuruh", "Pronome", 0),
            TestKit.Verb("dançar", "dan'hk", 1)
        });
        Assert.Equal("Eu dançarei", (await t.TranslateAsync("nuruh dan'hk'vil", TranslationDirection.RhaghoToPortuguese)).Text);
        Assert.Equal("Dance", (await t.TranslateAsync("dan'hk'nux", TranslationDirection.RhaghoToPortuguese)).Text);
        Assert.Equal("dan'hk'ta", (await t.TranslateAsync("dancei", TranslationDirection.PortugueseToRhagho)).Text);
        Assert.Equal("dan'hk'vil", (await t.TranslateAsync("dançarão", TranslationDirection.PortugueseToRhagho)).Text);
    }

    [Fact]
    public async Task AmbiguousVerbForm_PicksTheDefaultAndExposesAlternatives()
    {
        var r = await TestKit.RealTranslator().TranslateAsync("amamos", TranslationDirection.PortugueseToRhagho);
        Assert.Equal("mor'hk", r.Text);
        var seg = r.Segments.Single(s => s.Kind == SegmentKind.Word);
        Assert.Contains("mor'hk'ta", seg.Alternatives!);
        Assert.True(r.CanBenefitFromAi);
    }

    [Fact]
    public async Task UnknownWord_IsNotInventedAndComesWithClickableSuggestions()
    {
        var r = await TestKit.RealTranslator().TranslateAsync("nuruhh mor'hk", TranslationDirection.RhaghoToPortuguese);
        Assert.True(r.HasUnknownWords);
        var unknown = Assert.Single(r.UnknownWords);
        Assert.Equal("nuruhh", unknown.Word);
        Assert.Contains(unknown.Suggestions, s => s.Text == "nuruh");
        Assert.Contains("nuruhh", r.Text);                       // mantida como foi digitada
        Assert.Contains(r.Segments, s => s.Kind == SegmentKind.Unknown);
    }

    [Fact]
    public async Task UnknownPortugueseWord_IsReportedWithSimilarWords()
    {
        var r = await TestKit.RealTranslator().TranslateAsync("amarr", TranslationDirection.PortugueseToRhagho);
        Assert.True(r.HasUnknownWords);
        Assert.Contains(r.UnknownWords[0].Suggestions, s => s.Text == "amar");
    }

    [Fact]
    public async Task UnknownVerbBaseWithSuffix_SuggestsKnownBaseKeepingTheSuffix()
    {
        var r = await TestKit.RealTranslator().TranslateAsync("naur'vil", TranslationDirection.RhaghoToPortuguese);
        Assert.Contains(r.UnknownWords[0].Suggestions, s => s.Text == "naurn'vil");
    }

    [Fact]
    public async Task AlternativeForms_ParticipateInRhaghoTranslation()
    {
        var word = TestKit.Word("casa", "kaza", "Substantivo", 0);
        word.AlternativeForms.Add(new AlternativeForm { Text = "kazah" });
        var t = TestKit.TranslatorFor(new[] { word });
        Assert.Equal("Casa", (await t.TranslateAsync("kazah", TranslationDirection.RhaghoToPortuguese)).Text);
    }

    [Fact]
    public async Task EmptyInput_IsRejected() =>
        await Assert.ThrowsAsync<InvalidInputException>(() => TestKit.RealTranslator().TranslateAsync("   ", TranslationDirection.PortugueseToRhagho));
}

public class SuggestionTests
{
    [Fact]
    public void Levenshtein_ComputesEditDistance()
    {
        Assert.Equal(0, SuggestionService.Levenshtein("amar", "amar"));
        Assert.Equal(1, SuggestionService.Levenshtein("amar", "amarr"));
        Assert.Equal(3, SuggestionService.Levenshtein("kitten", "sitting"));
    }

    [Fact]
    public async Task Suggestions_AreOrderedBySimilarity()
    {
        var lookup = new Rhaghoorian.Core.Services.InMemoryDictionaryLookup(new[]
        {
            TestKit.Word("casa", "kaza", order: 0), TestKit.Word("cama", "kama", order: 1), TestKit.Word("barco", "baku", order: 2)
        });
        var svc = new SuggestionService(lookup, TestKit.Ling);
        var list = await svc.SuggestAsync("kaz", TranslationDirection.RhaghoToPortuguese);
        Assert.Equal("kaza", list[0].Text);
        Assert.DoesNotContain(list, s => s.Text == "baku");
    }
}
