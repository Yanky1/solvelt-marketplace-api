using System.Text.Json;
using System.Text.RegularExpressions;

namespace Solvelt.Marketplace.Api.Voice.TestSet;

/// <summary>One phrase of the test set: what the speaker is asked to say, and the words it is scored against. The audio is elsewhere (see <see cref="TestSetOptions"/>).</summary>
public sealed record Phrase(string Id, string Language, string Kind, string Text, string Status)
{
    /// <summary>A draft phrase has wording nobody has approved yet (the Yiddish references need a Yiddish speaker): reported apart and left out on request.</summary>
    public bool IsDraft => Status == "draft";
}

public sealed class PhraseList(IReadOnlyList<Phrase> phrases)
{
    public IReadOnlyList<Phrase> Phrases { get; } = phrases;
    public Phrase? Find(string id) => Phrases.FirstOrDefault(p => p.Id == id);

    private static readonly Regex IdPattern = new("^[a-z]{2}-[0-9]{2,3}$", RegexOptions.Compiled);
    private static readonly HashSet<string> Languages = ["en", "yi"], Kinds = ["plain", "shop", "digits", "address"], Statuses = ["approved", "draft"];

    /// <summary>Reads and checks the list; throws <see cref="FormatException"/> naming the first problem (an id, never the wording).</summary>
    public static PhraseList Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException) { throw new FormatException("The phrase list is not valid JSON."); }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("phrases", out var arr) || arr.ValueKind != JsonValueKind.Array) throw new FormatException("The phrase list has no \"phrases\" array.");
            var list = new List<Phrase>();
            foreach (var e in arr.EnumerateArray())
            {
                string Get(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                var p = new Phrase(Get("id"), Get("language"), Get("kind"), Get("text").Trim(), Get("status"));
                if (!IdPattern.IsMatch(p.Id)) throw new FormatException($"A phrase has an invalid id \"{p.Id}\" (like en-01).");
                if (list.Any(x => x.Id == p.Id)) throw new FormatException($"Phrase {p.Id} appears twice.");
                if (!Languages.Contains(p.Language) || !p.Id.StartsWith(p.Language + "-")) throw new FormatException($"Phrase {p.Id}: the language must be en or yi and match the id.");
                if (!Kinds.Contains(p.Kind)) throw new FormatException($"Phrase {p.Id}: the kind must be plain, shop, digits or address.");
                if (!Statuses.Contains(p.Status)) throw new FormatException($"Phrase {p.Id}: the status must be approved or draft.");
                if (ErrorRates.Normalize(p.Text).Length == 0) throw new FormatException($"Phrase {p.Id} has no words to score against.");
                list.Add(p);
            }
            if (list.Count == 0) throw new FormatException("The phrase list is empty.");
            return new PhraseList(list);
        }
    }

    /// <summary>The list that ships with the API (an embedded copy of docs/voice-testset/phrases.json).</summary>
    public static PhraseList Embedded()
    {
        using var s = typeof(PhraseList).Assembly.GetManifestResourceStream("voice-testset-phrases.json") ?? throw new InvalidOperationException("The phrase list is not embedded.");
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }
}

/// <summary>The name of one recording in the container: <c>&lt;set&gt;/&lt;phrase id&gt;-&lt;speaker&gt;.wav</c>. The speaker is a short label the owner chooses (never a real name by rule: a first name or a code).</summary>
public readonly record struct RecordingName(string Set, string PhraseId, string Speaker)
{
    private static readonly Regex Pattern = new("^(?<set>[a-z0-9][a-z0-9-]{0,39})/(?<id>[a-z]{2}-[0-9]{2,3})-(?<speaker>[a-z0-9]{1,20})\\.wav$", RegexOptions.Compiled);
    public override string ToString() => $"{Set}/{PhraseId}-{Speaker}.wav";
    public static bool TryParse(string blobName, out RecordingName name)
    {
        var m = Pattern.Match(blobName);
        name = m.Success ? new RecordingName(m.Groups["set"].Value, m.Groups["id"].Value, m.Groups["speaker"].Value) : default;
        return m.Success;
    }
    public static bool IsValidSpeaker(string? s) => s is { Length: > 0 and <= 20 } && Regex.IsMatch(s, "^[a-z0-9]+$");
}
