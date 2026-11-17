using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Voice;

public static class PhoneNumbers
{
    /// <summary>E.164 for a number as people write it. Ten digits, or eleven starting with 1, are US/Canada (our default market; the code takes no other assumption); anything with a leading + keeps its country. Null when it is not a phone number.</summary>
    public static string? ToE164(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var plus = raw.TrimStart().StartsWith('+');
        var d = new string(raw.Where(char.IsDigit).ToArray());
        if (plus) return d.Length is >= 8 and <= 15 ? "+" + d : null;
        if (d.Length == 10) return "+1" + d;
        if (d.Length == 11 && d[0] == '1') return "+" + d;
        return null;
    }
}

/// <summary>Sends a text message. A seam: tests and development use <see cref="InMemorySmsSender"/>; production uses SignalWire's messaging API.</summary>
public interface ISmsSender
{
    /// <summary>True when the provider accepted the message for delivery.</summary>
    Task<bool> SendAsync(string toE164, string body, CancellationToken ct);
}

/// <summary>Keeps what it was asked to send, in memory (development and tests). Never logs a body.</summary>
public sealed class InMemorySmsSender : ISmsSender
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string To, string Body)> _sent = new();
    public IReadOnlyList<(string To, string Body)> Sent => _sent.ToArray();
    public bool Fail { get; set; }
    public Task<bool> SendAsync(string toE164, string body, CancellationToken ct) { if (Fail) return Task.FromResult(false); _sent.Enqueue((toE164, body)); return Task.FromResult(true); }
}

/// <summary>
/// SignalWire REST: <c>POST https://{Space}.signalwire.com/api/messaging/messages</c>, JSON <c>{to, from, body}</c>, basic auth with the project id and API token
/// (https://signalwire.com/docs/apis/rest/messages/create-message.md, read 2026-11-17). The body holds a login code, so nothing here logs it; failures log a status code only.
/// </summary>
public sealed class SignalWireSmsSender(HttpClient http, IOptions<SignalWireOptions> options, ILogger<SignalWireSmsSender> log) : ISmsSender
{
    public async Task<bool> SendAsync(string toE164, string body, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Space) || string.IsNullOrWhiteSpace(o.ProjectId) || string.IsNullOrWhiteSpace(o.ApiToken) || string.IsNullOrWhiteSpace(o.FromNumber)) return false;
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{o.Space}.signalwire.com/api/messaging/messages") { Content = JsonContent.Create(new { to = toE164, from = o.FromNumber, body }) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(o.ProjectId + ":" + o.ApiToken)));
        try
        {
            using var r = await http.SendAsync(req, ct);
            if (!r.IsSuccessStatusCode) log.LogWarning("SMS provider refused a message ({Status}).", (int)r.StatusCode);
            return r.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { log.LogWarning("SMS provider could not be reached."); return false; }
    }
}

public sealed class PhoneChallenge
{
    public required string Phone { get; init; }
    public required byte[] Hash { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public int Attempts { get; set; }
    /// <summary><c>recognise</c> (an account found by this number) or <c>guest</c>.</summary>
    public required string Purpose { get; init; }
    public Guid? AccountId { get; init; }
}

public enum CodeResult { Ok, Wrong, Locked, Expired, NoChallenge }
public enum SendResult { Sent, TooMany, Failed }

/// <summary>
/// SMS codes for the keypad (decision V-03). A code is six random digits, hashed with its own key together with the call and the number (a code for one call or number is useless
/// for another), kept only as a hash in the call's memory state, valid ten minutes, five tries. It is sent by SMS and typed on the keypad: never spoken, never given to the model,
/// never logged. Sends are limited per number, per call and in total, so the endpoint cannot be used to text strangers.
/// </summary>
public sealed class PhoneCodeService(ISmsSender sms, IOptions<VoiceOptions> options, AttemptLimiter limiter, TimeProvider time)
{
    public const int Digits = 6, MaxAttempts = 5, MaxSendsPerCall = 3;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private byte[] Key => Encoding.UTF8.GetBytes(options.Value.PhoneCodeKey ?? throw new InvalidOperationException("Voice:PhoneCodeKey is not set."));

    internal byte[] Hash(Guid callId, string phone, string code)
    {
        using var h = new HMACSHA256(Key);
        return h.ComputeHash(Encoding.UTF8.GetBytes($"voice-phone-code-v1|{callId:N}|{phone}|{code}"));
    }

    public async Task<SendResult> SendAsync(VoiceCallState state, string phone, string purpose, Guid? accountId, CancellationToken ct)
    {
        var o = options.Value;
        if (!state.TryCountSms(MaxSendsPerCall)) return SendResult.TooMany;
        if (!limiter.TryAcquire("voice-sms-number", phone, Math.Max(1, o.MaxSmsPerNumberPerHour), TimeSpan.FromHours(1))) return SendResult.TooMany;
        if (!limiter.TryAcquire("voice-sms-all", "all", Math.Max(1, o.MaxSmsPerHour), TimeSpan.FromHours(1))) return SendResult.TooMany;
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var challenge = new PhoneChallenge { Phone = phone, Hash = Hash(state.CallId, phone, code), ExpiresAt = time.GetUtcNow().Add(Lifetime), Purpose = purpose, AccountId = accountId };
        if (!await sms.SendAsync(phone, $"Solvelt code: {code}. It expires in 10 minutes. We never ask you to say it aloud.", ct)) return SendResult.Failed;
        state.SetChallenge(challenge);
        return SendResult.Sent;
    }

    /// <summary>Checks the digits typed on the keypad. A right code consumes the challenge; a wrong one counts, and the fifth wrong one locks it.</summary>
    public CodeResult Verify(VoiceCallState state, string? typed)
    {
        var c = state.Challenge;
        if (c is null) return CodeResult.NoChallenge;
        if (time.GetUtcNow() > c.ExpiresAt) { state.SetChallenge(null); return CodeResult.Expired; }
        if (c.Attempts >= MaxAttempts) return CodeResult.Locked;
        c.Attempts++;
        var digits = new string((typed ?? "").Where(char.IsDigit).ToArray());
        var ok = digits.Length == Digits && CryptographicOperations.FixedTimeEquals(Hash(state.CallId, c.Phone, digits), c.Hash);
        if (ok) { state.SetChallenge(null); return CodeResult.Ok; }
        return c.Attempts >= MaxAttempts ? CodeResult.Locked : CodeResult.Wrong;
    }
}

/// <summary>Which buyer account, if any, owns a phone number. Never guesses: more than one match is no match.</summary>
public sealed class PhoneIdentity(IServiceScopeFactory scopes)
{
    public sealed record Match(Guid AccountId, string FirstName);

    public async Task<Match?> FindAsync(string e164, CancellationToken ct)
    {
        var last4 = e164[^4..];
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        // the profile phone is free text a buyer typed: narrow by its last four digits in SQL, then compare the normalised numbers
        var candidates = await db.BuyerAccounts.AsNoTracking()
            .Where(a => a.Phone != null && a.DeletedAt == null && a.StaffLockedAt == null && a.EmailVerifiedAt != null && a.Phone.Contains(last4)).Select(a => new { a.Id, a.DisplayName, a.Phone }).Take(50).ToListAsync(ct);
        var hits = candidates.Where(a => PhoneNumbers.ToE164(a.Phone) == e164).ToList();
        if (hits.Count != 1) return null;   // none, or several accounts claim this number: do not pick one
        return new Match(hits[0].Id, FirstWord(hits[0].DisplayName));
    }

    private static string FirstWord(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
}
