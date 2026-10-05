using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Text;
using Rhaghoorian.Core.Tests.Support;
using Xunit;

namespace Rhaghoorian.Core.Tests;

public class NormalizationTests
{
    [Theory]
    [InlineData("musica", "música")]
    [InlineData("Você", "voce")]
    [InlineData("CAÇAR", "cacar")]
    [InlineData("ike’hk", "ike'hk")]
    [InlineData("  até   nunca  mais ", "ate nunca mais")]
    public void Normalize_IgnoresAccentsCaseApostrophesAndSpaces(string a, string b) =>
        Assert.Equal(TextNormalizer.Normalize(b), TextNormalizer.Normalize(a));

    [Fact]
    public void Normalize_DoesNotMutateTheOriginalValue()
    {
        var original = "Música";
        _ = TextNormalizer.Normalize(original);
        Assert.Equal("Música", original);
    }

    [Fact]
    public void CountLetters_IgnoresApostrophesAndAccents()
    {
        Assert.Equal(4, TextNormalizer.CountLetters("ra'ri"));
        Assert.Equal(6, TextNormalizer.CountLetters("música"));
        Assert.Equal(7, TextNormalizer.CountLetters("mor'hk'ta"));
    }

    [Fact]
    public void Tokenizer_KeepsApostropheWordsPunctuationAndNewLines()
    {
        var tokens = Tokenizer.Tokenize("nuruh hoari'vil, ido's!\nfim");
        Assert.Equal(new[] { "nuruh", "hoari'vil", "ido's", "fim" }, tokens.Where(t => t.Kind == TokenKind.Word).Select(t => t.Text).ToArray());
        Assert.Equal(1, tokens.Count(t => t.Kind == TokenKind.NewLine));
        Assert.Equal(string.Concat(tokens.Select(t => t.Text)), "nuruh hoari'vil, ido's!\nfim");
    }
}

public class VerbRuleTests
{
    private readonly Rhaghoorian.Core.Linguistics.IRhaghoLinguisticService _ling = TestKit.Ling;

    [Theory]
    [InlineData(VerbTense.Present, "mor'hk")]
    [InlineData(VerbTense.Past, "mor'hk'ta")]
    [InlineData(VerbTense.Future, "mor'hk'vil")]
    [InlineData(VerbTense.Imperative, "mor'hk'nux")]
    public void Amar_FollowsTheOfficialRules(VerbTense tense, string expected) =>
        Assert.Equal(expected, _ling.Inflect("mor'hk", tense));

    [Fact]
    public void Conjugate_GeneratesAllFourForms()
    {
        var c = _ling.Conjugate("mor'hk");
        Assert.Equal("mor'hk", c.Present);
        Assert.Equal("mor'hk'ta", c.Past);
        Assert.Equal("mor'hk'vil", c.Future);
        Assert.Equal("mor'hk'nux", c.Imperative);
    }

    [Theory]
    [InlineData("hoari'vil", "hoari", VerbTense.Future)]
    [InlineData("naurn'ta", "naurn", VerbTense.Past)]
    [InlineData("mor'hk'nux", "mor'hk", VerbTense.Imperative)]
    [InlineData("ike’hk’ta", "ike'hk", VerbTense.Past)]
    public void StripInflectionSuffix_RecognizesInflectedForms(string form, string expectedBase, VerbTense tense)
    {
        var a = _ling.StripInflectionSuffix(form);
        Assert.NotNull(a);
        Assert.Equal(expectedBase, a!.Base);
        Assert.Equal(tense, a.Tense);
    }

    [Fact]
    public void IsBaseForm_RejectsFormsThatAlreadyCarryASuffix()
    {
        Assert.True(_ling.IsBaseForm("mor'hk"));
        Assert.False(_ling.IsBaseForm("mor'hk'ta"));
    }

    [Fact]
    public void OfficialRuleFile_ExamplesAreReproducedByTheGenerator()
    {
        // Lê o arquivo oficial de regras e confere cada par "português" : "rhagho" contra o gerador.
        var lines = File.ReadAllLines(TestKit.DataPath("regra_flexao_verbal_rhagho_oriano.txt"));
        var pairs = lines.Select(l => System.Text.RegularExpressions.Regex.Match(l, "\"([^\"]+)\"\\s*:\\s*\"([^\"]+)\""))
            .Where(m => m.Success).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        Assert.True(pairs.Count >= 20);
        foreach (var (_, rh) in pairs)
        {
            var analysis = _ling.StripInflectionSuffix(rh);
            var baseForm = analysis?.Base ?? rh;
            Assert.Equal("mor'hk", baseForm);
            Assert.Equal(rh, _ling.Inflect(baseForm, analysis?.Tense ?? VerbTense.Present));
        }
    }

    [Theory]
    [InlineData("amei", "amar", VerbTense.Past)]
    [InlineData("amarei", "amar", VerbTense.Future)]
    [InlineData("ame", "amar", VerbTense.Imperative)]
    [InlineData("comeram", "comer", VerbTense.Past)]
    [InlineData("caçarão", "cacar", VerbTense.Future)]
    public void PortugueseAnalysis_FindsRegularCandidates(string surface, string infinitive, VerbTense tense) =>
        Assert.Contains(_ling.AnalyzePortugueseForm(surface), c => c.Infinitive == infinitive && c.Tense == tense);

    [Theory]
    [InlineData("amar", VerbTense.Past, 0, "amei")]
    [InlineData("caçar", VerbTense.Future, 0, "caçarei")]
    [InlineData("comer", VerbTense.Present, 2, "come")]
    [InlineData("amar", VerbTense.Imperative, null, "ame")]
    public void PortugueseGeneration_ProducesRegularForms(string inf, VerbTense tense, int? slot, string expected) =>
        Assert.Equal(expected, _ling.ConjugatePortuguese(inf, tense, slot));
}

public class ScriptExtensionPointTests
{
    [Fact]
    public void LatinScript_IsTheOnlyAlphabetForNow_AndDoesNotConvertAnything()
    {
        IRhaghoScript script = new LatinRhaghoScript();
        Assert.Equal("mor'hk'ta", script.ToDisplay("mor'hk'ta"));
        Assert.Equal("ike’hk", script.FromDisplay("ike’hk"));   // nem mesmo apóstrofos são alterados
    }
}
