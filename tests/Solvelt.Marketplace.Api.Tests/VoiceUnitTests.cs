using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The pure parts of the voice module (decision V-01): signature, stream token, model switch, cost, masking, hours, configuration.</summary>
public class VoiceUnitTests
{
    private const string Url = "https://voice.test/api/v1/voice/signalwire/inbound";
    private static readonly Dictionary<string, string> Form = new() { ["CallSid"] = "CA123", ["From"] = "+15551234567", ["To"] = "+15559876543" };

    [Fact]
    public void Signature_verifies_and_every_change_breaks_it()
    {
        var sig = SignalWireSignature.Compute("key-1", Url, Form);
        Assert.True(SignalWireSignature.Verify("key-1", Url, Form, sig));
        Assert.True(SignalWireSignature.Verify("key-1", Url, Form.Reverse().ToDictionary(k => k.Key, k => k.Value), sig));   // parameter order does not matter
        Assert.False(SignalWireSignature.Verify("key-2", Url, Form, sig));
        Assert.False(SignalWireSignature.Verify("key-1", Url + "?x=1", Form, sig));
        Assert.False(SignalWireSignature.Verify("key-1", Url, new Dictionary<string, string>(Form) { ["From"] = "+15550000000" }, sig));
        Assert.False(SignalWireSignature.Verify("key-1", Url, Form, null));
        Assert.False(SignalWireSignature.Verify("key-1", Url, Form, ""));
        Assert.False(SignalWireSignature.Verify("", Url, Form, sig));
    }

    private static (StreamTokens T, FakeTimeProvider Clock) Tokens(string key = "k-0123456789abcdef0123456789abcdef", int seconds = 120)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        return (new StreamTokens(Options.Create(new VoiceOptions { StreamTokenKey = key, StreamTokenSeconds = seconds }), clock), clock);
    }

    [Fact]
    public void Stream_token_names_one_call_and_expires()
    {
        var (t, clock) = Tokens();
        var id = Guid.NewGuid();
        var token = t.Issue(id);
        Assert.Equal(id, t.Validate(token));
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(id, t.Validate(token));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(t.Validate(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("a.b.c")]
    [InlineData("a.b.c.d")]
    public void Stream_token_garbage_is_refused(string? token) => Assert.Null(Tokens().T.Validate(token));

    [Fact]
    public void Stream_token_forgeries_are_refused()
    {
        var (t, _) = Tokens();
        var (other, _) = Tokens("another-key-0123456789abcdef012345");
        var id = Guid.NewGuid();
        var token = t.Issue(id);
        Assert.Null(other.Validate(token));                                                        // signed by another key
        var parts = token.Split('.');
        Assert.Null(t.Validate($"{Guid.NewGuid():N}.{parts[1]}.{parts[2]}.{parts[3]}"));           // another call id
        Assert.Null(t.Validate($"{parts[0]}.{long.Parse(parts[1]) + 3600}.{parts[2]}.{parts[3]}")); // a later expiry
        Assert.Null(t.Validate($"{parts[0]}.{parts[1]}.0000000000000000.{parts[3]}"));             // another nonce
        Assert.Null(t.Validate(token + "0"));
    }

    [Fact]
    public void A_token_is_unique_each_time_and_opens_one_stream_once()
    {
        var (t, clock) = Tokens();
        var id = Guid.NewGuid();
        var a = t.Issue(id); var b = t.Issue(id);
        Assert.NotEqual(a, b);                                       // same call, same second: still two different tokens
        Assert.Equal(id, t.Use(a));
        Assert.Null(t.Use(a));                                       // replay refused
        Assert.Equal(id, t.Use(b));                                  // the next token (a keypad resume) is fine
        Assert.Null(t.Use("garbage"));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(t.Use(t.Issue(id).Replace(id.ToString("N"), Guid.NewGuid().ToString("N"))));   // a forged id never spends anything
        Assert.Equal(id, t.Use(t.Issue(id)));
    }

    [Fact]
    public void Model_modes_pick_the_configured_model_and_log_the_tier()
    {
        var id = Guid.NewGuid();
        var o = new VoiceOptions { OpenAiModel = "big", OpenAiModelMini = "small", ModelMode = "full" };
        Assert.Equal(("big", "full"), VoiceModelSelector.Pick(o, id));
        o.ModelMode = "mini";
        Assert.Equal(("small", "mini"), VoiceModelSelector.Pick(o, id));
        o.ModelMode = "split"; o.MiniPercent = 0;
        Assert.All(Enumerable.Range(0, 200), _ => Assert.Equal("full", VoiceModelSelector.Pick(o, Guid.NewGuid()).Tier));
        o.MiniPercent = 100;
        Assert.All(Enumerable.Range(0, 200), _ => Assert.Equal("mini", VoiceModelSelector.Pick(o, Guid.NewGuid()).Tier));
    }

    [Fact]
    public void Split_mode_sends_about_the_configured_share_to_mini_and_a_call_keeps_its_model()
    {
        var o = new VoiceOptions { ModelMode = "split", MiniPercent = 30 };
        var picks = Enumerable.Range(0, 4000).Select(_ => Guid.NewGuid()).Select(g => (g, VoiceModelSelector.Pick(o, g).Tier)).ToList();
        var share = picks.Count(p => p.Item2 == "mini") / 4000.0;
        Assert.InRange(share, 0.26, 0.34);
        Assert.All(picks.Take(50), p => Assert.Equal(p.Item2, VoiceModelSelector.Pick(o, p.g).Tier));
    }

    [Fact]
    public void Cost_is_estimated_in_whole_micro_dollars_from_the_configured_prices()
    {
        var prices = new VoicePrices { AudioIn = 32m, AudioOut = 64m, TextIn = 4m, TextOut = 16m };   // USD per million tokens
        var u = new VoiceUsage(1000, 500, 200, 100);
        Assert.Equal(1000 * 32 + 500 * 64 + 200 * 4 + 100 * 16, u.EstimateMicros(prices));
        Assert.Equal(0, u.EstimateMicros(new VoicePrices()));
        Assert.Equal(new VoiceUsage(2000, 1000, 400, 200), u.Add(u));
    }

    [Theory]
    [InlineData("+15551234567", "***4567")]
    [InlineData("(555) 123-4567", "***4567")]
    [InlineData("12", "***")]
    [InlineData(null, "***")]
    public void Phone_numbers_are_masked_to_the_last_four_digits(string? n, string expected) => Assert.Equal(expected, PhoneMask.Mask(n));

    [Theory]
    [InlineData("", "2026-11-17T03:00:00Z", true)]                       // no hours: always
    [InlineData("09:00-17:00", "2026-11-17T15:00:00Z", true)]            // 10:00 in New York (winter)
    [InlineData("09:00-17:00", "2026-11-17T13:59:00Z", false)]           // 08:59
    [InlineData("09:00-17:00", "2026-11-17T22:00:00Z", false)]           // 17:00 is closed
    [InlineData("Mon-Fri 09:00-17:00", "2026-11-17T15:00:00Z", true)]    // a Tuesday
    [InlineData("Mon-Fri 09:00-17:00", "2026-11-21T15:00:00Z", false)]   // a Saturday
    [InlineData("Sun 09:00-17:00", "2026-11-22T15:00:00Z", true)]
    [InlineData("nonsense", "2026-11-17T15:00:00Z", false)]              // fails closed
    public void Support_hours(string hours, string at, bool open)
        => Assert.Equal(open, SupportHours.IsOpen(new VoiceOptions { SupportHours = hours }, DateTimeOffset.Parse(at)));

    private static IReadOnlyList<string> Problems(Dictionary<string, string?> cfg, string env = "Production")
        => StartupValidator.VoiceProblems(new ConfigurationBuilder().AddInMemoryCollection(cfg).Build(), new FakeEnv(env));

    private sealed class FakeEnv(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public void Voice_is_off_by_default_and_needs_its_settings_when_on()
    {
        Assert.Empty(Problems([]));
        var on = new Dictionary<string, string?> { ["Voice:Enabled"] = "true" };
        var p = Problems(on);
        Assert.Contains(p, x => x.Contains("Voice:OpenAiApiKey")); Assert.Contains(p, x => x.Contains("SignalWire:SigningKey"));
        Assert.Contains(p, x => x.Contains("Voice:StreamTokenKey")); Assert.Contains(p, x => x.Contains("Voice:PublicBaseUrl"));
        var good = new Dictionary<string, string?>
        {
            ["Voice:Enabled"] = "true", ["Voice:OpenAiApiKey"] = "x", ["SignalWire:SigningKey"] = "sw-signing-key", ["Voice:StreamTokenKey"] = new string('k', 40), ["Voice:PhoneCodeKey"] = new string('c', 40), ["Voice:CallerHashKey"] = new string('h', 40),
            ["Voice:PublicBaseUrl"] = "https://api.example.com", ["SignalWire:ProjectId"] = "p", ["SignalWire:ApiToken"] = "t", ["SignalWire:Space"] = "s", ["SignalWire:FromNumber"] = "+15550002222",
        };
        Assert.Empty(Problems(good));
        Assert.Contains(Problems(new(good) { ["Voice:StreamTokenKey"] = new string('s', 40), ["SignalWire:SigningKey"] = new string('s', 40) }), x => x.Contains("must differ"));   // one key per purpose
        Assert.Contains(Problems(new(good) { ["Voice:PublicBaseUrl"] = "http://api.example.com" }), x => x.Contains("PublicBaseUrl"));
        Assert.Contains(Problems(new(good) { ["Voice:PhoneCodeKey"] = new string('k', 40) }), x => x.Contains("PhoneCodeKey must differ"));          // not the stream token key
        Assert.Contains(Problems(new(good) { ["Voice:PhoneCodeKey"] = "short" }), x => x.Contains("PhoneCodeKey"));
        Assert.Contains(Problems(new(good) { ["Voice:CallerHashKey"] = new string('c', 40) }), x => x.Contains("CallerHashKey must differ"));              // one key per purpose
        Assert.Contains(Problems(new(good) { ["Voice:CallerHashKey"] = null }), x => x.Contains("CallerHashKey"));
        Assert.Contains(Problems(new(good) { ["Voice:PhonePaymentProvider"] = "fake" }), x => x.Contains("development and tests only"));             // no fake money in production
        Assert.Contains(Problems(new(good) { ["Voice:PhonePaymentProvider"] = "stripe-link" }), x => x.Contains("none, fake or sola"));
        Assert.Contains(Problems(new(good) { ["Voice:PhonePaymentProvider"] = "sola" }), x => x.Contains("development and tests only"));             // nothing real moves money: the Sola sandbox is for development too
        Assert.Contains(Problems(new(good) { ["Voice:PhonePaymentProvider"] = "sola" }, "Development"), x => x.Contains("Sola:WebhookPin"));         // and it needs its own settings
        Assert.DoesNotContain(Problems(new(good) { ["Voice:PhonePaymentProvider"] = "fake" }, "Development"), x => x.Contains("PhonePaymentProvider"));
        Assert.Contains(Problems(new(good) { ["SignalWire:ApiToken"] = null }), x => x.Contains("login codes are sent by SMS"));                   // production needs the messaging settings
        // a scripted upstream and an SMS sink exist for development and the journey only: production refuses both
        Assert.Contains(Problems(new(good) { ["Voice:OpenAiRealtimeUrl"] = "ws://127.0.0.1:5091/v1/realtime" }), x => x.Contains("OpenAiRealtimeUrl must be OpenAI's"));
        Assert.Contains(Problems(new(good) { ["Voice:OpenAiRealtimeUrl"] = "wss://api.openai.com.evil.example/v1/realtime" }), x => x.Contains("OpenAiRealtimeUrl must be OpenAI's"));
        Assert.DoesNotContain(Problems(new(good) { ["Voice:OpenAiRealtimeUrl"] = "wss://api.openai.com/v1/realtime" }), x => x.Contains("OpenAiRealtimeUrl"));
        Assert.DoesNotContain(Problems(new(good) { ["Voice:OpenAiRealtimeUrl"] = "ws://127.0.0.1:5091/v1/realtime" }, "Development"), x => x.Contains("OpenAiRealtimeUrl"));
        Assert.Contains(Problems(new(good) { ["SignalWire:ApiBaseUrl"] = "http://127.0.0.1:5092" }), x => x.Contains("SignalWire:ApiBaseUrl is for development"));
        Assert.DoesNotContain(Problems(new(good) { ["SignalWire:ApiBaseUrl"] = "http://127.0.0.1:5092" }, "Development"), x => x.Contains("ApiBaseUrl"));
        Assert.Contains(Problems(new(good) { ["SignalWire:ApiBaseUrl"] = "not a url" }, "Development"), x => x.Contains("absolute http(s) URL"));
        // the off-topic strike limit (V2): 1 to 10, anything else is refused at start-up
        foreach (var bad in new[] { "0", "11", "-1", "100" }) Assert.Contains(Problems(new(good) { ["Voice:MaxOffTopicTurns"] = bad }), x => x.Contains("Voice:MaxOffTopicTurns must be between 1 and 10"));
        foreach (var fine in new[] { "1", "3", "10" }) Assert.DoesNotContain(Problems(new(good) { ["Voice:MaxOffTopicTurns"] = fine }), x => x.Contains("MaxOffTopicTurns"));
        Assert.DoesNotContain(Problems(good), x => x.Contains("MaxOffTopicTurns"));                                                                  // the default (3) is fine
        Assert.Contains(Problems(new(good) { ["Voice:ModelMode"] = "turbo" }), x => x.Contains("ModelMode"));
        Assert.Contains(Problems(new(good) { ["Voice:MiniPercent"] = "101" }), x => x.Contains("MiniPercent"));
    }

    [Fact]
    public void Usage_is_read_from_the_realtime_response_usage_block()
    {
        using var d = System.Text.Json.JsonDocument.Parse("""{"input_token_details":{"audio_tokens":120,"text_tokens":30,"cached_tokens":5},"output_token_details":{"audio_tokens":80,"text_tokens":9}}""");
        Assert.Equal(new VoiceUsage(120, 80, 30, 9), RealtimeBridge.ReadUsage(d.RootElement));
        using var empty = System.Text.Json.JsonDocument.Parse("{}");
        Assert.Equal(VoiceUsage.Zero, RealtimeBridge.ReadUsage(empty.RootElement));
    }
}
