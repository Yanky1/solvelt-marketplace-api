using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Voice;

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

/// <summary>
/// Which buyer account, if any, has PROVEN that it holds a phone number (decision V-05). The profile phone is free text nobody checked: a typo, or a number that was reassigned, would let whoever
/// holds it now pass the SMS code and become that account, so it is never used. Only <c>PhoneVerifiedE164</c>, set by a code typed in a session already signed in as the account, counts; at most one
/// account holds a number. Deleted, staff-locked, demo-verified and unverified-email accounts are never recognised. The one account that has no verified email and IS recognised is the phone-only account (no email, no password: slice 6, V-09): there is no mailbox for anyone to claim, so the number is its only credential.
/// </summary>
public sealed class PhoneIdentity(IServiceScopeFactory scopes)
{
    public sealed record Match(Guid AccountId, string FirstName);

    public async Task<Match?> FindAsync(string e164, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var hit = await db.BuyerAccounts.AsNoTracking()
            .Where(a => a.PhoneVerifiedE164 == e164 && a.DeletedAt == null && a.StaffLockedAt == null && (a.EmailVerifiedAt != null || (a.Email == null && a.PasswordHash == null)) && a.DemoVerifiedAt == null)
            .Select(a => new { a.Id, a.DisplayName }).FirstOrDefaultAsync(ct);
        return hit is null ? null : new Match(hit.Id, hit.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");
    }
}

/// <summary>
/// Records that an account has proven a number. Called ONLY after an SMS code was typed in a context already signed in as that account (slice 6's phone account setup; a web settings step is the
/// follow-up): it takes the number over from any other account (the code proves who holds it now) in one transaction, and refuses a number the account's own profile does not carry.
/// </summary>
public sealed class AccountPhoneVerifier(MarketplaceDb db, TimeProvider time)
{
    public async Task<bool> MarkVerifiedAsync(Guid accountId, string e164, CancellationToken ct)
    {
        var profile = await db.BuyerAccounts.AsNoTracking().Where(a => a.Id == accountId && a.DeletedAt == null).Select(a => a.Phone).FirstOrDefaultAsync(ct);
        if (PhoneNumbers.ToE164(profile) != e164) return false;   // the proof is for the number the profile carries
        var now = time.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.BuyerAccounts.Where(a => a.PhoneVerifiedE164 == e164 && a.Id != accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.PhoneVerifiedE164, (string?)null).SetProperty(a => a.PhoneVerifiedAt, (DateTimeOffset?)null), ct);
        var rows = await db.BuyerAccounts.Where(a => a.Id == accountId && a.DeletedAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.PhoneVerifiedE164, e164).SetProperty(a => a.PhoneVerifiedAt, now), ct);
        await tx.CommitAsync(ct);
        return rows == 1;
    }
}
