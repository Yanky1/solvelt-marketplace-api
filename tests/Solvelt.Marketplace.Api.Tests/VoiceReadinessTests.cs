using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The staff readiness view of the voice line (decision V-51): names of missing settings, never values; staff only and audited; the same evaluations as start-up. All on fakes; no network but one refused local connection.</summary>
public sealed class VoiceReadinessTests
{
    // values that must NEVER appear in the view: every secret the line takes
    private const string SecretOpenAi = "SECRET-openai-VALUE-1", SecretSw = "SECRET-signalwire-VALUE-2", SecretEleven = "SECRET-eleven-VALUE-3", SecretSola = "SECRET-sola-VALUE-4", SecretPin = "SECRETpinVALUE0123456789",
        SecretConn = "SECRET-connector-password-VALUE-5", SecretKey = "SECRET-connector-key-VALUE-6-0123456789", SecretSaved = "SECRET-saved-card-key-VALUE-7-01234567";
    private static readonly string[] Secrets = [SecretOpenAi, SecretSw, SecretEleven, SecretSola, SecretPin, SecretConn, SecretKey, SecretSaved];

    private static Dictionary<string, string?> Config() => VoiceFactory.Config(VoicePipelineMenuTests.MenuConfig(more: new()
    {
        ["Voice:OpenAiApiKey"] = SecretOpenAi, ["SignalWire:SigningKey"] = SecretSw,
        ["Voice:Pipelines:P7:Enabled"] = "true", ["Voice:Pipelines:Llm:Model"] = "model-x", ["Voice:Pipelines:Llm:Provider"] = "openai", ["Voice:ElevenLabs:ApiKey"] = SecretEleven,   // p7 lacks the voice id and the TTS model on purpose
        ["Voice:PhonePaymentProvider"] = "sola", ["Sola:PaymentSiteUrl"] = "https://pay.sola.test/site", ["Sola:WebhookPin"] = SecretPin, ["Sola:ApiKey"] = SecretSola,
        ["Sola:PayConnectorUrl"] = "https://127.0.0.1:1/connector", ["Sola:PayConnectorUser"] = "conn-user", ["Sola:PayConnectorPassword"] = SecretConn, ["Sola:PayConnectorKey"] = SecretKey, ["Sola:SavedCardKey"] = SecretSaved,
    }));

    private static async Task<(JsonElement Json, string Text)> Read(HttpResponseMessage r) { var t = await r.Content.ReadAsStringAsync(); return (JsonDocument.Parse(t).RootElement.Clone(), t); }
    private static JsonElement Option(JsonElement j, string id) => j.GetProperty("pipelines").EnumerateArray().Single(o => o.GetProperty("option").GetString() == id);
    private static string[] Missing(JsonElement e) => e.GetProperty("missing").EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Fact]
    public async Task The_view_is_for_staff_with_voice_view_only_and_every_look_is_audited()
    {
        using var s = new StaffKit(Config());
        var (admin, _, _) = await s.Admin();
        var (support, _, _) = await s.Member(admin, "support@example.com", "support");
        var (finance, _, _) = await s.Member(admin, "finance@example.com", "finance");
        Assert.Equal(HttpStatusCode.Unauthorized, (await s.Get("/api/v1/staff/voice/readiness", null)).StatusCode);          // never public
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Get("/api/v1/staff/voice/readiness", support)).StatusCode);          // support lacks voice.view
        Assert.DoesNotContain(await s.Audit(), a => a.Action == "voice.readiness_view");
        Assert.Equal(HttpStatusCode.OK, (await s.Get("/api/v1/staff/voice/readiness", finance)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Get("/api/v1/staff/voice/readiness", admin)).StatusCode);
        var looks = (await s.Audit()).Where(a => a.Action == "voice.readiness_view").ToList();
        Assert.Equal(2, looks.Count);
        Assert.All(looks, a => Assert.Equal("voice", a.TargetType));
        Assert.Equal(2, looks.Select(a => a.ActorId).Distinct().Count());                                                   // who looked is on record
    }

    [Fact]
    public async Task The_view_names_what_is_missing_and_never_shows_a_value()
    {
        using var s = new StaffKit(Config());
        var (admin, _, _) = await s.Admin();
        var (j, text) = await Read(await s.Get("/api/v1/staff/voice/readiness", admin));
        foreach (var secret in Secrets) Assert.DoesNotContain(secret, text);                                                  // not one secret value, anywhere
        Assert.True(j.GetProperty("voiceEnabled").GetBoolean());
        Assert.Equal("available", Option(j, "p1").GetProperty("state").GetString());
        Assert.Equal("off", Option(j, "p2").GetProperty("state").GetString());                                                // no providers named
        var p7 = Option(j, "p7");
        Assert.Equal("unavailable", p7.GetProperty("state").GetString());
        Assert.Equal(["Voice:ElevenLabs:TtsModel", "Voice:ElevenLabs:VoiceId"], Missing(p7).Order().ToArray());               // names only
        Assert.Equal(7, j.GetProperty("pipelines").GetArrayLength());
        var pay = j.GetProperty("phonePayments");
        Assert.Equal(("sola", "available", "Sola: mode unknown"), (pay.GetProperty("provider").GetString(), pay.GetProperty("state").GetString(), pay.GetProperty("solaMode").GetString()));
        Assert.Equal(("off", "off"), (j.GetProperty("diagnostics").GetProperty("state").GetString(), j.GetProperty("testSet").GetProperty("state").GetString()));
    }

    [Fact]
    public async Task The_connector_probe_gives_status_only_and_a_refused_connection_reads_as_not_reachable()
    {
        using var s = new StaffKit(Config());
        var (admin, _, _) = await s.Admin();
        var (j, text) = await Read(await s.Get("/api/v1/staff/voice/readiness", admin));
        var c = j.GetProperty("phonePayments").GetProperty("connector");
        Assert.True(c.GetProperty("configured").GetBoolean());
        Assert.False(c.GetProperty("reachable").GetBoolean());                                                                // nothing listens on 127.0.0.1:1
        Assert.Equal(JsonValueKind.Null, c.GetProperty("status").ValueKind);
        Assert.DoesNotContain("conn-user", text);                                                                             // no credential, no URL path of the connector
        Assert.DoesNotContain("127.0.0.1", text);
        // the probe asks the health page of the connector's ORIGIN, never the path, the user or the query
        Assert.Equal("https://host.example:8443/health", VoiceReadiness.ConnectorHealthUrl(new SolaOptions { PayConnectorUrl = "https://u:p@host.example:8443/connector?x=1" })!.ToString());
        Assert.Null(VoiceReadiness.ConnectorHealthUrl(new SolaOptions { PayConnectorUrl = "http://host.example/connector" }));
        Assert.Null(VoiceReadiness.ConnectorHealthUrl(new SolaOptions()));
    }

    [Fact]
    public void The_unavailable_reasons_are_exactly_the_ones_start_up_warns_about()
    {
        var v = new VoiceOptions { Enabled = true, OpenAiApiKey = "k" };
        v.PipelineMenu.Enabled = true; v.Pipelines.P7.Enabled = true; v.Pipelines.Llm.Provider = "openai"; v.Pipelines.Llm.Model = "m"; v.ElevenLabs.ApiKey = "e";
        v.Pipelines.P2.Stt = "deepgram"; v.Pipelines.P2.Tts = "elevenlabs";
        var logs = new CaptureLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var sp = new ServiceCollection().AddSingleton<ILoggerFactory>(factory).BuildServiceProvider();
        Assert.Empty(new VendorPartsSource(sp).Create(v));                                                                   // nothing is ready, each unavailable option is warned about once
        var report = VoiceReadiness.Build(v, new SignalWireOptions(), new SolaOptions(), devLike: true, null);
        foreach (var option in new[] { "p2", "p7" })
        {
            var view = report.Pipelines.Single(o => o.Option == option);
            Assert.Equal("unavailable", view.State);
            Assert.NotEmpty(view.Missing);
            var warning = Assert.Single(logs.Lines, l => l.Contains($"option {option} is not available"));
            foreach (var name in view.Missing) Assert.Contains(name, warning);                                                 // the view and the warning name the same settings
            Assert.Equal(VoicePipelineVendors.Missing(v, option), view.Missing);
        }
    }

    [Fact]
    public void Payments_in_production_need_the_api_key_and_a_live_named_key_is_unavailable_without_its_value_being_shown()
    {
        var v = new VoiceOptions { Enabled = true, OpenAiApiKey = "k", PhonePaymentProvider = "sola" };
        var sola = new SolaOptions { PaymentSiteUrl = "https://pay.sola.test/site", WebhookPin = "abcdefghij0123456789" };
        var none = VoiceReadiness.Build(v, new SignalWireOptions(), sola, devLike: false, null).PhonePayments;
        Assert.Equal("unavailable", none.State);
        Assert.Contains("Sola:ApiKey", none.Missing);
        Assert.StartsWith("Sola: not configured", none.SolaMode);
        sola.ApiKey = "acct_live_SECRETVALUE";
        var live = VoiceReadiness.Build(v, new SignalWireOptions(), sola, devLike: false, null).PhonePayments;
        Assert.Equal("unavailable", live.State);
        Assert.Contains("Sola:ApiKey", live.Missing);
        Assert.StartsWith("Sola: LIVE mode", live.SolaMode);
        Assert.DoesNotContain("SECRETVALUE", JsonSerializer.Serialize(live));
        sola.ApiKey = "xk_test_abc";
        Assert.DoesNotContain("Sola:ApiKey", VoiceReadiness.Build(v, new SignalWireOptions(), sola, devLike: false, null).PhonePayments.Missing);
        Assert.Equal("off", VoiceReadiness.Build(new VoiceOptions { Enabled = true, OpenAiApiKey = "k" }, new SignalWireOptions(), sola, false, null).PhonePayments.State);   // provider none
    }

    [Fact]
    public void Diagnostics_and_the_test_set_report_their_own_missing_settings_and_the_module_off_shows_everything_off()
    {
        var v = new VoiceOptions { Enabled = true, OpenAiApiKey = "k" };
        v.PipelineMenu.Enabled = true; v.Diagnostics.Enabled = true; v.Diagnostics.RetentionHours = 999;
        v.TestSet.Enabled = true;                                                                                            // no storage, no test numbers, no SignalWire settings
        var r = VoiceReadiness.Build(v, new SignalWireOptions(), new SolaOptions(), devLike: true, null);
        Assert.Equal("unavailable", r.Diagnostics.State); Assert.Contains("Voice:Diagnostics:RetentionHours", r.Diagnostics.Missing);
        Assert.Equal("unavailable", r.TestSet.State);
        Assert.Contains("Voice:TestSet:StorageAccountUrl", r.TestSet.Missing); Assert.Contains("Voice:PipelineMenu:TestNumbers", r.TestSet.Missing);
        var off = VoiceReadiness.Build(new VoiceOptions(), new SignalWireOptions(), new SolaOptions(), true, null);
        Assert.False(off.VoiceEnabled);
        Assert.All(off.Pipelines, o => Assert.Equal("off", o.State));
        Assert.Equal(("off", "off", "off"), (off.PhonePayments.State, off.Diagnostics.State, off.TestSet.State));
        Assert.Equal(["Sola:ApiKey", "Voice:Pipelines:P7:Enabled"], VoiceReadiness.Names(["Sola:ApiKey (env Sola__ApiKey) is required. Voice:Pipelines:P7:Enabled=true", "no names here"]).ToArray());
    }
}
