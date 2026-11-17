using System.Collections.Concurrent;
using System.Text;

namespace Solvelt.Marketplace.Api.Voice;

public sealed record ProductRef(Guid ListingId, string Slug, string Title);
public sealed record VariantRef(Guid VariantId, string Name, long PriceCents, bool Available);

public enum CallerLevel { Anonymous, Guest, Verified }
public enum ConfirmationStatus { None, ReadBack, Confirmed }

/// <summary>
/// The truth about one call, held on the SERVER (decision V-02), never in the model's memory: who the caller is and how far they are verified, the cart, the chosen address
/// and delivery option, the current quote, the read-back/confirmation status and any pending account change. Tools read and write it; the model only ever sees a short
/// server-written summary. Nothing here is a transcript. Every setter that touches the order goes through <see cref="OrderChanged"/>, so a stale quote or confirmation cannot survive a change.
/// </summary>
public sealed class VoiceCallState
{
    private readonly object _gate = new();
    public Guid CallId { get; }
    public VoiceCallState(Guid callId) => CallId = callId;

    public CallerLevel Level { get; private set; } = CallerLevel.Anonymous;
    /// <summary>True once the caller typed a correct SMS code on the keypad in THIS call (slice 2). Account edits need it.</summary>
    public bool VerifiedInThisCall { get; private set; }
    public Guid? AccountId { get; private set; }
    public string? CartRef { get; private set; }
    public int CartItemCount { get; private set; }
    public string? AddressLabel { get; private set; }
    public string? DeliveryChoice { get; private set; }
    public string? QuoteKey { get; private set; }
    public long? QuoteTotalCents { get; private set; }
    public ConfirmationStatus Confirmation { get; private set; }
    public string? PendingAccountChange { get; private set; }
    public string Task { get; private set; } = "start";
    /// <summary>Set while the caller is on a keypad step (SMS code, confirmation key, payment): the stream ends without closing the call, and a new stream resumes it.</summary>
    public string? KeypadStep { get; private set; }
    /// <summary>How many times a stream has been attached to this call (more than one after a keypad step).</summary>
    public int Connections { get; private set; }
    /// <summary>Bumped on every change; a read-back records the version it was made for.</summary>
    public int Version { get; private set; }
    public DateTimeOffset LastTouched { get; private set; }

    /// <summary>The caller's number as SignalWire reported it (E.164). Memory only: never stored, logged or given to the model. Caller ID alone never authenticates anyone.</summary>
    public string? CallerNumber { get; private set; }
    public string? FirstName { get; private set; }
    public string? GuestName { get; private set; }
    public string? VerifiedPhone { get; private set; }
    public PhoneChallenge? Challenge { get; private set; }
    private int _sms;
    /// <summary>The guest cart's token (the cart service creates it on the first add); an account cart needs none.</summary>
    public string? CartToken { get; private set; }
    /// <summary>The products last read out, so the caller can say "the second one". The model names a position; the server holds the ids.</summary>
    public IReadOnlyList<ProductRef> LastResults { get; private set; } = [];

    public void SetCaller(string? e164) { lock (_gate) CallerNumber ??= e164; }
    public void SetFirstName(string? name) { lock (_gate) FirstName = name; }
    public void SetGuestName(string? name) { lock (_gate) { GuestName = name; Version++; } }
    public void SetVerifiedPhone(string? phone) { lock (_gate) { VerifiedPhone = phone; Version++; } }
    /// <summary>The number and account of the last challenge, kept after it is consumed so the keypad handler can apply it.</summary>
    public string? LastChallengePhone { get; private set; }
    public Guid? LastChallengeAccount { get; private set; }
    public void SetChallenge(PhoneChallenge? c) { lock (_gate) { if (c is not null) { LastChallengePhone = c.Phone; LastChallengeAccount = c.AccountId; } Challenge = c; } }
    public bool TryCountSms(int max) { lock (_gate) { if (_sms >= max) return false; _sms++; return true; } }
    public void SetCartToken(string? token) { lock (_gate) CartToken = token; }
    public void SetResults(IReadOnlyList<ProductRef> results) { lock (_gate) LastResults = results; }
    /// <summary>The product last described and its variants, so "the large one" means a variant the server knows.</summary>
    public ProductRef? CurrentProduct { get; private set; }
    public IReadOnlyList<VariantRef> CurrentVariants { get; private set; } = [];
    public void SetCurrentProduct(ProductRef? product, IReadOnlyList<VariantRef> variants) { lock (_gate) { CurrentProduct = product; CurrentVariants = variants; } }
    /// <summary>How the last keypad step ended (<c>verified</c>, <c>failed</c>), told to the model once when the stream resumes.</summary>
    public string? LastKeypadOutcome { get; private set; }
    public void SetKeypadOutcome(string? outcome) { lock (_gate) LastKeypadOutcome = outcome; }

    public void Connected(DateTimeOffset now) { lock (_gate) { Connections++; LastTouched = now; } }
    public void Touch(DateTimeOffset now) { lock (_gate) LastTouched = now; }

    public void BeginKeypadStep(string step) { lock (_gate) { KeypadStep = step; Version++; } }
    public void EndKeypadStep() { lock (_gate) { KeypadStep = null; Version++; } }

    public void SetTask(string task) { lock (_gate) { Task = task; Version++; } }

    public void Identify(CallerLevel level, Guid? accountId, bool verifiedNow)
    {
        lock (_gate)
        {
            Level = level; AccountId = accountId; VerifiedInThisCall |= verifiedNow; Version++;
            if (accountId is null) { /* a guest has no account */ }
            OrderChangedLocked();   // who is ordering is part of the order
        }
    }

    public void SetCart(string? cartRef, int items) { lock (_gate) { CartRef = cartRef; CartItemCount = items; Version++; OrderChangedLocked(); } }
    public void SetAddress(string? label) { lock (_gate) { AddressLabel = label; Version++; OrderChangedLocked(); } }
    public void SetDelivery(string? choice) { lock (_gate) { DeliveryChoice = choice; Version++; OrderChangedLocked(); } }
    public void SetQuote(string? key, long? totalCents) { lock (_gate) { QuoteKey = key; QuoteTotalCents = totalCents; Confirmation = ConfirmationStatus.None; Version++; } }
    public void SetPendingAccountChange(string? description) { lock (_gate) { PendingAccountChange = description; Version++; } }

    /// <summary>The server read the order back (items, address, total) to the caller; only a read-back of the CURRENT version can be confirmed.</summary>
    public void ReadBackDone() { lock (_gate) { if (QuoteKey is not null) Confirmation = ConfirmationStatus.ReadBack; } }
    public bool Confirm() { lock (_gate) { if (Confirmation != ConfirmationStatus.ReadBack || QuoteKey is null) return false; Confirmation = ConfirmationStatus.Confirmed; return true; } }

    /// <summary>Anything that affects the order invalidates the quote and the confirmation: a fresh quote and read-back are required.</summary>
    private void OrderChangedLocked() { QuoteKey = null; QuoteTotalCents = null; Confirmation = ConfirmationStatus.None; }
    public void OrderChanged() { lock (_gate) { OrderChangedLocked(); Version++; } }

    /// <summary>Short, server-written, free of anything secret (no code, no card, no full address or number): given to the model whenever a stream (re)connects. The same text every time for the same state.</summary>
    public string Summary()
    {
        lock (_gate)
        {
            var sb = new StringBuilder("Server state of this call (facts, not caller words). ");
            sb.Append(Level switch { CallerLevel.Verified => VerifiedInThisCall ? "The caller is verified by SMS code in this call. " : "The caller is recognised but not verified in this call. ", CallerLevel.Guest => "The caller is a guest" + (VerifiedPhone is not null ? " whose phone is verified. " : ". "), _ => "The caller is not identified yet. " });
            if (FirstName is not null && Level == CallerLevel.Verified) sb.Append("Their first name is ").Append(FirstName).Append(". ");
            sb.Append("Current task: ").Append(Task).Append(". ");
            if (LastKeypadOutcome == "failed") sb.Append("The caller typed a wrong code too often or it expired: they are NOT verified. ");
            sb.Append(CartItemCount == 0 ? "The cart is empty. " : $"The cart has {CartItemCount} item{(CartItemCount == 1 ? "" : "s")}. ");
            if (AddressLabel is not null) sb.Append("Delivery address chosen: ").Append(AddressLabel).Append(". ");
            if (DeliveryChoice is not null) sb.Append("Delivery option: ").Append(DeliveryChoice).Append(". ");
            sb.Append(QuoteKey is null ? "There is no current quote. " : "A current quote exists. ");
            sb.Append(Confirmation switch { ConfirmationStatus.Confirmed => "The caller confirmed the read-back. ", ConfirmationStatus.ReadBack => "The order was read back and awaits the caller's yes. ", _ => "The order has not been read back. " });
            if (PendingAccountChange is not null) sb.Append("A pending account change awaits confirmation: ").Append(PendingAccountChange).Append(". ");
            sb.Append("Say briefly where things stand when you resume, then continue. Never state a price or total that a tool did not give you.");
            return sb.ToString();
        }
    }
}

/// <summary>
/// The states of calls in progress, in memory (decision V-02): a state lives only as long as its call (plus a short grace for a keypad-step reconnect), holds no transcript and is
/// dropped at the end. One instance serves a call; run the voice number behind a single instance or session affinity on the call (documented).
/// </summary>
public sealed class VoiceStateStore(TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, VoiceCallState> _states = new();
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);

    public VoiceCallState GetOrCreate(Guid callId)
    {
        Sweep();
        var s = _states.GetOrAdd(callId, id => new VoiceCallState(id));
        s.Touch(time.GetUtcNow());
        return s;
    }

    public VoiceCallState? Find(Guid callId) => _states.TryGetValue(callId, out var s) ? s : null;
    public void Remove(Guid callId) => _states.TryRemove(callId, out _);
    public int Count => _states.Count;

    private void Sweep()
    {
        var cutoff = time.GetUtcNow() - Grace;
        foreach (var kv in _states) if (kv.Value.LastTouched != default && kv.Value.LastTouched < cutoff) _states.TryRemove(kv.Key, out _);
    }
}
