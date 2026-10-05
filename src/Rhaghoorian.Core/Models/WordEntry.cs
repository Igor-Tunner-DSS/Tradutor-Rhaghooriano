using Rhaghoorian.Core.Text;

namespace Rhaghoorian.Core.Models;

/// <summary>Forma alternativa de grafia de uma palavra Rhagho'oriana.</summary>
public sealed class AlternativeForm
{
    public string Text { get; set; } = string.Empty;
    public string? Note { get; set; }
}

/// <summary>Sinônimo (em português) associado ao significado do registro.</summary>
public sealed class Synonym
{
    public string Text { get; set; } = string.Empty;
    /// <summary>Verdadeiro quando o sinônimo foi sugerido pela IA e aceito explicitamente por um usuário autenticado.</summary>
    public bool AcceptedFromAi { get; set; }
}

/// <summary>Informação verbal de um registro (infinitivo ou forma flexionada importada).</summary>
public sealed class VerbInfo
{
    /// <summary>Infinitivo português associado.</summary>
    public string Infinitive { get; set; } = string.Empty;
    /// <summary>Forma base em Rhagho'oriano (sem sufixo de flexão).</summary>
    public string Base { get; set; } = string.Empty;
    public VerbTense Tense { get; set; } = VerbTense.Present;
    /// <summary>0=eu 1=tu 2=ele/ela/você 3=nós 4=vós 5=eles/elas. Nulo quando não se aplica.</summary>
    public int? PersonSlot { get; set; }
    /// <summary>Verdadeiro para o registro do infinitivo; falso para formas flexionadas listadas individualmente.</summary>
    public bool IsInfinitive { get; set; } = true;
}

public sealed class WordEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Grafia Rhagho'oriana exatamente como cadastrada.</summary>
    public string Rhagho { get; set; } = string.Empty;
    /// <summary>Significado direto em português exatamente como cadastrado.</summary>
    public string Portuguese { get; set; } = string.Empty;
    public string PartOfSpeech { get; set; } = string.Empty;

    public string? Etymology { get; set; }
    public string? Example { get; set; }
    public List<AlternativeForm> AlternativeForms { get; set; } = new();
    public List<string> Abbreviations { get; set; } = new();
    public List<Synonym> Synonyms { get; set; } = new();
    public VerbInfo? Verb { get; set; }

    /// <summary>Ordem original de importação/criação; usada para escolhas determinísticas entre homógrafos.</summary>
    public int SortOrder { get; set; }
    public int? SourceLine { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public long Version { get; set; } = 1;
    /// <summary>updateTime do Firestore; usado como pré-condição de concorrência otimista.</summary>
    public string? RemoteUpdateTime { get; set; }

    public bool IsVerb => TextNormalizer.Normalize(PartOfSpeech) == "verbo";
    public bool IsInflectedVerbForm => Verb is { IsInfinitive: false };

    public WordEntry Clone()
    {
        return new WordEntry
        {
            Id = Id, Rhagho = Rhagho, Portuguese = Portuguese, PartOfSpeech = PartOfSpeech,
            Etymology = Etymology, Example = Example,
            AlternativeForms = AlternativeForms.Select(a => new AlternativeForm { Text = a.Text, Note = a.Note }).ToList(),
            Abbreviations = new List<string>(Abbreviations),
            Synonyms = Synonyms.Select(s => new Synonym { Text = s.Text, AcceptedFromAi = s.AcceptedFromAi }).ToList(),
            Verb = Verb is null ? null : new VerbInfo
            {
                Infinitive = Verb.Infinitive, Base = Verb.Base, Tense = Verb.Tense,
                PersonSlot = Verb.PersonSlot, IsInfinitive = Verb.IsInfinitive
            },
            SortOrder = SortOrder, SourceLine = SourceLine,
            CreatedAt = CreatedAt, UpdatedAt = UpdatedAt, CreatedBy = CreatedBy, UpdatedBy = UpdatedBy,
            Version = Version, RemoteUpdateTime = RemoteUpdateTime
        };
    }
}
