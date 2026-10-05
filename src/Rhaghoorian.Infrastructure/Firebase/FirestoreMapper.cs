using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rhaghoorian.Core.Models;

namespace Rhaghoorian.Infrastructure.Firebase;

/// <summary>Conversão WordEntry ⇄ documento Firestore (valores tipados da API REST).</summary>
public static class FirestoreMapper
{
    // ------------------------------------------------------------------ escrita

    public static JsonObject ToFields(WordEntry e)
    {
        var f = new JsonObject
        {
            ["rhagho"] = Str(e.Rhagho),
            ["portuguese"] = Str(e.Portuguese),
            ["partOfSpeech"] = Str(e.PartOfSpeech),
            ["sortOrder"] = Int(e.SortOrder),
            ["version"] = Int(e.Version),
            ["createdAt"] = Ts(e.CreatedAt),
            ["updatedAt"] = Ts(e.UpdatedAt),
            ["alternativeForms"] = Arr(e.AlternativeForms.Select(a => Map(
                ("text", Str(a.Text)), ("note", string.IsNullOrWhiteSpace(a.Note) ? null : Str(a.Note))))),
            ["abbreviations"] = Arr(e.Abbreviations.Select(Str)),
            ["synonyms"] = Arr(e.Synonyms.Select(s => Map(("text", Str(s.Text)), ("acceptedFromAi", Bool(s.AcceptedFromAi)))))
        };
        if (!string.IsNullOrWhiteSpace(e.Etymology)) f["etymology"] = Str(e.Etymology);
        if (!string.IsNullOrWhiteSpace(e.Example)) f["example"] = Str(e.Example);
        if (e.SourceLine is { } line) f["sourceLine"] = Int(line);
        if (!string.IsNullOrWhiteSpace(e.CreatedBy)) f["createdBy"] = Str(e.CreatedBy);
        if (!string.IsNullOrWhiteSpace(e.UpdatedBy)) f["updatedBy"] = Str(e.UpdatedBy);
        if (e.Verb is { } v)
        {
            f["verb"] = Map(
                ("infinitive", Str(v.Infinitive)), ("base", Str(v.Base)), ("tense", Int((int)v.Tense)),
                ("personSlot", v.PersonSlot is { } p ? Int(p) : null), ("isInfinitive", Bool(v.IsInfinitive)));
        }
        return f;
    }

    private static JsonObject Str(string? s) => new() { ["stringValue"] = s ?? string.Empty };
    private static JsonObject Int(long n) => new() { ["integerValue"] = n.ToString(CultureInfo.InvariantCulture) };
    private static JsonObject Bool(bool b) => new() { ["booleanValue"] = b };
    private static JsonObject Ts(DateTimeOffset d) => new() { ["timestampValue"] = d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture) };

    private static JsonObject Arr(IEnumerable<JsonObject> items) =>
        new() { ["arrayValue"] = new JsonObject { ["values"] = new JsonArray(items.Cast<JsonNode?>().ToArray()) } };

    private static JsonObject Map(params (string Key, JsonObject? Value)[] fields)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in fields) if (v is not null) obj[k] = v;
        return new JsonObject { ["mapValue"] = new JsonObject { ["fields"] = obj } };
    }

    // ------------------------------------------------------------------ leitura

    public static string IdFromName(string documentName) => documentName[(documentName.LastIndexOf('/') + 1)..];

    public static WordEntry FromDocument(JsonElement doc)
    {
        var fields = doc.TryGetProperty("fields", out var f) ? f : default;
        var entry = new WordEntry
        {
            Id = IdFromName(doc.GetProperty("name").GetString() ?? string.Empty),
            Rhagho = GetString(fields, "rhagho") ?? string.Empty,
            Portuguese = GetString(fields, "portuguese") ?? string.Empty,
            PartOfSpeech = GetString(fields, "partOfSpeech") ?? string.Empty,
            Etymology = GetString(fields, "etymology"),
            Example = GetString(fields, "example"),
            SortOrder = (int)(GetLong(fields, "sortOrder") ?? 0),
            SourceLine = GetLong(fields, "sourceLine") is { } sl ? (int)sl : null,
            Version = GetLong(fields, "version") ?? 1,
            CreatedBy = GetString(fields, "createdBy"),
            UpdatedBy = GetString(fields, "updatedBy"),
            CreatedAt = GetTimestamp(fields, "createdAt") ?? DateTimeOffset.UtcNow,
            UpdatedAt = GetTimestamp(fields, "updatedAt") ?? DateTimeOffset.UtcNow,
            RemoteUpdateTime = doc.TryGetProperty("updateTime", out var ut) ? ut.GetString() : null
        };

        foreach (var m in ArrayItems(fields, "alternativeForms"))
            entry.AlternativeForms.Add(new AlternativeForm { Text = GetString(MapFields(m), "text") ?? string.Empty, Note = GetString(MapFields(m), "note") });
        foreach (var a in ArrayItems(fields, "abbreviations"))
            if (a.TryGetProperty("stringValue", out var sv)) entry.Abbreviations.Add(sv.GetString() ?? string.Empty);
        foreach (var m in ArrayItems(fields, "synonyms"))
            entry.Synonyms.Add(new Synonym { Text = GetString(MapFields(m), "text") ?? string.Empty, AcceptedFromAi = GetBool(MapFields(m), "acceptedFromAi") });

        if (fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("verb", out var verb) && verb.TryGetProperty("mapValue", out var vm))
        {
            var vf = vm.TryGetProperty("fields", out var vff) ? vff : default;
            entry.Verb = new VerbInfo
            {
                Infinitive = GetString(vf, "infinitive") ?? string.Empty,
                Base = GetString(vf, "base") ?? string.Empty,
                Tense = (VerbTense)(int)(GetLong(vf, "tense") ?? 0),
                PersonSlot = GetLong(vf, "personSlot") is { } ps ? (int)ps : null,
                IsInfinitive = vf.ValueKind == JsonValueKind.Object && vf.TryGetProperty("isInfinitive", out _) ? GetBool(vf, "isInfinitive") : true
            };
        }
        return entry;
    }

    private static JsonElement MapFields(JsonElement mapValueHolder) =>
        mapValueHolder.TryGetProperty("mapValue", out var m) && m.TryGetProperty("fields", out var f) ? f : default;

    private static IEnumerable<JsonElement> ArrayItems(JsonElement fields, string name)
    {
        if (fields.ValueKind != JsonValueKind.Object || !fields.TryGetProperty(name, out var arr)) yield break;
        if (!arr.TryGetProperty("arrayValue", out var av) || !av.TryGetProperty("values", out var values)) yield break;
        foreach (var v in values.EnumerateArray()) yield return v;
    }

    private static JsonElement? Field(JsonElement fields, string name) =>
        fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(name, out var v) ? v : null;

    private static string? GetString(JsonElement fields, string name) =>
        Field(fields, name) is { } v && v.TryGetProperty("stringValue", out var s) ? s.GetString() : null;

    private static long? GetLong(JsonElement fields, string name) =>
        Field(fields, name) is { } v && v.TryGetProperty("integerValue", out var n) && long.TryParse(n.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;

    private static bool GetBool(JsonElement fields, string name) =>
        Field(fields, name) is { } v && v.TryGetProperty("booleanValue", out var b) && b.GetBoolean();

    private static DateTimeOffset? GetTimestamp(JsonElement fields, string name) =>
        Field(fields, name) is { } v && v.TryGetProperty("timestampValue", out var t)
        && DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
}
