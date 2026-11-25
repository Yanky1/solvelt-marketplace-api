using Anthropic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Voice;
using Solvelt.Marketplace.Api.Voice.Evals;

// The understanding evals (decision V-59): about eighty caller utterances with what the text model should do, played through the SAME instructions and the SAME tool definitions as the live line, with
// the model the line uses. ON DEMAND ONLY. It contacts Anthropic and costs a little money; it is never run by CI. The fixtures are test sentences (docs/voice-evals/understanding.json), never callers.
//
//   dotnet run -c Release --project tools/VoiceEvals -- check
//   dotnet run -c Release --project tools/VoiceEvals -- run [--category cart] [--parallel 4] [--out /dir/outside/the/repo] [--config settings.json]
//
// Settings: environment variables (Voice__Anthropic__ApiKey, optionally Voice__Pipelines__Llm__Model) as in the API, plus --config settings.json (a file OUTSIDE the repository: it can hold the key).
// `check` needs no key and no network: it validates the fixtures against the live tool registry. `run` needs Voice:Anthropic:ApiKey and says so when it is missing.

static class Entry
{
    // not top-level statements: those would declare a class named Program, which the API already has (the host below needs the API's)
    public static Task<int> Main(string[] args) => Cli.RunAsync(args);
}

static class Cli
{
    const string Usage = "Usage: VoiceEvals check | run [--category NAME] [--parallel N] [--out DIR] [--config FILE]";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("check" or "run")) { Console.Error.WriteLine(Usage); return 2; }
        string? Value(string name) { var i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var cb = new ConfigurationBuilder();
        if (Value("config") is { } cfg)
        {
            if (IsInsideRepository(cfg)) { Console.Error.WriteLine("--config must be a file outside the repository (it can hold keys)."); return 2; }
            cb.AddJsonFile(Path.GetFullPath(cfg), optional: false);
        }
        var config = cb.AddEnvironmentVariables().Build();
        var fixtures = UnderstandingEvals.Embedded();
        if (Value("category") is { } cat)
        {
            fixtures = fixtures.Where(f => f.Category == cat).ToList();
            if (fixtures.Count == 0) { Console.Error.WriteLine($"No fixtures in category '{cat}'. Categories: {string.Join(", ", UnderstandingEvals.Categories)}."); return 2; }
        }
        var apiKey = config["Voice:Anthropic:ApiKey"];
        if (args[0] == "run" && string.IsNullOrWhiteSpace(apiKey)) { Console.Error.WriteLine("Voice:Anthropic:ApiKey is not set (environment variable Voice__Anthropic__ApiKey, or --config). Nothing was sent."); return 2; }

        var temp = Path.Combine(Path.GetTempPath(), "voice_evals_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var factory = new Host(temp);
            var tools = factory.Services.GetRequiredService<VoiceToolRunner>().Definitions();
            var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
            var unknown = fixtures.SelectMany(f => f.Expect.AnyTool.Append(f.Expect.Tool ?? "")).Where(n => n.Length > 0 && !names.Contains(n)).Distinct().ToList();
            if (unknown.Count > 0) { Console.Error.WriteLine("Fixtures expect tools the line does not have: " + string.Join(", ", unknown)); return 1; }
            Console.WriteLine($"{fixtures.Count} fixtures, {tools.Count} live tools, all expected tools exist.");
            if (args[0] == "check") return 0;

            var model = config["Voice:Pipelines:Llm:Model"] is { Length: > 0 } m ? m.Trim() : AnthropicDefaults.ConversationModel;
            var options = new VoiceOptions();
            config.GetSection("Voice:Pipelines:Llm").Bind(options.Pipelines.Llm);
            var llm = new AnthropicVoiceLlm(new AnthropicClient { ApiKey = apiKey!, BaseUrl = "https://api.anthropic.com", MaxRetries = 2 }, model, options.Pipelines.Llm.MaxTokens, options.Pipelines.Llm.TimeoutSeconds);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var parallel = int.TryParse(Value("parallel"), out var p) ? p : 4;
            Console.WriteLine($"Model {model}; {parallel} at a time.");
            var outcomes = await UnderstandingEvals.RunAsync(llm, tools, fixtures, parallel, cts.Token);
            var report = UnderstandingEvals.Report(outcomes);
            Console.WriteLine(report);
            if (Value("out") is { } dir)
            {
                if (IsInsideRepository(dir)) { Console.Error.WriteLine("--out must be a folder outside the repository."); return 2; }
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, $"understanding-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt"), $"Model {model}\n" + report);
            }
            return outcomes.All(o => o.Passed) ? 0 : 1;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Stopped."); return 130; }
        finally { foreach (var f in new[] { temp, temp + "-shm", temp + "-wal" }) try { File.Delete(f); } catch (IOException) { } }
    }

    static bool IsInsideRepository(string path)
    {
        var full = Path.GetFullPath(path);
        for (var d = new DirectoryInfo(Path.GetDirectoryName(full) ?? "/"); d is not null; d = d.Parent) if (Directory.Exists(Path.Combine(d.FullName, ".git"))) return true;
        return false;
    }
}

/// <summary>The real API, in process, on a throw-away SQLite file: only used to read the live tool definitions (no request is ever sent to it).</summary>
sealed class Host(string sqlitePath) : WebApplicationFactory<Program>
{
    static string ApiFolder()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Solvelt.Marketplace.slnx"))) return Path.Combine(d.FullName, "src", "Solvelt.Marketplace.Api");
        throw new InvalidOperationException("Run this from inside the repository: the folder with Solvelt.Marketplace.slnx was not found above the program.");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(ApiFolder());   // the test-host package finds it from a test assembly's attributes; a command-line tool says where the API is
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Portal:BaseUrl"] = "https://portal.invalid", ["Database:Provider"] = "Sqlite", ["ConnectionStrings:Default"] = $"Data Source={sqlitePath}",
            ["Voice:Enabled"] = "true", ["Voice:OpenAiApiKey"] = "not-used", ["Voice:StreamTokenKey"] = new string('t', 40), ["Voice:PublicBaseUrl"] = "https://voice.invalid",
            ["Voice:OpenAiModel"] = "m", ["Voice:OpenAiModelMini"] = "m", ["SignalWire:SigningKey"] = "not-used",
        }));
    }
}
