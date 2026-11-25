using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// The signed token of one callback (decision V-60): <c>id.expiry.mac</c>, the mac keyed with <c>Voice:StreamTokenKey</c> under its own label so a stream token can never pass for it. It travels in the query string of the three
/// URLs SignalWire is given for the call, and is checked together with SignalWire's own signature on every request. It is one-time in effect: the callback row moves <c>dialing</c> to <c>answered</c> exactly once.
/// </summary>
public sealed class PayCallbackTokens(IOptions<VoiceOptions> options)
{
    private byte[] Key => Encoding.UTF8.GetBytes(options.Value.StreamTokenKey ?? throw new InvalidOperationException("Voice:StreamTokenKey is not set."));

    private string Mac(string payload)
    {
        using var h = new HMACSHA256(Key);
        return Convert.ToHexStringLower(h.ComputeHash(Encoding.UTF8.GetBytes("voice-pay-callback-v1:" + payload)));
    }

    public string Issue(Guid id, DateTimeOffset expiresAt)
    {
        var payload = id.ToString("N") + "." + expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return payload + "." + Mac(payload);
    }

    /// <summary>The callback id when the token is genuine and not past its time; otherwise null.</summary>
    public Guid? Validate(string? token, DateTimeOffset now)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 3 } || token!.Length > 120) return null;
        if (!Guid.TryParseExact(parts[0], "N", out var id) || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var exp)) return null;
        var expected = Encoding.ASCII.GetBytes(Mac(parts[0] + "." + parts[1]));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(parts[2]))) return null;
        return exp >= now.ToUnixTimeSeconds() ? id : null;
    }
}

public sealed record DialRequest(string ToE164, string FromE164, string AnswerUrl, string StatusUrl, int RingSeconds);

/// <summary>The outcome of asking SignalWire to ring a number. <see cref="Code"/> is the provider's error code when it sent one: the only thing of its answer that is ever kept.</summary>
public sealed record DialResult(bool Ok, string? CallSid, int? HttpStatus, string? Code);

public interface IOutboundCaller
{
    Task<DialResult> PlaceAsync(DialRequest request, CancellationToken ct);
}

/// <summary>
/// SignalWire's create-a-call request (https://signalwire.com/docs/compatibility-api/rest/calls/create-a-call, read 2026-11-25): <c>POST {space}/api/laml/2010-04-01/Accounts/{ProjectId}/Calls</c>, form encoded, Basic auth
/// (project id and API token, the token needs the Voice scope). <c>To</c> and <c>From</c> are E.164, <c>Url</c> gives the cXML, <c>Timeout</c> is the ring time, <c>MachineDetection=Enable</c> asks for the synchronous
/// answering-machine detection, <c>Record</c> stays false. <c>StatusCallbackEvent</c> is NOT sent: its default (completed) is the one wanted, and how several values are written is not on the page. The answer's
/// <c>sid</c> is kept; on an error only the HTTP status and <c>code</c> are logged, never the body.
/// </summary>
public sealed class SignalWireOutboundCaller(HttpClient http, IOptions<SignalWireOptions> options, ILogger<SignalWireOutboundCaller> log) : IOutboundCaller
{
    public async Task<DialResult> PlaceAsync(DialRequest r, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Space) || string.IsNullOrWhiteSpace(o.ProjectId) || string.IsNullOrWhiteSpace(o.ApiToken)) return new(false, null, null, "not_configured");
        var baseUrl = string.IsNullOrWhiteSpace(o.ApiBaseUrl) ? $"https://{o.Space}.signalwire.com" : o.ApiBaseUrl.TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/laml/2010-04-01/Accounts/{Uri.EscapeDataString(o.ProjectId)}/Calls")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["To"] = r.ToE164, ["From"] = r.FromE164, ["Url"] = r.AnswerUrl, ["Method"] = "POST",
                ["StatusCallback"] = r.StatusUrl, ["StatusCallbackMethod"] = "POST",
                ["Timeout"] = r.RingSeconds.ToString(CultureInfo.InvariantCulture), ["MachineDetection"] = "Enable", ["Record"] = "false",
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(o.ProjectId + ":" + o.ApiToken)));
        try
        {
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            string? sid = null, code = null;
            try
            {
                using var d = JsonDocument.Parse(body);
                if (d.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (d.RootElement.TryGetProperty("sid", out var s) && s.ValueKind == JsonValueKind.String) sid = s.GetString();
                    if (d.RootElement.TryGetProperty("code", out var c)) code = c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString();
                }
            }
            catch (JsonException) { /* not JSON: nothing to read */ }
            if (code is { Length: > 20 }) code = code[..20];
            if (!res.IsSuccessStatusCode || string.IsNullOrWhiteSpace(sid) || sid.Length > 64)
            {
                log.LogWarning("The call provider refused a callback ({Status}, code {Code}).", (int)res.StatusCode, code ?? "none");
                return new(false, null, (int)res.StatusCode, code);
            }
            return new(true, sid, (int)res.StatusCode, null);
        }
        catch (HttpRequestException) { log.LogWarning("The call provider could not be reached for a callback."); return new(false, null, null, "unreachable"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { log.LogWarning("The call provider timed out on a callback."); return new(false, null, null, "timeout"); }
    }
}

/// <summary>Development and tests, and nothing else: remembers the requests and answers as told. No call is placed.</summary>
public sealed class FakeOutboundCaller : IOutboundCaller
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<DialRequest> _requests = new();
    public IReadOnlyList<DialRequest> Requests => _requests.ToArray();
    public Func<DialRequest, DialResult> Answer { get; set; } = _ => new(true, "CA" + Guid.NewGuid().ToString("N")[..20], 201, null);
    public Task<DialResult> PlaceAsync(DialRequest request, CancellationToken ct) { _requests.Enqueue(request); return Task.FromResult(Answer(request)); }
}

public enum ScheduleStatus { Scheduled, Unavailable, NeedVerification, NotFound, NotPayable, AlreadyScheduled, TooMany }

/// <summary>
/// The pay callback (decision V-60). Only for a caller who ASKED, for a placed, unpaid order that is theirs, and only to the number just verified in that same call: the model supplies an order and nothing else, so it
/// can never choose a number. One active callback per order (a unique index), 30 minutes, a handful per number per day. Every change is written to the callback's own event list (a fixed word, never a number).
/// </summary>
public sealed class PayCallbackService(MarketplaceDb db, IOptions<VoiceOptions> options, IOptions<SignalWireOptions> signalWire, PhonePayments payments, VoiceCallStore calls,
    PayCallbackTokens tokens, IOutboundCaller caller, TimeProvider time, ILogger<PayCallbackService> log)
{
    public const string AnswerPath = "/api/v1/voice/signalwire/pay-callback/answer", ConfirmPath = "/api/v1/voice/signalwire/pay-callback/confirm", StatusPath = "/api/v1/voice/signalwire/pay-callback/status";

    /// <summary>True only when every piece is there: the switch, a public address, the SignalWire call settings and a phone payment provider that takes the keypad.</summary>
    public bool IsAvailable()
    {
        var v = options.Value; var s = signalWire.Value;
        return v.Enabled && v.PayCallback.Enabled && !string.IsNullOrWhiteSpace(v.PublicBaseUrl) && !string.IsNullOrWhiteSpace(s.Space) && !string.IsNullOrWhiteSpace(s.ProjectId) && !string.IsNullOrWhiteSpace(s.ApiToken)
            && !string.IsNullOrWhiteSpace(s.FromNumber) && payments.Provider is { } p && p.Supports(PhonePaymentMethod.Keypad);
    }

    /// <summary>
    /// The unpaid order the caller means, and only if it is theirs: an account's own pending order, or (for a caller without an account) one whose payment was tried by phone from this same verified number. The buyer's order
    /// lists show only paid and placed orders, so a pending one cannot be picked from a list: with no number given the newest of theirs is taken. Null when there is none (or the number is not theirs: the same answer as one that does not exist).
    /// </summary>
    private async Task<(string Number, long TotalCents, string Status, string? Intent)?> FindOrderAsync(VoiceCallState s, string? requested, CancellationToken ct)
    {
        IQueryable<MarketplaceOrder> mine;
        if (OrderAccess.IsAccount(s)) { var account = s.AccountId!.Value; mine = db.Orders.AsNoTracking().Where(o => o.AccountId == account); }
        else
        {
            var key = calls.CallerKeyOf(s.VerifiedPhone);
            if (key is null) return null;
            mine = db.Orders.AsNoTracking().Where(o => o.AccountId == null && db.VoicePaymentAttempts.Any(a => a.OrderNumber == o.Number && db.VoiceCalls.Any(c => c.Id == a.CallId && c.CallerKey == key)));
        }
        if (requested is not null) mine = mine.Where(o => o.Number == requested);
        else mine = mine.Where(o => o.Status == OrderStatus.PendingPayment);
        var found = await mine.OrderByDescending(o => o.CreatedAt).Select(o => new { o.Number, o.TotalCents, o.Status, o.PaymentIntentId }).FirstOrDefaultAsync(ct);
        return found is null ? null : (found.Number, found.TotalCents, found.Status, found.PaymentIntentId);
    }

    public async Task<(ScheduleStatus Status, VoicePayCallback? Callback)> ScheduleAsync(VoiceCallState s, Guid requestedByCallId, string? requestedOrder, CancellationToken ct)
    {
        if (!IsAvailable()) return (ScheduleStatus.Unavailable, null);
        var phone = PhoneNumbers.ToE164(s.VerifiedPhone);
        if (!(OrderAccess.IsAccount(s) || OrderAccess.IsGuest(s)) || phone is null) return (ScheduleStatus.NeedVerification, null);
        if (await FindOrderAsync(s, requestedOrder, ct) is not { } order) return (ScheduleStatus.NotFound, null);
        var orderNumber = order.Number;
        if (order.Status != OrderStatus.PendingPayment || order.TotalCents <= 0 || order.Intent is null) return (ScheduleStatus.NotPayable, null);
        var now = time.GetUtcNow();
        await ExpireOldAsync(ct);
        var v = options.Value.PayCallback;
        if (await db.VoicePayCallbacks.CountAsync(c => c.Phone == phone && c.CreatedAt > now.AddHours(-24), ct) >= Math.Max(1, v.MaxPerNumberPerDay)) return (ScheduleStatus.TooMany, null);
        var row = new VoicePayCallback
        {
            Id = Guid.NewGuid(), OrderNumber = orderNumber, AccountId = OrderAccess.IsAccount(s) ? s.AccountId : null, Phone = phone, RequestedByCallId = requestedByCallId,
            CreatedAt = now, DueAt = now.AddSeconds(Math.Max(0, v.DialDelaySeconds)), ExpiresAt = now.AddMinutes(Math.Max(1, v.ExpiryMinutes)), Status = PayCallbackStatus.Scheduled,
        };
        db.VoicePayCallbacks.Add(row);
        AddEvent(row.Id, PayCallbackStatus.Scheduled, "asked_by_caller");
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)   // the unique index: another active callback for this order won
        {
            db.ChangeTracker.Clear();
            return (ScheduleStatus.AlreadyScheduled, null);
        }
        return (ScheduleStatus.Scheduled, row);
    }

    private void AddEvent(Guid callbackId, string status, string detail)
        => db.VoicePayCallbackEvents.Add(new VoicePayCallbackEvent { Id = Guid.NewGuid(), CallbackId = callbackId, At = time.GetUtcNow(), Status = status, Detail = detail.Length > 60 ? detail[..60] : detail });

    /// <summary>Moves a callback from one of <paramref name="from"/> to <paramref name="to"/> once; false when it was not in one of those states (a replay, a race, an old request).</summary>
    public async Task<bool> MoveAsync(Guid id, string[] from, string to, string detail, CancellationToken ct, string? callSid = null)
    {
        var now = time.GetUtcNow();
        var terminal = !PayCallbackStatus.IsActive(to);
        var q = db.VoicePayCallbacks.Where(c => c.Id == id && from.Contains(c.Status));
        var n = terminal
            ? await q.ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, to).SetProperty(c => c.EndedAt, (DateTimeOffset?)now), ct)
            : await q.ExecuteUpdateAsync(u => u.SetProperty(c => c.Status, to), ct);
        if (n > 0 && callSid is not null) await db.VoicePayCallbacks.Where(c => c.Id == id && c.CallSid == null).ExecuteUpdateAsync(u => u.SetProperty(c => c.CallSid, callSid), ct);
        if (n == 0) return false;
        AddEvent(id, to, detail);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The end of the grace: the token, the yes, the keypad payment and the end-of-call word all still work for a call that was answered inside the window until then.</summary>
    public DateTimeOffset GraceEnd(VoicePayCallback c) => c.ExpiresAt.AddMinutes(Math.Max(1, options.Value.PayCallback.GraceMinutes));

    /// <summary>
    /// Callbacks past their time stop being active (so the unique index frees the order, and a late request finds nothing to start). A callback not yet answered ends at <c>ExpiresAt</c>; one that was answered (the caller is
    /// saying yes or paying) is left alone until the grace has passed, and then is <c>paid</c> if the order is paid, otherwise <c>expired</c>.
    /// </summary>
    public async Task<int> ExpireOldAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var grace = now.AddMinutes(-Math.Max(1, options.Value.PayCallback.GraceMinutes));
        var rows = await db.VoicePayCallbacks.AsNoTracking().Where(c =>
            ((c.Status == PayCallbackStatus.Scheduled || c.Status == PayCallbackStatus.Dialing) && c.ExpiresAt < now)
            || ((c.Status == PayCallbackStatus.Answered || c.Status == PayCallbackStatus.Paying) && c.ExpiresAt < grace)).Select(c => new { c.Id, c.OrderNumber }).ToListAsync(ct);
        var n = 0;
        foreach (var r in rows)
        {
            var paid = await db.Orders.AsNoTracking().AnyAsync(o => o.Number == r.OrderNumber && o.Status == OrderStatus.Paid, ct);
            if (await MoveAsync(r.Id, PayCallbackStatus.Active, paid ? "paid" : "expired", paid ? "order_is_paid" : "time_ran_out", ct)) n++;
        }
        return n;
    }

    /// <summary>Rings every callback that is due. One failure never stops the others and nothing here throws.</summary>
    public async Task<int> DispatchDueAsync(CancellationToken ct)
    {
        if (!IsAvailable()) return 0;
        await ExpireOldAsync(ct);
        var now = time.GetUtcNow();
        var due = await db.VoicePayCallbacks.AsNoTracking().Where(c => c.Status == PayCallbackStatus.Scheduled && c.DueAt <= now).OrderBy(c => c.DueAt).Take(20).ToListAsync(ct);
        var placed = 0;
        foreach (var c in due)
        {
            var v = options.Value;
            if (c.ExpiresAt - now < TimeSpan.FromMinutes(Math.Max(1, v.PayCallback.MinMinutesLeftToDial)))
            {
                await MoveAsync(c.Id, [PayCallbackStatus.Scheduled], "expired", "too_late_to_dial", ct);   // it would be answered too late to finish: not placed
                continue;
            }
            if (!await MoveAsync(c.Id, [PayCallbackStatus.Scheduled], PayCallbackStatus.Dialing, "ringing_now", ct)) continue;   // claimed by someone else
            var token = tokens.Issue(c.Id, GraceEnd(c));   // the token outlives the window by the grace, for the steps of a call that was answered inside it
            var baseUrl = v.PublicBaseUrl!.TrimEnd('/');
            DialResult result;
            try
            {
                result = await caller.PlaceAsync(new DialRequest(c.Phone, signalWire.Value.FromNumber!, baseUrl + AnswerPath + "?t=" + token, baseUrl + StatusPath + "?t=" + token, v.PayCallback.RingSeconds), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { result = new(false, null, null, "exception"); }
            if (result.Ok)
            {
                await db.VoicePayCallbacks.Where(x => x.Id == c.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.CallSid, x => x.CallSid ?? result.CallSid).SetProperty(x => x.DialedAt, now), ct);
                AddEvent(c.Id, PayCallbackStatus.Dialing, "provider_accepted");
                await db.SaveChangesAsync(ct);
                placed++;
            }
            else
            {
                await MoveAsync(c.Id, [PayCallbackStatus.Dialing], "dial_failed", "provider_refused_" + (result.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? result.Code ?? "none"), ct);
                log.LogWarning("Pay callback {Callback} for order {Order} could not be placed.", c.Id, c.OrderNumber);
            }
        }
        return placed;
    }

    public Task<VoicePayCallback?> FindAsync(Guid id, CancellationToken ct) => db.VoicePayCallbacks.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    /// <summary>Deletes finished callbacks (and their events) past the retention; active ones are never touched here.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().AddDays(-Math.Max(1, options.Value.PayCallback.RetentionDays));
        var ids = db.VoicePayCallbacks.Where(c => c.CreatedAt < cutoff && c.Status != PayCallbackStatus.Scheduled && c.Status != PayCallbackStatus.Dialing && c.Status != PayCallbackStatus.Answered && c.Status != PayCallbackStatus.Paying).Select(c => c.Id);
        await db.VoicePayCallbackEvents.Where(e => ids.Contains(e.CallbackId)).ExecuteDeleteAsync(ct);
        return await db.VoicePayCallbacks.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(ct);
    }
}

/// <summary>Looks for callbacks that are due, and tidies up. Not started in tests (they call the service themselves).</summary>
public sealed class PayCallbackDispatcher(IServiceScopeFactory scopes, IOptions<VoiceOptions> options, ILogger<PayCallbackDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var lastPurge = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<PayCallbackService>();
                await svc.DispatchDueAsync(stop);
                if (DateTime.UtcNow - lastPurge > TimeSpan.FromHours(6)) { await svc.PurgeAsync(stop); lastPurge = DateTime.UtcNow; }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("The pay callback dispatcher met a problem: {Type}.", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.Value.PayCallback.PollSeconds)), stop); } catch (OperationCanceledException) { }
        }
    }
}

/// <summary>An erased account takes its pay callbacks with it (the callbacks of a guest carry no account and leave with the retention).</summary>
public sealed class VoicePayCallbackEraser(MarketplaceDb db) : Solvelt.Marketplace.Api.Account.IAccountEraser
{
    public async Task EraseAsync(Guid accountId, CancellationToken ct)
    {
        var ids = db.VoicePayCallbacks.Where(c => c.AccountId == accountId).Select(c => c.Id);
        await db.VoicePayCallbackEvents.Where(e => ids.Contains(e.CallbackId)).ExecuteDeleteAsync(ct);
        await db.VoicePayCallbacks.Where(c => c.AccountId == accountId).ExecuteDeleteAsync(ct);
    }
}

/// <summary>The callbacks the account's owner asked for by phone, in their data export (section "pay-callbacks"): the order, the state and the times; the number is theirs and is shown masked.</summary>
public sealed class VoicePayCallbackExporter(MarketplaceDb db) : Solvelt.Marketplace.Api.Account.IAccountDataExporter
{
    public string Section => "pay-callbacks";
    public async Task<object?> ExportAsync(Guid accountId, CancellationToken ct)
        => (await db.VoicePayCallbacks.AsNoTracking().Where(c => c.AccountId == accountId).OrderBy(c => c.CreatedAt).ToListAsync(ct)).Select(c => new { c.OrderNumber, c.Status, phone = PhoneMask.Mask(c.Phone), c.CreatedAt, c.ExpiresAt, c.EndedAt });
}
