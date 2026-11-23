using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Staff;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// The staff readiness view of the voice line (decision V-51): what is available, unavailable or off, and the NAMES of the settings that are missing or invalid (never a value), for each pipeline option, the
/// phone payments (Sola and its card connector), the diagnostics and the test set. Built from the SAME evaluations start-up uses (<see cref="VoicePipelineVendors.Evaluate"/>, <see cref="VoiceConfig.Problems"/>,
/// <see cref="SolaOptions.Problems"/>, <see cref="Voice.TestSet.TestSetOptions.Problems"/>), so the view and the start-up warnings cannot disagree. Pure: the one network call (the connector probe) is made by the caller.
/// </summary>
public static class VoiceReadiness
{
    public const string Available = "available", Unavailable = "unavailable", Off = "off";

    public sealed record Section(string State, IReadOnlyList<string> Missing, string? Note = null);
    public sealed record OptionView(string Option, string State, IReadOnlyList<string> Missing, string? Note);
    public sealed record Report(bool VoiceEnabled, IReadOnlyList<OptionView> Pipelines, PaymentsView PhonePayments, Section Diagnostics, Section TestSet);
    public sealed record ConnectorView(bool Configured, bool? Reachable, int? Status, IReadOnlyList<string> Missing);
    public sealed record PaymentsView(string Provider, string State, IReadOnlyList<string> Missing, string SolaMode, ConnectorView Connector);

    /// <summary>The names of settings in a start-up problem sentence (<c>Voice:...</c>, <c>Sola:...</c>, <c>SignalWire:...</c>, <c>Connector:...</c>): never anything else of the sentence.</summary>
    private static readonly Regex SettingName = new(@"\b(?:Voice|Sola|SignalWire|Connector)(?::[A-Za-z0-9]+)+", RegexOptions.Compiled);
    public static IReadOnlyList<string> Names(IEnumerable<string> problems) => problems.SelectMany(p => SettingName.Matches(p).Select(m => m.Value)).Distinct().Order().ToList();

    /// <summary>The origin of the connector's health page, from the connector URL the API is given: scheme, host and port only (never the path, the user or the query).</summary>
    public static Uri? ConnectorHealthUrl(SolaOptions sola)
        => Uri.TryCreate(sola.PayConnectorUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? new UriBuilder(Uri.UriSchemeHttps, u.Host, u.IsDefaultPort ? -1 : u.Port, "/health").Uri : null;

    public static Report Build(VoiceOptions v, SignalWireOptions sw, SolaOptions sola, bool devLike, (bool Reachable, int? Status)? connectorProbe)
    {
        var startup = VoiceConfig.Problems(v, sw, devLike, sola);   // empty when the module is off
        var pipelines = new List<OptionView>();
        foreach (var o in VoicePipelineIds.All) pipelines.Add(PipelineView(v, o));

        // phone payments
        var provider = v.PhonePaymentProvider;
        var payMissing = Names(startup.Where(p => p.Contains("Sola:") || p.Contains("PhonePaymentProvider") || p.Contains("Connector:")));
        var keypad = sola.KeypadEnabled;
        var connector = new ConnectorView(keypad, keypad ? connectorProbe?.Reachable : null, keypad ? connectorProbe?.Status : null, keypad ? Names(startup.Where(p => p.Contains("PayConnector") || p.Contains("SavedCardKey"))) : []);
        var payState = !v.Enabled || provider == "none" ? Off : payMissing.Count > 0 ? Unavailable : Available;
        var payments = new PaymentsView(provider, payState, payMissing, sola.ModeText, connector);

        // diagnostics: only meaningful with the pipeline menu (V-39)
        var diagMissing = Names(startup.Where(p => p.Contains("Voice:Diagnostics")));
        var diagnostics = !v.Enabled || !v.PipelineMenu.Enabled || !v.Diagnostics.Enabled ? new Section(Off, [], !v.PipelineMenu.Enabled ? "the pipeline menu is off (Voice:PipelineMenu:Enabled)" : null)
            : new Section(diagMissing.Count > 0 ? Unavailable : Available, diagMissing);

        // test set
        var tsMissing = Names(startup.Where(p => p.Contains("Voice:TestSet")));
        var testSet = !v.Enabled || !v.TestSet.Enabled ? new Section(Off, []) : new Section(tsMissing.Count > 0 ? Unavailable : Available, tsMissing);

        return new Report(v.Enabled, pipelines, payments, diagnostics, testSet);
    }

    private static OptionView PipelineView(VoiceOptions v, string o)
    {
        if (!v.Enabled) return new(o, Off, [], "Voice:Enabled is false");
        if (o == "p1") return string.IsNullOrWhiteSpace(v.OpenAiApiKey) ? new(o, Unavailable, ["Voice:OpenAiApiKey"], null) : new(o, Available, [], "the OpenAI Realtime line (the default)");
        if (!v.PipelineMenu.Enabled) return new(o, Off, [], "the pipeline menu is off (Voice:PipelineMenu:Enabled)");
        var e = VoicePipelineVendors.Evaluate(v, o);
        if (!e.Configured) return new(o, Off, [], o == "p6" ? "Voice:Pipelines:P6:Enabled is false" : "no providers named for this option");
        if (!e.Supported) return new(o, Unavailable, [], "the named providers have no adapter for this option");
        return e.Missing.Count == 0 ? new(o, Available, [], null) : new(o, Unavailable, e.Missing, null);
    }
}

public static class VoiceReadinessEndpoint
{
    public static IEndpointRouteBuilder MapStaffVoiceReadiness(this IEndpointRouteBuilder app)
    {
        app.StaffGroup().MapGet("/voice/readiness", View).StaffCan(StaffPermissions.VoiceView);
        return app;
    }

    private static async Task<IResult> View(HttpContext ctx, IOptions<VoiceOptions> voice, IOptions<SignalWireOptions> signalWire, IOptions<SolaOptions> sola, Microsoft.Extensions.Hosting.IHostEnvironment env,
        IHttpClientFactory http, MarketplaceDb db, TimeProvider time, CancellationToken ct)
    {
        // one probe of the connector's health page, status only: no credentials, no body read, a short timeout
        (bool, int?)? probe = null;
        if (VoiceReadiness.ConnectorHealthUrl(sola.Value) is { } url)
        {
            try
            {
                using var client = http.CreateClient("voice-readiness");
                client.Timeout = TimeSpan.FromSeconds(3);
                using var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                probe = (res.IsSuccessStatusCode, (int)res.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested) { probe = (false, null); }
        }
        var report = VoiceReadiness.Build(voice.Value, signalWire.Value, sola.Value, StartupValidator.IsDevLike(env), probe);
        StaffAudit.Add(db, time, ctx.Staff(), "voice.readiness_view", "voice", null, "readiness view");
        await db.SaveChangesAsync(ct);
        return Results.Json(report);
    }
}
