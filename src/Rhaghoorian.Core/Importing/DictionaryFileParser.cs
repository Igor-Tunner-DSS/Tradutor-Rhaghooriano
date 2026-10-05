using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Rhaghoorian.Core.Linguistics;
using Rhaghoorian.Core.Models;
using Rhaghoorian.Core.Text;

namespace Rhaghoorian.Core.Importing;

public interface IDictionaryFileParser
{
    ImportReport Parse(string content);
}

/// <summary>
/// Lê o arquivo "Dicionário rhagho'oriano atual". Princípios: nenhuma entrada é descartada por parecer duplicada,
/// nenhum valor é corrigido silenciosamente (o texto original é preservado) e todo problema vira um item do relatório.
/// Formato: seções "# CLASSE" e linhas  "português" : "rhagho",  (aspas retas ou tipográficas).
/// </summary>
public sealed class DictionaryFileParser : IDictionaryFileParser
{
    private static readonly Regex EntryRegex = new(
        "^[\"\u201C\u201D\u201E]([^\"\u201C\u201D\u201E]*)[\"\u201C\u201D\u201E]\\s*:\\s*[\"\u201C\u201D\u201E]([^\"\u201C\u201D\u201E]*)[\"\u201C\u201D\u201E]\\s*,?\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumericLike = new(@"^[0-9][0-9.,]*$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> KnownClasses = new()
    {
        ["pronomes"] = "Pronome", ["pronome"] = "Pronome",
        ["artigos"] = "Artigo", ["artigo"] = "Artigo",
        ["substantivo"] = "Substantivo", ["substantivos"] = "Substantivo",
        ["numeros"] = "Número", ["numero"] = "Número",
        ["verbos"] = "Verbo", ["verbo"] = "Verbo",
        ["adjetivos"] = "Adjetivo", ["adjetivo"] = "Adjetivo",
        ["adverbios"] = "Advérbio", ["adverbio"] = "Advérbio",
        ["preposicoes"] = "Preposição", ["preposicao"] = "Preposição",
        ["outros"] = "Outros",
        ["girias"] = "Gíria", ["giria"] = "Gíria"
    };

    private readonly IRhaghoLinguisticService _ling;

    public DictionaryFileParser(IRhaghoLinguisticService ling) => _ling = ling;

    public ImportReport Parse(string content)
    {
        var report = new ImportReport();
        var lines = (content ?? string.Empty).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        report.TotalLines = lines.Length;

        var currentClass = "Não classificado";
        var sawHeader = false;
        var sawEntry = false;
        var order = 0;

        for (var idx = 0; idx < lines.Length; idx++)
        {
            var lineNo = idx + 1;
            var trimmed = lines[idx].Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith('#'))
            {
                var header = trimmed.TrimStart('#').Trim();
                var hn = TextNormalizer.Normalize(header);
                sawHeader = true;
                if (KnownClasses.TryGetValue(hn, out var cls)) currentClass = cls;
                else if (hn.StartsWith("todo"))
                    report.Issues.Add(new ImportIssue(IssueSeverity.Warning, "SecaoTodo", lineNo, header,
                        $"Cabeçalho de pendência \"{header}\": a classe gramatical atual ({currentClass}) foi mantida para as entradas seguintes."));
                else
                {
                    currentClass = header;
                    report.Issues.Add(new ImportIssue(IssueSeverity.Warning, "SecaoDesconhecida", lineNo, header,
                        $"Cabeçalho \"{header}\" não é uma classe gramatical conhecida; foi usado literalmente como classe."));
                }
                continue;
            }

            var m = EntryRegex.Match(trimmed);
            if (!m.Success)
            {
                var code = !sawHeader && !sawEntry ? "TituloIgnorado" : "LinhaNaoReconhecida";
                report.Issues.Add(new ImportIssue(code == "TituloIgnorado" ? IssueSeverity.Info : IssueSeverity.Warning,
                    code, lineNo, null, $"Linha não interpretada como entrada: {trimmed}"));
                continue;
            }

            var key = m.Groups[1].Value;
            var value = m.Groups[2].Value;

            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                report.Issues.Add(new ImportIssue(IssueSeverity.Error, "CampoVazio", lineNo, key,
                    "Entrada com chave ou valor vazio; não foi importada."));
                continue;
            }
            sawEntry = true;

            if (trimmed.IndexOfAny(new[] { '\u201C', '\u201D', '\u201E' }) >= 0)
                report.Issues.Add(new ImportIssue(IssueSeverity.Warning, "AspasTipograficas", lineNo, key,
                    "A linha usa aspas tipográficas em vez de aspas retas (valor importado como escrito)."));

            string portuguese = key, rhagho = value;

            if (currentClass == "Número")
            {
                var keyNumeric = NumericLike.IsMatch(key.Trim());
                var valueNumeric = NumericLike.IsMatch(value.Trim()) || value.Contains("tradução simbólica", StringComparison.OrdinalIgnoreCase);
                if (valueNumeric && !keyNumeric)
                {
                    portuguese = value; rhagho = key;
                    report.Issues.Add(new ImportIssue(IssueSeverity.Info, "DirecaoInvertida", lineNo, key,
                        $"Seção NÚMEROS: a linha está escrita como \"rhagho\" : \"português\"; foi lida como {key} = {value}."));
                }
            }

            foreach (var (label, text) in new[] { ("português", portuguese), ("rhagho", rhagho) })
            {
                if (text.Any(c => TextNormalizer.IsApostropheLike(c) && c != '\''))
                    report.Issues.Add(new ImportIssue(IssueSeverity.Warning, "ApostrofoTipografico", lineNo, key,
                        $"O campo {label} contém apóstrofo tipográfico (’) diferente do apóstrofo reto (') — pode quebrar comparações de sufixos."));
                if (text.Any(c => char.IsWhiteSpace(c) && c != ' '))
                    report.Issues.Add(new ImportIssue(IssueSeverity.Warning, "EspacoIncomum", lineNo, key,
                        $"O campo {label} contém espaço não padrão (ex.: espaço sem quebra)."));
                if (text != text.Trim())
                    report.Issues.Add(new ImportIssue(IssueSeverity.Info, "EspacosNasBordas", lineNo, key,
                        $"O campo {label} começa ou termina com espaços."));
            }

            if (TextNormalizer.HasDiacritics(TextNormalizer.UnifyApostrophes(rhagho)))
                report.Issues.Add(new ImportIssue(IssueSeverity.Warning, "CaractereNaoLatinoBasico", lineNo, key,
                    "A grafia Rhagho'oriana contém letras acentuadas."));

            var rhLetters = rhagho.Where(char.IsLetter).ToArray();
            if (rhLetters.Length > 0 && char.IsUpper(rhLetters[0]))
                report.Issues.Add(new ImportIssue(IssueSeverity.Info, "CaixaIncomum", lineNo, key,
                    $"A grafia Rhagho'oriana \"{rhagho}\" começa com maiúscula; a comparação ignora caixa."));

            report.Entries.Add(new WordEntry
            {
                Rhagho = rhagho.Trim(),
                Portuguese = portuguese.Trim(),
                PartOfSpeech = currentClass,
                SortOrder = order++,
                SourceLine = lineNo
            });
        }

        AssignVerbStructure(report);
        AssignStableIds(report);
        DetectDuplicatesAndConflicts(report);
        return report;
    }

    // ------------------------------------------------------------------ verbos

    private sealed class VerbGroup
    {
        public string BaseKey = "";
        public string BaseOriginal = "";
        public VerbTense Tense;
        public List<WordEntry> Entries = new();
    }

    private void AssignVerbStructure(ImportReport report)
    {
        var groups = new List<VerbGroup>();
        VerbGroup? current = null;

        foreach (var e in report.Entries)
        {
            if (TextNormalizer.Normalize(e.PartOfSpeech) != "verbo") { current = null; continue; }

            var analysis = _ling.StripInflectionSuffix(e.Rhagho);
            var baseOriginal = TextNormalizer.UnifyApostrophes(analysis?.Base ?? e.Rhagho.Trim());
            var tense = analysis?.Tense ?? VerbTense.Present;
            var baseKey = TextNormalizer.Normalize(baseOriginal);

            if (current is null || current.BaseKey != baseKey || current.Tense != tense)
            {
                current = new VerbGroup { BaseKey = baseKey, BaseOriginal = baseOriginal, Tense = tense };
                groups.Add(current);
            }
            current.Entries.Add(e);
        }

        var infinitiveByBase = new Dictionary<string, string>();

        foreach (var g in groups.Where(g => g.Tense == VerbTense.Present))
        {
            var first = g.Entries[0];
            if (_ling.LooksLikePortugueseInfinitive(first.Portuguese))
            {
                infinitiveByBase.TryAdd(g.BaseKey, first.Portuguese);
                first.Verb = new VerbInfo { Infinitive = first.Portuguese, Base = g.BaseOriginal, Tense = VerbTense.Present, IsInfinitive = true };
                for (var i = 1; i < g.Entries.Count; i++)
                    g.Entries[i].Verb = new VerbInfo
                    {
                        Infinitive = first.Portuguese, Base = g.BaseOriginal, Tense = VerbTense.Present,
                        PersonSlot = Math.Min(i - 1, 5), IsInfinitive = false
                    };
                if (g.Entries.Count != 1 && g.Entries.Count != 7)
                    report.Issues.Add(new ImportIssue(IssueSeverity.Warning, "FormaVerbalIncompleta", first.SourceLine, first.Portuguese,
                        $"Presente de \"{first.Portuguese}\" ({g.BaseOriginal}) tem {g.Entries.Count} linhas; o padrão é infinitivo + 6 pessoas = 7."));
            }
        }

        foreach (var g in groups)
        {
            var already = g.Entries[0].Verb is not null;
            if (g.Tense == VerbTense.Present && already) continue;

            infinitiveByBase.TryGetValue(g.BaseKey, out var inf);
            var expectedOk = g.Tense switch
            {
                VerbTense.Present or VerbTense.Past or VerbTense.Future => g.Entries.Count == 6,
                VerbTense.Imperative => g.Entries.Count is 3 or 4,
                _ => false
            };

            for (var i = 0; i < g.Entries.Count; i++)
            {
                g.Entries[i].Verb = new VerbInfo
                {
                    Infinitive = inf ?? string.Empty, Base = g.BaseOriginal, Tense = g.Tense, IsInfinitive = false,
                    PersonSlot = g.Tense is VerbTense.Past or VerbTense.Future ? Math.Min(i, 5) : null
                };
            }

            var firstLine = g.Entries[0].SourceLine;
            if (inf is null)
                report.Issues.Add(new ImportIssue(IssueSeverity.Warning, "VerboSemInfinitivo", firstLine, g.Entries[0].Portuguese,
                    $"Formas de {g.Tense} com base \"{g.BaseOriginal}\" não têm um infinitivo correspondente no dicionário."));
            if (!expectedOk)
                report.Issues.Add(new ImportIssue(IssueSeverity.Warning, "FormaVerbalIncompleta", firstLine, g.Entries[0].Portuguese,
                    $"Bloco de {g.Tense} com base \"{g.BaseOriginal}\" tem {g.Entries.Count} linhas (esperado: {(g.Tense == VerbTense.Imperative ? "3 ou 4" : "6")})."));
        }
    }

    // ------------------------------------------------------------------ ids / duplicatas

    private static void AssignStableIds(ImportReport report)
    {
        var seen = new Dictionary<string, int>();
        foreach (var e in report.Entries)
        {
            var basis = $"{TextNormalizer.Normalize(e.PartOfSpeech)}|{TextNormalizer.Normalize(e.Portuguese)}|{TextNormalizer.Normalize(e.Rhagho)}";
            seen[basis] = seen.TryGetValue(basis, out var n) ? n + 1 : 0;
            var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{basis}|{seen[basis]}"));
            e.Id = "imp-" + Convert.ToHexString(hash)[..20].ToLowerInvariant();
        }
    }

    private static void DetectDuplicatesAndConflicts(ImportReport report)
    {
        var entries = report.Entries;

        // Duplicatas exatas
        foreach (var g in entries.GroupBy(e => (TextNormalizer.Normalize(e.PartOfSpeech), TextNormalizer.Normalize(e.Portuguese), TextNormalizer.Normalize(e.Rhagho))).Where(g => g.Count() > 1))
        {
            var lines = string.Join(", ", g.Select(e => e.SourceLine));
            foreach (var e in g.Skip(1))
                report.Issues.Add(new ImportIssue(IssueSeverity.Warning, "DuplicataExata", e.SourceLine, e.Portuguese,
                    $"\"{e.Portuguese}\" : \"{e.Rhagho}\" aparece mais de uma vez (linhas {lines}). Todas foram mantidas."));
        }

        // Mesma chave portuguesa com valores Rhagho diferentes
        foreach (var g in entries.GroupBy(e => TextNormalizer.Normalize(e.Portuguese)))
        {
            var distinct = g.Select(e => TextNormalizer.Normalize(e.Rhagho)).Distinct().ToList();
            if (distinct.Count < 2) continue;

            var allVerbForms = g.All(e => e.Verb is not null);
            var detail = string.Join("; ", g.Select(e => $"linha {e.SourceLine}: {e.Rhagho} ({e.PartOfSpeech})"));
            if (allVerbForms)
                report.Issues.Add(new ImportIssue(IssueSeverity.Info, "ChaveRepetidaVerbal", g.First().SourceLine, g.Key,
                    $"A forma verbal \"{g.Key}\" serve a mais de um tempo (ambiguidade esperada): {detail}."));
            else
                report.Issues.Add(new ImportIssue(IssueSeverity.Warning, "ChaveRepetidaConflitante", g.First().SourceLine, g.Key,
                    $"A chave portuguesa \"{g.Key}\" aparece com valores diferentes: {detail}."));
        }

        // Mesma grafia Rhagho com significados diferentes, fora dos verbos (homônimos/sinônimos legítimos)
        foreach (var g in entries.Where(e => e.Verb is null).GroupBy(e => TextNormalizer.Normalize(e.Rhagho)))
        {
            var meanings = g.Select(e => TextNormalizer.Normalize(e.Portuguese)).Distinct().ToList();
            if (meanings.Count < 2) continue;
            report.Issues.Add(new ImportIssue(IssueSeverity.Info, "RhaghoRepetido", g.First().SourceLine, g.Key,
                $"A grafia \"{g.Key}\" é usada para {meanings.Count} significados: {string.Join(", ", g.Select(e => $"\"{e.Portuguese}\" (linha {e.SourceLine})"))}."));
        }

        // Acentuação mista nas chaves portuguesas
        var accented = entries.Count(e => TextNormalizer.HasDiacritics(e.Portuguese));
        if (accented > 0 && accented < entries.Count)
            report.Issues.Add(new ImportIssue(IssueSeverity.Info, "AcentuacaoMista", null, null,
                $"{accented} entradas portuguesas têm acento e {entries.Count - accented} não têm. A pesquisa e a tradução ignoram acentos; os valores originais foram preservados."));
    }
}
