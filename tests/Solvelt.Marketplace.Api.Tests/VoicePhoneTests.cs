using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Phone numbers, the SMS sender and the code service, without a call around them (decision V-03).</summary>
public class VoicePhoneTests
{
    [Theory]
    [InlineData("(555) 123-4567", "+15551234567")]
    [InlineData("555.123.4567", "+15551234567")]
    [InlineData("1 555 123 4567", "+15551234567")]
    [InlineData("+1 (555) 123-4567", "+15551234567")]
    [InlineData("+44 20 7946 0958", "+442079460958")]
    [InlineData("5551234567", "+15551234567")]
    [InlineData("12345", null)]
    [InlineData("555-1234", null)]
    [InlineData("+123", null)]
    [InlineData("anonymous", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("25551234567", null)]
    public void Numbers_are_normalised_to_e164_or_refused(string? raw, string? expected) => Assert.Equal(expected, PhoneNumbers.ToE164(raw));

    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private static SignalWireSmsSender Sender(Handler h, SignalWireOptions? o = null, ILogger<SignalWireSmsSender>? log = null)
        => new(new HttpClient(h), Options.Create(o ?? new SignalWireOptions { ProjectId = "proj-1", ApiToken = "tok-secret", Space = "acme", FromNumber = "+15550002222" }), log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SignalWireSmsSender>.Instance);

    [Fact]
    public async Task The_sender_calls_signalwires_messaging_api_as_documented()
    {
        var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.True(await Sender(h).SendAsync("+15551234567", "Solvelt code: 123456.", CancellationToken.None));
        var (req, body) = Assert.Single(h.Calls);
        Assert.Equal("https://acme.signalwire.com/api/messaging/messages", req.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("proj-1:tok-secret")), req.Headers.Authorization!.ToString());
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(("+15551234567", "+15550002222", "Solvelt code: 123456."), (doc.RootElement.GetProperty("to").GetString(), doc.RootElement.GetProperty("from").GetString(), doc.RootElement.GetProperty("body").GetString()));
    }

    [Theory]
    [InlineData(null, "tok", "acme", "+1555")]
    [InlineData("p", null, "acme", "+1555")]
    [InlineData("p", "tok", null, "+1555")]
    [InlineData("p", "tok", "acme", null)]
    public async Task The_sender_without_its_settings_sends_nothing(string? project, string? token, string? space, string? from)
    {
        var h = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.False(await Sender(h, new SignalWireOptions { ProjectId = project, ApiToken = token, Space = space, FromNumber = from }).SendAsync("+15551234567", "x", CancellationToken.None));
        Assert.Empty(h.Calls);
    }

    private sealed class CaptureLog : ILogger<SignalWireSmsSender>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception) + (exception?.ToString() ?? ""));
    }

    [Fact]
    public async Task A_refusal_or_an_outage_is_a_false_and_the_log_holds_no_code_number_or_secret()
    {
        var log = new CaptureLog();
        Assert.False(await Sender(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("to +15551234567 body Solvelt code: 654321") }), null, log).SendAsync("+15551234567", "Solvelt code: 654321.", CancellationToken.None));
        Assert.False(await Sender(new Handler((_, _) => throw new HttpRequestException("down +15551234567 tok-secret")), null, log).SendAsync("+15551234567", "Solvelt code: 654321.", CancellationToken.None));
        Assert.Equal(2, log.Lines.Count);
        Assert.All(log.Lines, l => { Assert.DoesNotContain("654321", l); Assert.DoesNotContain("5551234567", l); Assert.DoesNotContain("tok-secret", l); });
    }

    // ---------- the code service ----------

    private static (PhoneCodeService Svc, InMemorySmsSender Sms, FakeTimeProvider Clock) Codes(Action<VoiceOptions>? tweak = null)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var o = new VoiceOptions { PhoneCodeKey = new string('p', 40) };
        tweak?.Invoke(o);
        var sms = new InMemorySmsSender();
        return (new PhoneCodeService(sms, Options.Create(o), new AttemptLimiter(clock), clock), sms, clock);
    }

    private static string CodeOf(InMemorySmsSender sms) => System.Text.RegularExpressions.Regex.Match(sms.Sent[^1].Body, @"\b(\d{6})\b").Groups[1].Value;

    [Fact]
    public async Task A_code_is_six_digits_hashed_in_memory_valid_once_and_consumed_by_the_right_digits()
    {
        var (svc, sms, _) = Codes();
        var state = new VoiceCallState(Guid.NewGuid());
        Assert.Equal(SendResult.Sent, await svc.SendAsync(state, "+15551234567", "guest", null, CancellationToken.None));
        var code = CodeOf(sms);
        Assert.Matches(@"^\d{6}$", code);
        Assert.DoesNotContain(code, Convert.ToHexString(state.Challenge!.Hash));      // only a hash is kept
        Assert.Equal(CodeResult.Ok, svc.Verify(state, code));
        Assert.Null(state.Challenge);
        Assert.Equal(CodeResult.NoChallenge, svc.Verify(state, code));                // used up
    }

    [Fact]
    public async Task A_code_is_bound_to_its_call_and_its_number()
    {
        var (svc, sms, _) = Codes();
        var a = new VoiceCallState(Guid.NewGuid()); var b = new VoiceCallState(Guid.NewGuid());
        await svc.SendAsync(a, "+15551234567", "guest", null, CancellationToken.None);
        var code = CodeOf(sms);
        Assert.NotEqual(Convert.ToHexString(svc.Hash(a.CallId, "+15551234567", code)), Convert.ToHexString(svc.Hash(b.CallId, "+15551234567", code)));   // another call
        Assert.NotEqual(Convert.ToHexString(svc.Hash(a.CallId, "+15551234567", code)), Convert.ToHexString(svc.Hash(a.CallId, "+15559999999", code)));   // another number
        Assert.Equal(CodeResult.NoChallenge, svc.Verify(b, code));
    }

    [Fact]
    public async Task Five_tries_then_locked_even_for_the_right_code_and_ten_minutes_then_expired()
    {
        var (svc, sms, clock) = Codes();
        var state = new VoiceCallState(Guid.NewGuid());
        await svc.SendAsync(state, "+15551234567", "guest", null, CancellationToken.None);
        var code = CodeOf(sms); var wrong = code == "000000" ? "111111" : "000000";
        for (var i = 0; i < 4; i++) Assert.Equal(CodeResult.Wrong, svc.Verify(state, wrong));
        Assert.Equal(CodeResult.Locked, svc.Verify(state, wrong));
        Assert.Equal(CodeResult.Locked, svc.Verify(state, code));                      // the right one is too late
        var s2 = new VoiceCallState(Guid.NewGuid());
        await svc.SendAsync(s2, "+15550001111", "guest", null, CancellationToken.None);
        var c2 = CodeOf(sms);
        clock.Advance(PhoneCodeService.Lifetime - TimeSpan.FromSeconds(1));
        Assert.Equal(CodeResult.Ok, svc.Verify(s2, c2));
        var s3 = new VoiceCallState(Guid.NewGuid());
        await svc.SendAsync(s3, "+15550002222", "guest", null, CancellationToken.None);
        var c3 = CodeOf(sms);
        clock.Advance(PhoneCodeService.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(CodeResult.Expired, svc.Verify(s3, c3));
    }

    [Fact]
    public async Task The_global_hourly_cap_stops_a_flood_across_many_numbers()
    {
        var (svc, sms, _) = Codes(o => o.MaxSmsPerHour = 5);
        var sent = 0;
        for (var i = 0; i < 10; i++)
            if (await svc.SendAsync(new VoiceCallState(Guid.NewGuid()), $"+1555000{i:D4}", "guest", null, CancellationToken.None) == SendResult.Sent) sent++;
        Assert.Equal(5, sent);
        Assert.Equal(5, sms.Sent.Count);
    }

    [Fact]
    public async Task A_failed_send_leaves_no_challenge_to_guess_at()
    {
        var (svc, sms, _) = Codes();
        sms.Fail = true;
        var state = new VoiceCallState(Guid.NewGuid());
        Assert.Equal(SendResult.Failed, await svc.SendAsync(state, "+15551234567", "guest", null, CancellationToken.None));
        Assert.Null(state.Challenge);
        Assert.Equal(CodeResult.NoChallenge, svc.Verify(state, "123456"));
    }

    [Fact]
    public void The_message_never_asks_to_say_the_code_and_no_code_is_ever_spoken_by_a_script()
    {
        Assert.DoesNotContain("say", VoiceScripts.AskCode, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("type", VoiceScripts.AskCode);
        Assert.Contains("keypad", VoiceScripts.Instructions);
        Assert.Contains("never ask for, repeat or accept", VoiceScripts.Instructions, StringComparison.OrdinalIgnoreCase);
    }
}
