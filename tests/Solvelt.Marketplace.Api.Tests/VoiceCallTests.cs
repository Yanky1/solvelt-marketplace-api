using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Whole calls through the real app: a fake SignalWire stream on one side, a scripted fake OpenAI Realtime server on the other (decision V-01).</summary>
public class VoiceCallTests
{
    private const string Audio = "AAECAwQFBgc=";   // 8 bytes

    private static Task<T> Db<T>(TestFactory f, Func<MarketplaceDb, Task<T>> q) => Task.Run(async () =>
    {
        using var s = f.Services.CreateScope();
        return await q(s.ServiceProvider.GetRequiredService<MarketplaceDb>());
    });

    private static async Task<VoiceCall> Call(TestFactory f) => await Db(f, db => db.VoiceCalls.AsNoTracking().SingleAsync());

    // ---------- the webhooks ----------

    [Fact]
    public async Task Inbound_answers_with_the_disclosure_and_a_bidirectional_stream_with_a_token_and_records_a_masked_call()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake, new() { ["Voice:ModelMode"] = "mini" });
        var r = await VoiceFactory.Hook(f.CreateClient(), VoiceEndpoints.InboundPath, "CA-1", "+15551234567");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/xml", r.Content.Headers.ContentType!.MediaType);
        var xml = XDocument.Parse(await r.Content.ReadAsStringAsync()).Root!;
        Assert.Contains("AI", xml.Element("Say")!.Value.Replace("A I", "AI"));                       // spoken by the platform, not the model
        var stream = xml.Element("Connect")!.Element("Stream")!;
        var url = new Uri(stream.Attribute("url")!.Value);
        Assert.Equal(("wss", "voice.test", VoiceEndpoints.StreamPath), (url.Scheme, url.Host, url.AbsolutePath));
        Assert.Null(xml.Element("Connect")!.Attribute("action"));                                       // the Connect page documents no action: the Redirect continues the call
        Assert.Equal(VoiceFactory.BaseUrl + VoiceEndpoints.AfterStreamPath, xml.Element("Redirect")!.Value);
        var call = await Call(f);
        Assert.Equal(("CA-1", "***4567", "model-mini", "mini", VoiceOutcome.InProgress), (call.CallSid, call.CallerMasked, call.Model, call.Tier, call.Outcome));
        Assert.DoesNotContain("5551234567", await r.Content.ReadAsStringAsync());
        Assert.DoesNotContain(f.Logs.Lines, l => l.Contains("5551234567"));
    }

    [Fact]
    public async Task The_cxml_hands_the_call_on_after_the_stream_and_sends_the_token_as_a_bearer_too()
    {
        using var f = VoiceFactory.Create(new FakeRealtime());
        var xml = XDocument.Parse(await (await VoiceFactory.Hook(f.CreateClient(), VoiceEndpoints.InboundPath, "CA-r")).Content.ReadAsStringAsync()).Root!;
        var after = VoiceFactory.BaseUrl + VoiceEndpoints.AfterStreamPath;
        Assert.Equal(after, xml.Element("Redirect")!.Value);                       // the Connect page documents no "action", so the Redirect is what continues the call
        var stream = xml.Element("Connect")!.Element("Stream")!;
        var inUrl = System.Web.HttpUtility.ParseQueryString(new Uri(stream.Attribute("url")!.Value).Query)["token"];
        Assert.Equal(inUrl, stream.Attribute("authBearerToken")!.Value);
    }

    [Fact]
    public async Task The_stream_accepts_the_token_as_a_bearer_header_alone_and_a_bad_bearer_is_refused()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake);
        var token = await VoiceFactory.Answer(f.CreateClient());
        await Assert.ThrowsAnyAsync<Exception>(() => FakePhone.Connect(f, token + "0", bearerOnly: true));
        using var phone = await FakePhone.Connect(f, token, bearerOnly: true);
        await phone.Start();
        await fake.Wait("session.update");
    }

    [Fact]
    public void The_shipped_defaults_are_the_verified_models_voice_and_prices()
    {
        var o = new VoiceOptions();
        Assert.Equal(("gpt-realtime-2.1", "gpt-realtime-2.1-mini", "cedar"), (o.OpenAiModel, o.OpenAiModelMini, o.OpenAiVoice));
        Assert.Equal(32_000_000 + 64_000_000 + 4_000_000 + 24_000_000, new VoiceUsage(1_000_000, 1_000_000, 1_000_000, 1_000_000).EstimateMicros(o.PricePerMillionFull));
        Assert.Equal(10_000_000 + 20_000_000 + 600_000 + 2_400_000, new VoiceUsage(1_000_000, 1_000_000, 1_000_000, 1_000_000).EstimateMicros(o.PricePerMillionMini));
    }

    [Fact]
    public async Task A_retried_webhook_is_one_call_even_in_parallel()
    {
        using var f = VoiceFactory.Create(new FakeRealtime());
        var c = f.CreateClient();
        var rs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => VoiceFactory.Hook(c, VoiceEndpoints.InboundPath, "CA-same")));
        Assert.All(rs, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, await Db(f, db => db.VoiceCalls.CountAsync()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("AAAA")]
    public async Task Unsigned_or_wrongly_signed_webhooks_are_refused_and_start_nothing(string? signature)
    {
        using var f = VoiceFactory.Create(new FakeRealtime());
        var req = new HttpRequestMessage(HttpMethod.Post, VoiceEndpoints.InboundPath) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["CallSid"] = "CA-x", ["From"] = "+15551234567" }) };
        if (signature != null) req.Headers.Add(SignalWireSignature.Header, signature);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.CreateClient().SendAsync(req)).StatusCode);
        Assert.Equal(0, await Db(f, db => db.VoiceCalls.CountAsync()));
    }

    [Fact]
    public async Task A_signature_for_another_url_or_body_is_refused()
    {
        using var f = VoiceFactory.Create(new FakeRealtime());
        var other = SignalWireSignature.Compute(VoiceFactory.SigningKey, VoiceFactory.BaseUrl + VoiceEndpoints.AfterStreamPath, new Dictionary<string, string> { ["CallSid"] = "CA-1", ["From"] = "+15551234567", ["To"] = "+15559876543" });
        Assert.Equal(HttpStatusCode.Forbidden, (await VoiceFactory.Hook(f.CreateClient(), VoiceEndpoints.InboundPath, "CA-1", signature: other)).StatusCode);
    }

    [Fact]
    public async Task Nothing_answers_while_voice_is_disabled()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake, new() { ["Voice:Enabled"] = "false" });
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await VoiceFactory.Hook(c, VoiceEndpoints.InboundPath, "CA-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await VoiceFactory.Hook(c, VoiceEndpoints.AfterStreamPath, "CA-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(VoiceEndpoints.StreamPath)).StatusCode);
    }

    [Fact]
    public async Task The_stream_needs_a_valid_unexpired_token()
    {
        using var f = VoiceFactory.Create(new FakeRealtime());
        var token = await VoiceFactory.Answer(f.CreateClient());
        await Assert.ThrowsAnyAsync<Exception>(() => FakePhone.Connect(f, "nonsense"));
        await Assert.ThrowsAnyAsync<Exception>(() => FakePhone.Connect(f, token + "0"));
        f.Clock.Advance(TimeSpan.FromMinutes(5));
        await Assert.ThrowsAnyAsync<Exception>(() => FakePhone.Connect(f, token));   // expired
    }

    [Fact]
    public async Task A_call_that_has_ended_cannot_be_streamed_again()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake);
        var token = await VoiceFactory.Answer(f.CreateClient());
        using (var phone = await FakePhone.Connect(f, token)) { await phone.Start(); await phone.Send(new { @event = "stop" }); Assert.True(await phone.WaitClosed()); }
        await FakeRealtime.Poll(() => Db(f, db => db.VoiceCalls.Where(c => c.EndedAt != null).CountAsync()).Result == 1 ? "x" : null, 5);
        await Assert.ThrowsAnyAsync<Exception>(() => FakePhone.Connect(f, token));
    }

    // ---------- the bridge ----------

    [Fact]
    public async Task A_call_opens_the_model_session_with_the_chosen_model_the_phone_audio_format_the_voice_a_greeting_and_the_tools()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake, new() { ["Voice:OpenAiVoice"] = "ash", ["Voice:ModelMode"] = "full" });
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        var update = await fake.Wait("session.update");
        var s = update["session"]!;
        Assert.Equal("model-full", fake.Model);
        Assert.Equal("audio/pcmu", s["audio"]!["input"]!["format"]!["type"]!.GetValue<string>());
        Assert.Equal("audio/pcmu", s["audio"]!["output"]!["format"]!["type"]!.GetValue<string>());
        Assert.Equal("ash", s["audio"]!["output"]!["voice"]!.GetValue<string>());
        Assert.Equal("semantic_vad", s["audio"]!["input"]!["turn_detection"]!["type"]!.GetValue<string>());   // the agent does not cut in on a caller who pauses mid-sentence
        Assert.Contains(s["tools"]!.AsArray(), t => t!["name"]!.GetValue<string>() == "transfer_to_person");
        var greeting = await fake.Wait("response.create");
        Assert.Contains("AI", greeting["response"]!["instructions"]!.GetValue<string>());
        // the instructions say what the model must never do with card data and codes
        Assert.Contains("keypad", s["instructions"]!.GetValue<string>());
    }

    [Fact]
    public async Task Caller_audio_goes_up_and_agent_audio_comes_down()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake);
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        await phone.Media(Audio);
        var up = await fake.Wait("input_audio_buffer.append");
        Assert.Equal(Audio, up["audio"]!.GetValue<string>());
        await fake.Send(new { type = "response.output_audio.delta", item_id = "item_1", delta = Audio });
        var down = await phone.Wait("media");
        Assert.Equal(Audio, down["media"]!["payload"]!.GetValue<string>());
        Assert.Equal("MZ1", down["streamSid"]!.GetValue<string>());
    }

    [Fact]
    public async Task Barge_in_clears_the_phone_queue_and_truncates_what_the_model_said()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake);
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        await fake.Send(new { type = "response.output_audio.delta", item_id = "item_7", delta = Audio });
        await fake.Send(new { type = "response.output_audio.delta", item_id = "item_7", delta = Audio });
        await phone.Wait("media");
        await FakeRealtime.Poll(() => phone.Got.Count(n => n["event"]!.GetValue<string>() == "media") == 2 ? "x" : null, 5);
        await fake.Send(new { type = "input_audio_buffer.speech_started" });
        var clear = await phone.Wait("clear");
        Assert.Equal("MZ1", clear["streamSid"]!.GetValue<string>());
        var cut = await fake.Wait("conversation.item.truncate");
        Assert.Equal("item_7", cut["item_id"]!.GetValue<string>());
        Assert.Equal(2, cut["audio_end_ms"]!.GetValue<int>());   // 16 bytes of mu-law = 2 ms
    }

    [Fact]
    public async Task Speech_while_the_agent_is_silent_clears_nothing()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake);
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        await fake.Send(new { type = "input_audio_buffer.speech_started" });
        await Task.Delay(200);
        Assert.DoesNotContain(phone.Got, n => n["event"]!.GetValue<string>() == "clear");
        Assert.Empty(fake.Of("conversation.item.truncate"));
    }

    [Fact]
    public async Task Usage_cost_and_latency_are_recorded_per_call_and_the_call_closes_with_its_duration()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake, new()
        {
            ["Voice:PricePerMillionFull:AudioIn"] = "32", ["Voice:PricePerMillionFull:AudioOut"] = "64", ["Voice:PricePerMillionFull:TextIn"] = "4", ["Voice:PricePerMillionFull:TextOut"] = "16",
        });
        var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        await fake.Send(new { type = "input_audio_buffer.speech_stopped" });
        await fake.Send(new { type = "response.output_audio.delta", item_id = "i1", delta = Audio });
        await phone.Wait("media");
        await fake.Send(new
        {
            type = "response.done",
            response = new { output = Array.Empty<object>(), usage = new { input_token_details = new { audio_tokens = 1000, text_tokens = 200 }, output_token_details = new { audio_tokens = 500, text_tokens = 100 } } },
        });
        await FakeRealtime.Poll(() => Db(f, db => db.VoiceCalls.Where(c => c.AudioInTokens == 1000).CountAsync()).Result == 1 ? "x" : null, 5);
        f.Clock.Advance(TimeSpan.FromSeconds(90));
        await phone.Send(new { @event = "stop" });
        await phone.WaitClosed();
        var call = await FakeRealtime.Poll(() => Db(f, db => db.VoiceCalls.AsNoTracking().Where(c => c.EndedAt != null).FirstOrDefaultAsync()).Result, 5);
        Assert.Equal((1000L, 500L, 200L, 100L), (call.AudioInTokens, call.AudioOutTokens, call.TextInTokens, call.TextOutTokens));
        Assert.Equal(1000 * 32 + 500 * 64 + 200 * 4 + 100 * 16, call.EstimatedCostMicros);
        Assert.Equal(1, call.LatencySamples);
        Assert.Equal(90, call.DurationSeconds);
        Assert.Equal(VoiceOutcome.Abandoned, call.Outcome);
    }

    [Fact]
    public async Task The_caller_hanging_up_closes_the_model_session()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake);
        var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        await fake.Wait("session.update");
        await phone.Send(new { @event = "stop" });
        await FakeRealtime.Poll(() => fake.Closed ? "x" : null, 5);
    }

    [Fact]
    public async Task When_the_model_session_ends_the_phone_stream_ends_too()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake);
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        await fake.Wait("session.update");
        await fake.Dispose2();
        Assert.True(await phone.WaitClosed());
    }

    [Fact]
    public async Task A_model_session_that_cannot_open_ends_the_call_with_a_503_and_no_leak()
    {
        var fake = new FakeRealtime { Fail = true };
        using var f = VoiceFactory.Create(fake);
        var tok = await VoiceFactory.Answer(f.CreateClient());
        await Assert.ThrowsAnyAsync<Exception>(() => FakePhone.Connect(f, tok));
        Assert.Equal(VoiceOutcome.Abandoned, (await Call(f)).Outcome);
        Assert.DoesNotContain(f.Logs.Lines, l => l.Contains("test-key-not-real"));
    }

    [Fact]
    public async Task A_call_is_ended_after_the_configured_length()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake, new() { ["Voice:MaxCallSeconds"] = "1" });
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        Assert.True(await phone.WaitClosed(6));
    }

    // ---------- keypad ----------

    [Fact]
    public async Task Keypad_digits_never_reach_the_model_or_the_logs()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake);
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        foreach (var d in "4111111111111111#") await phone.Dtmf(d.ToString());
        await phone.Media(Audio);
        await fake.Wait("input_audio_buffer.append");   // the audio sent after the digits has arrived, so the digits would have too
        Assert.DoesNotContain(fake.Received, m => m.Contains("4111") || m.Contains("\"dtmf\"", StringComparison.OrdinalIgnoreCase) || m.Contains("\"digit\""));
        Assert.DoesNotContain(f.Logs.Lines, l => l.Contains("4111") || l.Contains("dtmf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Zero_on_the_keypad_transfers_to_a_person_in_support_hours_and_the_next_instruction_dials()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake, new() { ["Voice:SupportNumber"] = "+15550001111", ["Voice:SupportHours"] = "09:00-17:00", ["Voice:SupportTimeZone"] = "UTC" });
        f.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero));
        var c = f.CreateClient();
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(c, "CA-t"));
        await phone.Start();
        await phone.Dtmf("0");
        Assert.True(await phone.WaitClosed());
        var after = XDocument.Parse(await (await VoiceFactory.Hook(c, VoiceEndpoints.AfterStreamPath, "CA-t")).Content.ReadAsStringAsync()).Root!;
        Assert.Equal("+15550001111", after.Element("Dial")!.Value);
        var call = await FakeRealtime.Poll(() => Db(f, db => db.VoiceCalls.AsNoTracking().Where(x => x.EndedAt != null).FirstOrDefaultAsync()).Result, 5);
        Assert.Equal(VoiceOutcome.Transferred, call.Outcome);
    }

    [Fact]
    public async Task Zero_outside_support_hours_or_without_a_number_keeps_the_call_going_and_never_dials()
    {
        foreach (var cfg in new Dictionary<string, string?>[]
        {
            new() { ["Voice:SupportNumber"] = "+15550001111", ["Voice:SupportHours"] = "09:00-17:00", ["Voice:SupportTimeZone"] = "UTC" },   // the clock is moved to 22:00 below: closed
            new() { },                                                                                                                    // no number
        })
        {
            var fake = new FakeRealtime();
            using var f = VoiceFactory.Create(fake, cfg);
            if (cfg.ContainsKey("Voice:SupportHours")) f.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 17, 22, 0, 0, TimeSpan.Zero));
            var c = f.CreateClient();
            using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(c, "CA-n"));
            await phone.Start();
            await phone.Dtmf("0");
            await Task.Delay(300);
            Assert.False(await phone.WaitClosed(1));
            var after = XDocument.Parse(await (await VoiceFactory.Hook(c, VoiceEndpoints.AfterStreamPath, "CA-n")).Content.ReadAsStringAsync()).Root!;
            Assert.Null(after.Element("Dial"));
            Assert.NotNull(after.Element("Hangup"));
        }
    }

    // ---------- tools: the model proposes, the server decides ----------

    private static object FunctionCall(string name, string callId, string args = "{}") => new
    {
        type = "response.done",
        response = new { output = new object[] { new { type = "function_call", name, call_id = callId, arguments = args } } },
    };

    [Fact]
    public async Task The_transfer_tool_answers_from_the_server_then_ends_the_call_after_the_goodbye_has_played()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake, new() { ["Voice:SupportNumber"] = "+15550001111" });
        var c = f.CreateClient();
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(c, "CA-tool"));
        await phone.Start();
        await fake.Send(FunctionCall("transfer_to_person", "call_1"));
        var output = await fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create" && n["item"]!["type"]!.GetValue<string>() == "function_call_output");
        Assert.Equal("call_1", output["item"]!["call_id"]!.GetValue<string>());
        Assert.Contains("transferring", output["item"]!["output"]!.GetValue<string>());
        await FakeRealtime.Poll(() => fake.Of("response.create").Count() == 2 ? "x" : null, 5);   // the greeting, then the answer to the tool
        await fake.Send(new { type = "response.done", response = new { output = Array.Empty<object>() } });   // the goodbye is done
        var mark = await phone.Wait("mark");
        await phone.Send(new { @event = "mark", streamSid = "MZ1", mark = new { name = mark["mark"]!["name"]!.GetValue<string>() } });   // the phone played it
        Assert.True(await phone.WaitClosed());
        Assert.NotNull((await Call(f)).TransferRequestedAt);
    }

    [Theory]
    [InlineData("issue_refund", """{"orderNumber":"MP-1","amountCents":99999}""")]
    [InlineData("set_price", """{"sku":"X","priceCents":0}""")]
    [InlineData("transfer_to_person ", "{}")]   // a name that is not exactly a registered tool
    [InlineData("", "{}")]
    public async Task A_tool_the_server_does_not_have_changes_nothing_and_the_model_is_told_so(string name, string args)
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake, new() { ["Voice:SupportNumber"] = "+15550001111" });
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        await fake.Send(FunctionCall(name, "call_x", args));
        var output = await fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create");
        Assert.Contains("unknown_tool", output["item"]!["output"]!.GetValue<string>());
        Assert.Null((await Call(f)).TransferRequestedAt);
        Assert.False(await phone.WaitClosed(1));
    }

    [Fact]
    public async Task Malformed_tool_arguments_are_an_error_for_the_model_not_a_crash()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake);
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        await fake.Send(FunctionCall("transfer_to_person", "call_b", "{not json"));
        var output = await fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create");
        Assert.Contains("bad_arguments", output["item"]!["output"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_transfer_tool_tells_the_model_when_nobody_is_available()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake);   // no support number
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        await fake.Send(FunctionCall("transfer_to_person", "call_u"));
        var output = await fake.Wait(n => n["type"]?.GetValue<string>() == "conversation.item.create");
        Assert.Contains("unavailable", output["item"]!["output"]!.GetValue<string>());
        Assert.Null((await Call(f)).TransferRequestedAt);
    }

    // ---------- prompt injection: caller words reach the model, but the server's rules are not in the model ----------

    [Fact]
    public async Task The_model_instructions_and_tools_offer_no_way_to_change_a_price_or_refund()
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake);
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        await phone.Start();
        var s = (await fake.Wait("session.update"))["session"]!;
        var names = s["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain(names, n => n.Contains("refund", StringComparison.OrdinalIgnoreCase) || n.Contains("price", StringComparison.OrdinalIgnoreCase) || n.Contains("discount", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("cannot change a price", s["instructions"]!.GetValue<string>());
    }

    // ---------- the staff report ----------

    [Fact]
    public async Task The_staff_report_compares_full_and_mini_and_needs_a_staff_session()
    {
        using var s = new StaffKit(VoiceFactory.Config());
        Assert.NotEqual(HttpStatusCode.OK, (await s.Get("/api/v1/staff/voice/report", null)).StatusCode);
        var now = s.F.Clock.GetUtcNow();
        await s.Db(async db =>
        {
            VoiceCall C(string tier, string outcome, int secs, long cost, int samples, long latency) => new()
            {
                Id = Guid.NewGuid(), CallSid = Guid.NewGuid().ToString("N"), CallerMasked = "***1111", Model = "m-" + tier, Tier = tier, StartedAt = now.AddHours(-1), EndedAt = now, DurationSeconds = secs,
                EstimatedCostMicros = cost, LatencySamples = samples, LatencyTotalMs = latency, Outcome = outcome,
            };
            db.VoiceCalls.AddRange(C("full", VoiceOutcome.OrderPlaced, 120, 600_000, 2, 1400), C("full", VoiceOutcome.Transferred, 60, 300_000, 1, 900), C("full", VoiceOutcome.Abandoned, 60, 300_000, 1, 700), C("full", VoiceOutcome.OrderPlaced, 60, 300_000, 0, 0),
                C("mini", VoiceOutcome.OrderPlaced, 120, 120_000, 2, 800),
                new VoiceCall { Id = Guid.NewGuid(), CallSid = "old", CallerMasked = "***2", Model = "m-full", Tier = "full", StartedAt = now.AddDays(-90), EndedAt = now.AddDays(-90), DurationSeconds = 600, EstimatedCostMicros = 9_000_000, Outcome = VoiceOutcome.Abandoned });
            await db.SaveChangesAsync();
            return 0;
        });
        var (token, _, _) = await s.Admin();
        var r = await s.Get("/api/v1/staff/voice/report?days=30", token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
        var full = j["tiers"]!.AsArray().Single(t => t!["tier"]!.GetValue<string>() == "full")!;
        var mini = j["tiers"]!.AsArray().Single(t => t!["tier"]!.GetValue<string>() == "mini")!;
        Assert.Equal(4, full["calls"]!.GetValue<int>());                                         // the 90-day-old call is outside the window
        Assert.Equal(1_500_000L * 60 / 300, full["costPerMinuteMicros"]!.GetValue<long>());       // 1.5 USD over 5 minutes
        Assert.Equal(3000 / 4, full["averageLatencyMs"]!.GetValue<long>());
        Assert.Equal(50.0, full["orderCompletionPercent"]!.GetValue<double>());
        Assert.Equal(25.0, full["transferPercent"]!.GetValue<double>());
        Assert.Equal(120_000L * 60 / 120, mini["costPerMinuteMicros"]!.GetValue<long>());
        Assert.Equal(100.0, mini["orderCompletionPercent"]!.GetValue<double>());
        Assert.DoesNotContain("***1111", await r.Content.ReadAsStringAsync());                   // no caller data in the report
    }

    [Fact]
    public async Task Old_call_rows_are_purged_by_retention()
    {
        using var f = VoiceFactory.Create(new FakeRealtime(), new() { ["Voice:CallRetentionDays"] = "30" });
        var c = f.CreateClient();
        await VoiceFactory.Answer(c, "CA-old");
        f.Clock.Advance(TimeSpan.FromDays(20));
        await VoiceFactory.Answer(c, "CA-new");
        f.Clock.Advance(TimeSpan.FromDays(15));
        Assert.Equal(1, await f.Services.GetRequiredService<VoiceCallStore>().PurgeAsync(CancellationToken.None));
        Assert.Equal("CA-new", (await Call(f)).CallSid);
    }
}

internal static class FakeRealtimeExt
{
    /// <summary>The model session drops.</summary>
    public static async Task Dispose2(this FakeRealtime fake)
    {
        await fake.CloseAsync();
    }
}
