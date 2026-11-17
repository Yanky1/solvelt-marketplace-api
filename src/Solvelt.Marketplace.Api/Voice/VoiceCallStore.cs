using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>Reads and writes <see cref="VoiceCall"/> rows. A singleton (the bridge lives as long as a call) that opens a short scope per operation. Counters are atomic SQL increments.</summary>
public sealed class VoiceCallStore(IServiceScopeFactory scopes, IOptions<VoiceOptions> options, TimeProvider time)
{
    /// <summary>Creates the call row (idempotent on <paramref name="callSid"/>: SignalWire retries webhooks). The model is chosen here, once, and logged.</summary>
    public async Task<VoiceCall> StartAsync(string callSid, string? from, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var existing = await db.VoiceCalls.AsNoTracking().FirstOrDefaultAsync(c => c.CallSid == callSid, ct);
        if (existing is not null) return existing;
        var id = Guid.NewGuid();
        var (model, tier) = VoiceModelSelector.Pick(options.Value, id);
        var call = new VoiceCall { Id = id, CallSid = callSid, CallerMasked = PhoneMask.Mask(from), CallerKey = CallerKeyOf(Infrastructure.PhoneNumbers.ToE164(from)), Model = model, Tier = tier, StartedAt = time.GetUtcNow() };
        db.VoiceCalls.Add(call);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)   // a parallel retry of the same webhook won the unique index
        {
            db.ChangeTracker.Clear();
            return await db.VoiceCalls.AsNoTracking().FirstAsync(c => c.CallSid == callSid, ct);
        }
        return call;
    }

    /// <summary>A keyed hash of the caller's number (null without a number or a key). The number itself is never stored.</summary>
    public string? CallerKeyOf(string? e164)
    {
        var key = options.Value.CallerHashKey;
        if (e164 is null || string.IsNullOrWhiteSpace(key)) return null;
        using var h = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexStringLower(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes("voice-caller-v1|" + e164)));
    }

    /// <summary>How many phone orders this number placed in the last 24 hours (calls that ended as <c>order_placed</c>).</summary>
    public async Task<int> OrdersTodayAsync(string callerKey, CancellationToken ct)
    {
        var since = time.GetUtcNow().AddHours(-24);
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceCalls.CountAsync(c => c.CallerKey == callerKey && c.Outcome == VoiceOutcome.OrderPlaced && c.StartedAt >= since, ct);
    }

    /// <summary>The number that proved itself in this call (keyed hash). Set when the SMS code is typed right.</summary>
    public async Task SetVerifiedKeyAsync(Guid id, string? key, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceCalls.Where(c => c.Id == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.VerifiedKey, key), ct);
    }

    /// <summary>Orders paid by phone in calls whose VERIFIED number is this one, newest first (our own records, never a match on a phone string in the orders).</summary>
    public async Task<IReadOnlyList<string>> PhoneOrdersAsync(string verifiedKey, int limit, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var rows = await (from a in db.VoicePaymentAttempts.AsNoTracking()
                          join c in db.VoiceCalls.AsNoTracking() on a.CallId equals c.Id
                          where c.VerifiedKey == verifiedKey && a.Status == "paid"
                          orderby a.SettledAt descending
                          select a.OrderNumber).Take(limit * 3).ToListAsync(ct);
        return rows.Distinct().Take(limit).ToList();
    }

    /// <summary>The call placed (and was paid for) an order: the outcome the cost report counts. Set once, stays through the call's end.</summary>
    public async Task MarkOrderPlacedAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceCalls.Where(c => c.Id == id && (c.Outcome == VoiceOutcome.InProgress || c.Outcome == VoiceOutcome.Abandoned)).ExecuteUpdateAsync(s => s.SetProperty(c => c.Outcome, VoiceOutcome.OrderPlaced), ct);   // also after the caller hung up: a payment link can be paid later
    }

    public async Task<VoiceCall?> FindAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceCalls.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<VoiceCall?> FindBySidAsync(string callSid, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceCalls.AsNoTracking().FirstOrDefaultAsync(c => c.CallSid == callSid, ct);
    }

    public async Task AddUsageAsync(Guid id, string tier, VoiceUsage u, CancellationToken ct)
    {
        var cost = u.EstimateMicros(VoiceModelSelector.Prices(options.Value, tier));
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceCalls.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.AudioInTokens, c => c.AudioInTokens + u.AudioIn).SetProperty(c => c.AudioOutTokens, c => c.AudioOutTokens + u.AudioOut)
            .SetProperty(c => c.TextInTokens, c => c.TextInTokens + u.TextIn).SetProperty(c => c.TextOutTokens, c => c.TextOutTokens + u.TextOut)
            .SetProperty(c => c.EstimatedCostMicros, c => c.EstimatedCostMicros + cost), ct);
    }

    public async Task AddLatencyAsync(Guid id, long ms, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceCalls.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.LatencySamples, c => c.LatencySamples + 1).SetProperty(c => c.LatencyTotalMs, c => c.LatencyTotalMs + ms), ct);
    }

    /// <summary>Records that a person was asked for and is reachable. Idempotent.</summary>
    public async Task MarkTransferAsync(Guid id, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceCalls.Where(c => c.Id == id && c.TransferRequestedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.TransferRequestedAt, now), ct);
    }

    /// <summary>Closes the call once: the outcome is <c>transferred</c> if a transfer was marked, otherwise what the bridge says (an order already placed stays placed).</summary>
    public async Task EndAsync(Guid id, string fallbackOutcome, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        var c = await db.VoiceCalls.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || c.EndedAt is not null) return;
        c.EndedAt = now;
        c.DurationSeconds = (int)Math.Max(0, (now - c.StartedAt).TotalSeconds);
        if (c.Outcome == VoiceOutcome.InProgress) c.Outcome = c.TransferRequestedAt is not null ? VoiceOutcome.Transferred : fallbackOutcome;
        c.Version++;
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { /* another writer closed it */ }
    }

    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().AddDays(-Math.Max(1, options.Value.CallRetentionDays));
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketplaceDb>();
        await db.VoicePaymentAttempts.Where(a => a.CreatedAt < cutoff).ExecuteDeleteAsync(ct);   // payment attempts follow the call rows
        return await db.VoiceCalls.Where(c => c.StartedAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
