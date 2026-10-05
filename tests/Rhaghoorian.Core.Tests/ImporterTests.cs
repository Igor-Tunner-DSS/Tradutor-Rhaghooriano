using Rhaghoorian.Core.Importing;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Text;
using Rhaghoorian.Core.Tests.Support;
using Xunit;

namespace Rhaghoorian.Core.Tests;

public class ImporterTests
{
    [Fact]
    public void NoEntryIsDiscarded_EveryEntryLikeLineBecomesAnEntry()
    {
        var text = File.ReadAllText(TestKit.DataPath("dicionario_rhagho_oriano_atual.txt"));
        var entryLikeLines = text.Split('\n').Count(l => l.Count(c => c is '"' or '“' or '”') == 4 && l.Contains(':'));
        var report = TestKit.RealDictionary();
        Assert.Equal(entryLikeLines, report.Entries.Count);
        Assert.True(report.Entries.Count > 800);
    }

    [Fact]
    public void ClassesComeFromSectionHeaders()
    {
        var byClass = TestKit.RealDictionary().EntriesByClass;
        foreach (var cls in new[] { "Pronome", "Artigo", "Substantivo", "Número", "Verbo", "Adjetivo", "Advérbio", "Preposição", "Outros", "Gíria" })
            Assert.True(byClass.ContainsKey(cls), cls);
    }

    [Fact]
    public void NumberSectionWithInvertedDirectionIsReadCorrectlyAndReported()
    {
        var report = TestKit.RealDictionary();
        var aki = report.Entries.Single(e => e.Rhagho == "Aki");
        Assert.Equal("0", aki.Portuguese);
        Assert.Contains(report.Issues, i => i.Code == "DirecaoInvertida");
        var big = report.Entries.Single(e => e.Portuguese == "1337");
        Assert.Equal("haku’fu’mi’hacho’fu’mai’fu’kon", big.Rhagho); // valor original preservado, sem correção silenciosa
    }

    [Fact]
    public void ValidationReport_ListsTheExpectedProblemKinds()
    {
        var codes = TestKit.RealDictionary().IssuesByCode.Keys;
        foreach (var expected in new[] { "AspasTipograficas", "ApostrofoTipografico", "SecaoTodo", "FormaVerbalIncompleta", "ChaveRepetidaConflitante", "AcentuacaoMista", "DirecaoInvertida" })
            Assert.Contains(expected, codes);
    }

    [Fact]
    public void RepeatedKeyWithDifferentValues_IsReportedNotResolved()
    {
        var report = TestKit.RealDictionary();
        var que = report.Entries.Where(e => TextNormalizer.Normalize(e.Portuguese) == "que").Select(e => e.Rhagho).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "nah", "zuf" }, que);
        Assert.Contains(report.Issues, i => i.Code == "ChaveRepetidaConflitante" && i.Key == "que");
    }

    [Fact]
    public void Verbs_AreGroupedWithInfinitiveTenseAndPerson()
    {
        var report = TestKit.RealDictionary();
        var amar = report.Entries.First(e => e.Portuguese == "amar");
        Assert.True(amar.Verb!.IsInfinitive);
        Assert.Equal("mor'hk", amar.Verb.Base);

        var amei = report.Entries.First(e => e.Portuguese == "amei");
        Assert.Equal(VerbTense.Past, amei.Verb!.Tense);
        Assert.Equal("amar", amei.Verb.Infinitive);
        Assert.Equal(0, amei.Verb.PersonSlot);
        Assert.True(amei.IsInflectedVerbForm);

        // base com apóstrofo tipográfico é unificada na informação verbal; a grafia original da entrada é preservada
        var ver = report.Entries.Single(e => e.Portuguese == "ver");
        Assert.Equal("ike’hk", ver.Rhagho);
        Assert.Equal("ike'hk", ver.Verb!.Base);
    }

    [Fact]
    public void DuplicatedVerbBlockInTheSourceFile_IsKeptAndReported()
    {
        var report = TestKit.RealDictionary();
        Assert.Equal(2, report.Entries.Count(e => e.Portuguese == "amar"));
        Assert.Contains(report.Issues, i => i.Code == "DuplicataExata" && i.Key == "amar");
    }

    [Fact]
    public void TwoLetterInfinitiveIr_IsRecognized_SoItsInflectedBlocksHaveALemma()
    {
        var report = TestKit.RealDictionary();
        var ir = report.Entries.First(e => e.Portuguese == "ir");
        Assert.True(ir.Verb!.IsInfinitive);
        Assert.Equal("hoari", ir.Verb.Base);
        var irei = report.Entries.First(e => e.Portuguese == "irei");
        Assert.Equal("ir", irei.Verb!.Infinitive);
        Assert.Equal(VerbTense.Future, irei.Verb.Tense);
        Assert.DoesNotContain(report.Issues, i => i.Code == "VerboSemInfinitivo" && i.Message.Contains("\"hoari\""));
    }

    [Fact]
    public void ValidationMessages_AreNeverSelfContradictory()
    {
        var report = TestKit.RealDictionary();
        Assert.DoesNotContain(report.Issues, i => i.Message.Contains("tem 6 linhas (esperado: 6)"));
    }

    [Fact]
    public void IncompleteVerbBlocks_AreFlagged()
    {
        var report = TestKit.RealDictionary();
        Assert.Contains(report.Issues, i => i.Code == "FormaVerbalIncompleta" && i.Key == "adorar");
    }

    [Fact]
    public void StableIds_AreDeterministicAndUnique()
    {
        var a = TestKit.RealDictionary();
        var b = TestKit.RealDictionary();
        Assert.Equal(a.Entries.Select(e => e.Id).ToArray(), b.Entries.Select(e => e.Id).ToArray());
        Assert.Equal(a.Entries.Count, a.Entries.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void ExactDuplicates_AreKeptAndReported()
    {
        const string text = "# SUBSTANTIVO\n\"casa\" : \"kaza\",\n\"casa\" : \"kaza\",\n";
        var report = new DictionaryFileParser(TestKit.Ling).Parse(text);
        Assert.Equal(2, report.Entries.Count);
        Assert.Contains(report.Issues, i => i.Code == "DuplicataExata");
        Assert.NotEqual(report.Entries[0].Id, report.Entries[1].Id);
    }

    [Fact]
    public void EmptyValue_IsReportedAsError()
    {
        var report = new DictionaryFileParser(TestKit.Ling).Parse("# OUTROS\n\"x\" : \"\",\n");
        Assert.Empty(report.Entries);
        Assert.Equal(1, report.Count(IssueSeverity.Error));
    }
}
