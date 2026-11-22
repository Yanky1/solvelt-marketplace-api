using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Staff;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The recognition diagnostics (decision V-39): the owner's own test calls only, off by default, masked, short-lived, staff-only and audited. All on fakes.</summary>
public sealed class VoiceDiagnosticsTests : IDisposable
{
    private const string Number = "+15551234567", Dialed = "+15559876543", Other = "+15557770000";
    private VoiceScenario? _s;
    private FakePhone? _phone;
    public void Dispose() { _phone?.Dispose(); _s?.Dispose(); }
    private VoiceScenario S => _s!;

    private static Dictionary<string, string?> On(Dictionary<string, string?>? more = null) { var d = new Dictionary<string, string?> { ["Voice:Diagnostics:Enabled"] = "true" }; if (more is not null) foreach (var kv in more) d[kv.Key] = kv.Value; return d; }

    /// <summary>A call from <paramref name="from"/> that chose option p2 on the menu (offered because the caller or the dialed number is listed).</summary>
    private async Task<FakeParts> Call(string from, Dictionary<string, string?> config, string[]? menuNumbers = null)
    {
        var parts = new FakeParts("p2");
        parts.LlmFake.Then(new LlmTextDelta("Hello."), new LlmCompleted(10, 2));
        _s = new VoiceScenario(from, VoicePipelineMenuTests.MenuConfig(menuNumbers ?? [Number], config), services: sv => sv.AddSingleton<IChainedPipelineParts>(parts));
        await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, from);
        var res = await VoiceFactory.Hook(S.C, VoiceEndpoints.PipelinePath, S.Sid, from, extra: new() { ["Digits"] = "2" });
        var (token, _) = await VoiceScenario.StreamFrom(res);
        _phone = await FakePhone.Connect(S.F, token);
        await _phone.Start();
        return parts;
    }

    private async Task Advance(int ms) { for (var i = 0; i < ms / 50; i++) { S.F.Clock.Advance(TimeSpan.FromMilliseconds(50)); await Task.Delay(3); } }

    private async Task<List<VoiceDiagnosticTurn>> Rows() => await S.F.Services.CreateScope().ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceDiagnosticTurns.AsNoTracking().OrderBy(t => t.At).ToListAsync();

    private async Task Speak(FakeParts p, string text)
    {
        p.SttFake.Say(text);
        await Advance(1500);
    }

    // ---------- what is stored, and when ----------

    [Fact]
    public async Task A_test_number_with_the_flag_on_stores_the_turn_masked_with_its_option_recogniser_endpointing_reason_and_timings()
    {
        var p = await Call(Number, On());
        await Advance(1500);
        await Speak(p, "two challah rolls please my email is jane@example.com and my number is 555 123 4567");
        var row = Assert.Single(await Rows());
        Assert.Equal(("p2", "p2", "silence"), (row.Option, row.Recogniser, row.EndpointingReason));                  // no provider names are configured in this test: the option id stands in
        Assert.Contains("two challah rolls please", row.Text);
        Assert.Contains("[email]", row.Text); Assert.Contains("[digits]", row.Text);
        Assert.DoesNotContain("jane@example.com", row.Text); Assert.DoesNotContain("555 123", row.Text);              // masked before it is stored
        Assert.Equal(1, row.Turn);
        Assert.NotNull(row.SpeechEndToTextMs);
        Assert.Equal((await S.Row()).Id, row.CallId);
        await FakeRealtime.Poll(() => Rows().Result.Single().SpeechEndToFirstAudioMs is not null ? "x" : null, 5);    // added when the reply's first audio is sent
    }

    [Fact]
    public async Task The_flag_off_stores_nothing()
    {
        var p = await Call(Number, new());
        await Advance(1500);
        await Speak(p, "two challah rolls please");
        Assert.Empty(await Rows());
    }

    [Fact]
    public async Task A_caller_who_is_not_a_test_number_stores_nothing_even_when_the_number_dialed_is_listed_and_the_flag_is_on()
    {
        var p = await Call(Other, On(), menuNumbers: [Dialed]);                          // the menu is offered by the dialed number; the CALLER is not one of the owner's numbers
        await Advance(1500);
        await Speak(p, "two challah rolls please");
        Assert.Empty(await Rows());
    }

    [Fact]
    public async Task A_turn_during_a_keypad_step_or_a_suppressed_turn_stores_nothing()
    {
        var p = await Call(Number, On());
        await Advance(1500);
        S.State().BeginKeypadStep("pin");
        await Speak(p, "my pin is one two three");
        S.State().EndKeypadStep();
        S.State().SuppressDiagnosticsNow();
        await Speak(p, "the code is four seven two");
        Assert.Empty(await Rows());
        S.State().AllowDiagnostics();
        await Speak(p, "and a plain sentence");
        Assert.Equal("and a plain sentence", Assert.Single(await Rows()).Text);
    }

    [Fact]
    public async Task Digits_cards_and_codes_typed_or_recognised_as_numbers_are_masked_and_emails_too()
    {
        Assert.Equal("my card is [digits] thanks", DiagnosticText.Mask("my card is 4242 4242 4242 4242 thanks"));
        Assert.Equal("code [digits]", DiagnosticText.Mask("code 472913"));
        Assert.Equal("[digits]", DiagnosticText.Mask("1-2-3-4"));
        Assert.Equal("two 12 loaves", DiagnosticText.Mask("two 12 loaves"));                                        // short numbers are quantities and stay
        Assert.Equal("write to [email] now", DiagnosticText.Mask("write to jane.doe@example.co.uk now"));
        Assert.Equal(2000, DiagnosticText.Mask(new string('a', 5000)).Length);
        Assert.DoesNotContain('\0', DiagnosticText.Mask("a\0b"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Option_five_keeps_each_engines_text_and_the_judges_pick_masked_in_one_json_cell()
    {
        using var s = new VoiceScenario(Number, VoicePipelineMenuTests.MenuConfig(more: On()));
        var diag = s.F.Services.GetRequiredService<IVoiceDiagnostics>();
        (await VoiceFactory.Hook(s.C, VoiceEndpoints.InboundPath, s.Sid, Number)).EnsureSuccessStatusCode();
        var call = await s.Row();
        var state = s.F.Services.GetRequiredService<VoiceStateStore>().GetOrCreate(call.Id);
        state.SetCaller(Number);
        var id = await diag.RecordAsync(call, state, new DiagnosticTurn("p5", "gladia+yiddishlabs", "a challah", [new("gladia", "a hallah", "yi", 0.8), new("yiddishlabs", "a challah 4242 4242", "yi", null)], "a challah", "yi", 0.8, 420, null, "silence"), CancellationToken.None);
        Assert.NotNull(id);
        var row = await s.F.Services.CreateScope().ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceDiagnosticTurns.AsNoTracking().SingleAsync();
        var j = JsonNode.Parse(row.Engines!)!;
        Assert.Equal(["gladia", "yiddishlabs"], j["engines"]!.AsArray().Select(e => e!["engine"]!.GetValue<string>()));
        Assert.Equal("a challah [digits]", j["engines"]![1]!["text"]!.GetValue<string>());                          // each engine's text is masked too
        Assert.Equal("a challah", j["judge"]!.GetValue<string>());
        Assert.Equal(("yi", 0.8, 420L), (row.Language, row.Confidence, row.SpeechEndToTextMs));
    }

    [Fact]
    public async Task Long_candidate_texts_always_make_valid_json_that_fits_the_column_and_the_staff_view_returns_it()
    {
        var yiddish = string.Concat(Enumerable.Repeat("איך וויל א חלה ", 200))[..2000];                                  // 2000 characters of Hebrew letters
        var three = new SttCandidate[] { new("gladia", yiddish, "yi", null), new("yiddishlabs", yiddish, "yi", 0.8), new("openai", yiddish, "yi", null) };
        var json = VoiceDiagnostics.EnginesJson(three, yiddish)!;
        Assert.True(json.Length <= VoiceDiagnostics.EnginesColumn);
        var doc = JsonNode.Parse(json)!;                                                                                // valid JSON: it parses
        Assert.Equal(3, doc["engines"]!.AsArray().Count);
        Assert.All(doc["engines"]!.AsArray(), e => Assert.True(e!["text"]!.GetValue<string>().Length >= 40));
        Assert.StartsWith("איך וויל", doc["judge"]!.GetValue<string>());
        Assert.True(VoiceDiagnostics.EnginesJson(Enumerable.Range(0, 9).Select(i => new SttCandidate("e" + i, "x", null, null)).ToList(), null) is { } many && JsonNode.Parse(many)!["engines"]!.AsArray().Count == VoiceDiagnostics.MaxCandidates);
        Assert.Null(VoiceDiagnostics.EnginesJson(null, "judge")); Assert.Null(VoiceDiagnostics.EnginesJson([], "judge"));
        var nasty = string.Concat(Enumerable.Repeat("\"\n\t", 1500));                                                // quotes and newlines double in size when escaped
        var nastyJson = VoiceDiagnostics.EnginesJson([new("a", nasty, null, null), new("b", nasty, null, null), new("c", nasty, null, null)], nasty)!;
        Assert.True(nastyJson.Length <= VoiceDiagnostics.EnginesColumn);                                                // the cap was halved until the escaped value fits
        Assert.NotNull(JsonNode.Parse(nastyJson));

        using var s = new StaffKit(VoiceFactory.Config());
        var call = Guid.NewGuid();
        await s.Db(async db => { db.VoiceDiagnosticTurns.Add(new VoiceDiagnosticTurn { Id = Guid.NewGuid(), CallId = call, At = s.F.Clock.GetUtcNow(), Turn = 1, Option = "p5", Recogniser = "x", Text = "t", Engines = json, EndpointingReason = "silence" }); await db.SaveChangesAsync(); return 0; });
        var (admin, _, _) = await s.Admin();
        var r = await s.Post($"/api/v1/staff/voice/diagnostics/calls/{call}", new { reason = "checking a long call" }, admin);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(3, JsonNode.Parse(await r.Content.ReadAsStringAsync())!["turns"]![0]!["engines"]!["engines"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_stored_engines_cell_that_does_not_parse_is_shown_as_text_not_as_a_500()
    {
        using var s = new StaffKit(VoiceFactory.Config());
        var call = Guid.NewGuid();
        await s.Db(async db => { db.VoiceDiagnosticTurns.Add(new VoiceDiagnosticTurn { Id = Guid.NewGuid(), CallId = call, At = s.F.Clock.GetUtcNow(), Turn = 1, Option = "p5", Recogniser = "x", Text = "t", Engines = "{\"engines\":[{\"engine\":\"a\",\"te", EndpointingReason = "silence" }); await db.SaveChangesAsync(); return 0; });
        var (admin, _, _) = await s.Admin();
        var r = await s.Post($"/api/v1/staff/voice/diagnostics/calls/{call}", new { reason = "checking a broken row" }, admin);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("{\"engines\":[{\"engine\":\"a\",\"te", JsonNode.Parse(await r.Content.ReadAsStringAsync())!["turns"]![0]!["engines"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_flag_on_with_no_usable_test_number_stores_nothing_and_warns_once()
    {
        using var s = new VoiceScenario(Number, On());
        var diag = s.F.Services.GetRequiredService<IVoiceDiagnostics>();
        (await VoiceFactory.Hook(s.C, VoiceEndpoints.InboundPath, s.Sid, Number)).EnsureSuccessStatusCode();
        var state = s.F.Services.GetRequiredService<VoiceStateStore>().GetOrCreate((await s.Row()).Id);
        state.SetCaller(Number);
        Assert.False(diag.Active(state)); Assert.False(diag.Active(state));
        Assert.Single(s.F.Logs.Lines, l => l.Contains("Voice:PipelineMenu:TestNumbers has no usable number"));
    }

    [Fact]
    public async Task Option_six_stores_the_signalwire_text_with_the_confidence_only_for_a_test_number_and_the_flag_on()
    {
        foreach (var (flag, from, expected) in new[] { (true, Number, 1), (false, Number, 0), (true, Other, 0) })
        {
            using var s = new VoiceScenario(from, VoicePipelineMenuTests.MenuConfig([Number, Dialed], flag ? On() : new()), services: sv => sv.AddSingleton<IChainedPipelineParts>(new FakeParts("p6")));
            (await VoiceFactory.Hook(s.C, VoiceEndpoints.InboundPath, s.Sid, from)).EnsureSuccessStatusCode();
            await VoiceFactory.Hook(s.C, VoiceEndpoints.PipelinePath, s.Sid, from, extra: new() { ["Digits"] = "6" });
            await VoiceFactory.Hook(s.C, VoiceEndpoints.SpeechPath + "?first=1", s.Sid, from);
            await VoiceFactory.Hook(s.C, VoiceEndpoints.SpeechPath, s.Sid, from, extra: new() { ["SpeechResult"] = "two challah rolls, code 123456", ["Confidence"] = "0.91" });
            var rows = await s.F.Services.CreateScope().ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceDiagnosticTurns.AsNoTracking().ToListAsync();
            Assert.Equal(expected, rows.Count);
            if (expected == 1)
            {
                var row = rows.Single();
                Assert.Equal(("p6", "signalwire", "two challah rolls, code [digits]", "signalwire_speech_timeout", "en-US"), (row.Option, row.Recogniser, row.Text, row.EndpointingReason, row.Language));
                Assert.Equal(0.91, row.Confidence);
            }
        }
    }

    // ---------- retention ----------

    [Fact]
    public async Task Rows_older_than_the_retention_are_deleted_and_the_retention_is_clamped_to_168_hours()
    {
        using var s = new VoiceScenario(Number, On(new() { ["Voice:Diagnostics:RetentionHours"] = "5000" }));
        var diag = s.F.Services.GetRequiredService<VoiceDiagnostics>();
        var now = s.F.Clock.GetUtcNow();
        await using (var scope = s.F.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
            VoiceDiagnosticTurn T(int hoursAgo) => new() { Id = Guid.NewGuid(), CallId = Guid.NewGuid(), At = now.AddHours(-hoursAgo), Turn = 1, Option = "p2", Recogniser = "x", Text = "t", EndpointingReason = "silence" };
            db.VoiceDiagnosticTurns.AddRange(T(200), T(100), T(1));
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await diag.PurgeAsync(CancellationToken.None));                                              // 5000 is clamped to 168: only the 200-hour row goes
        using var small = new VoiceScenario(Number, On(new() { ["Voice:Diagnostics:RetentionHours"] = "0" }));
        Assert.Equal(0, await small.F.Services.GetRequiredService<VoiceDiagnostics>().PurgeAsync(CancellationToken.None));
    }

    [Fact]
    public void An_out_of_range_retention_is_a_start_up_problem_only_when_the_menu_is_on()
    {
        List<string> Problems(Action<VoiceOptions> f)
        {
            var o = new VoiceOptions { Enabled = true, OpenAiApiKey = "k", StreamTokenKey = new string('t', 40), PhoneCodeKey = new string('p', 40), CallerHashKey = new string('c', 40), PublicBaseUrl = "https://voice.test" };
            f(o); return VoiceConfig.Problems(o, new SignalWireOptions { SigningKey = "sw" }, true).ToList();
        }
        Assert.Contains(Problems(o => { o.PipelineMenu.Enabled = true; o.Diagnostics.Enabled = true; o.Diagnostics.RetentionHours = 169; }), p => p.Contains("RetentionHours"));
        Assert.Contains(Problems(o => { o.PipelineMenu.Enabled = true; o.Diagnostics.Enabled = true; o.Diagnostics.RetentionHours = 0; }), p => p.Contains("RetentionHours"));
        Assert.DoesNotContain(Problems(o => { o.PipelineMenu.Enabled = true; o.Diagnostics.Enabled = true; o.Diagnostics.RetentionHours = 168; }), p => p.Contains("RetentionHours"));
        Assert.DoesNotContain(Problems(o => { o.Diagnostics.Enabled = true; o.Diagnostics.RetentionHours = 5000; }), p => p.Contains("RetentionHours"));   // menu off: tolerated (and clamped)
    }

    // ---------- staff access ----------

    private static async Task Seed(StaffKit s, Guid callId)
    {
        var now = s.F.Clock.GetUtcNow();
        await s.Db(async db =>
        {
            db.VoiceDiagnosticTurns.AddRange(
                new VoiceDiagnosticTurn { Id = Guid.NewGuid(), CallId = callId, At = now, Turn = 1, Option = "p5", Recogniser = "gladia+yiddishlabs", Text = "=HYPERLINK(\"x\")", Engines = """{"engines":[{"engine":"gladia","text":"a"}],"judge":"a"}""", Language = "yi", Confidence = 0.8, SpeechEndToTextMs = 400, SpeechEndToFirstAudioMs = 900, EndpointingReason = "silence" },
                new VoiceDiagnosticTurn { Id = Guid.NewGuid(), CallId = callId, At = now.AddSeconds(5), Turn = 2, Option = "p5", Recogniser = "gladia+yiddishlabs", Text = "plain, with \"quotes\"", EndpointingReason = "silence" },
                new VoiceDiagnosticTurn { Id = Guid.NewGuid(), CallId = Guid.NewGuid(), At = now, Turn = 1, Option = "p2", Recogniser = "deepgram", Text = "another call", EndpointingReason = "silence" });
            await db.SaveChangesAsync();
            return 0;
        });
    }

    [Fact]
    public async Task Staff_with_voice_view_read_one_calls_turns_and_the_read_is_audited_with_the_reason_and_the_count()
    {
        using var s = new StaffKit(VoiceFactory.Config());
        var call = Guid.NewGuid();
        await Seed(s, call);
        var (admin, _, _) = await s.Admin();
        var r = await s.Post($"/api/v1/staff/voice/diagnostics/calls/{call}", new { reason = "checking the Yiddish test call" }, admin);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        Assert.Equal([1, 2], j["turns"]!.AsArray().Select(t => t!["turn"]!.GetValue<int>()));
        Assert.Equal("a", j["turns"]![0]!["engines"]!["judge"]!.GetValue<string>());
        var audit = (await s.Audit()).Single(a => a.Action == "voice.diagnostics_view");
        Assert.Equal(("voice_call", call.ToString("D"), "checking the Yiddish test call"), (audit.TargetType, audit.TargetId, audit.Reason));
        Assert.Contains("\"turns\":2", audit.Detail);
    }

    [Fact]
    public async Task The_csv_export_is_audited_quotes_cells_and_defuses_a_leading_formula_character()
    {
        using var s = new StaffKit(VoiceFactory.Config());
        await Seed(s, Guid.NewGuid());
        var (admin, _, _) = await s.Admin();
        var r = await s.Post("/api/v1/staff/voice/diagnostics/export", new { reason = "weekly listening review", hours = 24 }, admin);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("text/csv", r.Content.Headers.ContentType!.MediaType + "");
        var csv = await r.Content.ReadAsStringAsync();
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("call_id,turn,at,option,recogniser,text,engines,language,confidence,speech_end_to_text_ms,speech_end_to_first_audio_ms,endpointing_reason", lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);                                                              // a leading = is neutralised, quotes are doubled
        Assert.Contains("\"plain, with \"\"quotes\"\"\"", csv);
        Assert.Contains((await s.Audit()), a => a.Action == "voice.diagnostics_export" && a.Reason == "weekly listening review" && a.Detail!.Contains("\"rows\":3"));
    }

    [Fact]
    public async Task Without_a_staff_session_or_the_voice_view_permission_or_a_reason_nothing_is_read_and_nothing_is_audited()
    {
        using var s = new StaffKit(VoiceFactory.Config());
        var call = Guid.NewGuid();
        await Seed(s, call);
        var path = $"/api/v1/staff/voice/diagnostics/calls/{call}";
        Assert.NotEqual(HttpStatusCode.OK, (await s.Post(path, new { reason = "checking a call" }, null)).StatusCode);          // no session
        var (admin, _, _) = await s.Admin();
        var (support, _, _) = await s.Member(admin, "support@example.com", StaffRoleNames.Support);                              // support has no voice.view
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Post(path, new { reason = "checking a call" }, support)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Post("/api/v1/staff/voice/diagnostics/export", new { reason = "checking a call" }, support)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await s.Post(path, new { reason = "x" }, admin)).StatusCode);        // a reason is required
        Assert.DoesNotContain(await s.Audit(), a => a.Action.StartsWith("voice.diagnostics"));
    }
}
