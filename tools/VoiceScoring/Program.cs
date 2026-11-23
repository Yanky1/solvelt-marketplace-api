using Microsoft.Extensions.Configuration;
using Solvelt.Marketplace.Api.Voice;
using Solvelt.Marketplace.Api.Voice.TestSet;

// The recognition test set (decision V-46): replays recorded phrases through the recognisers of the telephone line and prints word and character error rates.
// ON DEMAND ONLY. It contacts the vendors that the settings name and costs a little money; it is never run by CI. The audio stays in the private Azure Blob container: it is
// read into memory, played to the recogniser and dropped. Reports are text and hold the test phrases and what each recogniser heard, never audio.
//
//   dotnet run -c Release --project tools/VoiceScoring -- check
//   dotnet run -c Release --project tools/VoiceScoring -- list  [--set set1]
//   dotnet run -c Release --project tools/VoiceScoring -- upload --phrase en-02 --speaker a1 --file /path/outside/the/repo/en-02.wav [--set set1]
//   dotnet run -c Release --project tools/VoiceScoring -- score [--set set1] [--matrix matrix.json] [--speakers a1,b2] [--include-draft] [--fast] [--out /dir/outside/the/repo]
//
// Settings: environment variables (Voice__TestSet__StorageAccountUrl, Voice__Deepgram__ApiKey, ...) as in the API, plus --config settings.json (a file OUTSIDE the repository).
// The storage sign-in is the developer's own Azure sign-in (az login) or a managed identity: no key.

return await Cli.RunAsync(args);

static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        var opt = Options.Parse(args);
        if (opt.Command is not ("check" or "list" or "upload" or "score")) { Console.Error.WriteLine(Usage); return 2; }
        if (opt.Error is { } err) { Console.Error.WriteLine(err); return 2; }
        var cb = new ConfigurationBuilder();
        if (opt.Value("config") is { } cfgPath)
        {
            if (GitGuard.IsInsideRepository(cfgPath)) { Console.Error.WriteLine("--config must be a file outside the repository (it can hold keys)."); return 2; }
            cb.AddJsonFile(Path.GetFullPath(cfgPath), optional: false);
        }
        var config = cb.AddEnvironmentVariables().Build();
        var ts = new VoiceOptions().TestSet;
        config.GetSection("Voice:TestSet").Bind(ts);
        if (opt.Value("set") is { } setArg) ts.SetName = setArg;
        PhraseList phrases;
        try
        {
            phrases = opt.Value("phrases") is { } pf ? PhraseList.Parse(await File.ReadAllTextAsync(pf)) : PhraseList.Embedded();
        }
        catch (Exception ex) when (ex is FormatException or IOException) { Console.Error.WriteLine("Phrase list: " + ex.Message); return 2; }

        if (opt.Command == "check") return Check(config, ts, phrases);
        var problems = ts.Problems().Where(p => !p.Contains("MaxRecordSeconds")).ToList();
        if (problems.Count > 0) { foreach (var p in problems) Console.Error.WriteLine(p); return 2; }
        ITestSetStore store = new AzureTestSetStore(ts);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            return opt.Command switch
            {
                "list" => await List(store, ts, phrases, cts.Token),
                "upload" => await Upload(store, ts, phrases, opt, cts.Token),
                _ => await Score(store, config, ts, phrases, opt, cts.Token),
            };
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Stopped."); return 130; }
        catch (Azure.RequestFailedException ex) { Console.Error.WriteLine($"Storage refused the request ({ex.Status} {ex.ErrorCode}). Check the account address, the container name and that your sign-in has the role Storage Blob Data Reader (Contributor to upload)."); return 1; }
        catch (Azure.Identity.CredentialUnavailableException) { Console.Error.WriteLine("No Azure sign-in found: run az login, or run this where a managed identity exists."); return 1; }
    }

    const string Usage = "Usage: VoiceScoring check | list | upload --phrase ID --speaker LABEL --file PATH | score [--matrix FILE] [--speakers a,b] [--include-draft] [--fast] [--out DIR]   (all take --set NAME, --config FILE)";

    static int Check(IConfiguration config, TestSetOptions ts, PhraseList phrases)
    {
        Console.WriteLine($"Phrase list: {phrases.Phrases.Count} phrases ({phrases.Phrases.Count(p => p.Language == "en")} English, {phrases.Phrases.Count(p => p.Language == "yi")} Yiddish, {phrases.Phrases.Count(p => p.IsDraft)} draft).");
        var problems = ts.Problems().Where(p => !p.Contains("MaxRecordSeconds")).ToList();
        Console.WriteLine(problems.Count == 0 ? "Storage settings: ok (nothing was contacted)." : "Storage settings:\n  " + string.Join("\n  ", problems));
        var options = ScoringSetup.ReplayableOptions(config);
        Console.WriteLine(options.Count == 0 ? "No recogniser option has complete settings (Voice:Pipelines:P2..P5 and the vendor keys)." : "Recognisers that can be replayed: " + string.Join(", ", options) + ". (Option 1, OpenAI's speech-to-speech line, and option 6, SignalWire's own recogniser inside the call, cannot take recorded audio.)");
        return problems.Count == 0 ? 0 : 2;
    }

    static async Task<int> List(ITestSetStore store, TestSetOptions ts, PhraseList phrases, CancellationToken ct)
    {
        var byPhrase = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var other = 0;
        await foreach (var blob in store.ListAsync(ts.SetName, ct))
        {
            if (RecordingName.TryParse(blob, out var n)) { if (!byPhrase.TryGetValue(n.PhraseId, out var l)) byPhrase[n.PhraseId] = l = []; l.Add(n.Speaker); } else other++;
        }
        Console.WriteLine($"Set {ts.SetName}: {byPhrase.Sum(x => x.Value.Count)} recordings of {byPhrase.Count} of {phrases.Phrases.Count} phrases{(other > 0 ? $", {other} other file(s)" : "")}.");
        foreach (var p in phrases.Phrases) Console.WriteLine($"  {p.Id}{(p.IsDraft ? " (draft)" : "")}: {(byPhrase.TryGetValue(p.Id, out var sp) ? string.Join(", ", sp) : "-")}");
        return 0;
    }

    static async Task<int> Upload(ITestSetStore store, TestSetOptions ts, PhraseList phrases, Options opt, CancellationToken ct)
    {
        var phraseId = opt.Value("phrase"); var speaker = opt.Value("speaker"); var file = opt.Value("file");
        if (phraseId is null || file is null || !RecordingName.IsValidSpeaker(speaker)) { Console.Error.WriteLine("upload needs --phrase ID, --speaker LABEL (1 to 20 lower-case letters or digits) and --file PATH."); return 2; }
        if (phrases.Find(phraseId) is null) { Console.Error.WriteLine($"No such phrase: {phraseId}."); return 2; }
        if (GitGuard.IsInsideRepository(file)) { Console.Error.WriteLine("The audio file is inside a git repository: audio never goes in the repository. Move it out first (and delete it from the repository if it was added)."); return 2; }
        byte[] wav;
        try { wav = await File.ReadAllBytesAsync(file, ct); _ = WavAudio.ToMulaw8k(wav); }
        catch (Exception ex) when (ex is FormatException or IOException) { Console.Error.WriteLine("The file cannot be used: " + ex.Message); return 2; }
        try { await store.WriteAsync(new RecordingName(ts.SetName, phraseId, speaker!).ToString(), wav, ct); }
        catch (RecordingExistsException ex) { Console.Error.WriteLine(ex.Message); return 1; }
        Console.WriteLine($"Stored {phraseId}-{speaker} in set {ts.SetName}.");
        return 0;
    }

    static async Task<int> Score(ITestSetStore store, IConfiguration config, TestSetOptions ts, PhraseList phrases, Options opt, CancellationToken ct)
    {
        string? outDir = opt.Value("out");
        if (outDir is not null && GitGuard.IsInsideRepository(outDir)) { Console.Error.WriteLine("--out must be a directory outside the repository."); return 2; }
        IReadOnlyList<VariantSpec> specs;
        try { specs = opt.Value("matrix") is { } mf ? ScoringSetup.ParseMatrix(await File.ReadAllTextAsync(mf, ct)) : ScoringSetup.DefaultVariants(config); }
        catch (Exception ex) when (ex is FormatException or IOException) { Console.Error.WriteLine("Matrix: " + ex.Message); return 2; }
        if (specs.Count == 0) { Console.Error.WriteLine("Nothing to score: no recogniser option has complete settings. Run check."); return 2; }
        using var setup = new ScoringSetup();
        var variants = new List<ScoringVariant>();
        try { foreach (var s in specs) variants.Add(setup.Build(config, s)); }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        var speakers = opt.Value("speakers")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var replay = new ReplayOptions(RealTime: !opt.Flag("fast"));
        Console.WriteLine($"Scoring set {ts.SetName} with {variants.Count} variant(s): {string.Join(", ", variants.Select(v => v.Name))}. This contacts those vendors; real time, so about as long as the audio.");
        var run = await TestSetScorer.RunAsync(store, phrases, variants, new ScoringOptions(ts.SetName, speakers, opt.Flag("include-draft"), replay), ct, p => Console.Error.WriteLine("  " + p));
        if (run.Rows.Count == 0) { Console.Error.WriteLine("No recordings matched (see list; draft phrases need --include-draft)."); foreach (var s in run.Skipped) Console.Error.WriteLine("  " + s); return 1; }
        Console.WriteLine();
        Console.WriteLine(ScoreReport.Text(run));
        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            await File.WriteAllTextAsync(Path.Combine(outDir, $"voice-scores-{ts.SetName}-{stamp}.csv"), ScoreReport.Csv(run), ct);
            await File.WriteAllTextAsync(Path.Combine(outDir, $"voice-scores-{ts.SetName}-{stamp}.txt"), ScoreReport.Text(run), ct);
            Console.WriteLine($"Written to {outDir}.");
        }
        return 0;
    }
}

sealed class Options
{
    public string Command = "";
    public string? Error;
    private readonly Dictionary<string, string> _v = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private static readonly HashSet<string> FlagNames = ["include-draft", "fast"];
    public string? Value(string n) => _v.GetValueOrDefault(n);
    public bool Flag(string n) => _flags.Contains(n);

    public static Options Parse(string[] args)
    {
        var o = new Options { Command = args.Length > 0 ? args[0] : "" };
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) { o.Error = $"Unexpected argument {args[i]}."; return o; }
            var name = args[i][2..];
            if (FlagNames.Contains(name)) { o._flags.Add(name); continue; }
            if (i + 1 >= args.Length) { o.Error = $"--{name} needs a value."; return o; }
            o._v[name] = args[++i];
        }
        return o;
    }
}

static class GitGuard
{
    /// <summary>True when the path is inside a git work tree (a folder with .git above it): audio, keys and reports must never be put there.</summary>
    public static bool IsInsideRepository(string path)
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "/"); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return true;
        return false;
    }
}
