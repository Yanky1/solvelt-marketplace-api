using System.Collections.Concurrent;
using System.Text;
using Solvelt.Marketplace.Api.Checkout;

namespace Solvelt.Marketplace.Api.Voice;

public sealed record ProductRef(Guid ListingId, string Slug, string Title);
public sealed record DeliveryChoice(Guid SellerId, Guid BrandId, string SellerName, IReadOnlyList<DeliveryOption> Options);
public sealed record DeliveryOption(string Kind, string? Speed, Guid? WindowId, string? Date, long PriceCents, string Spoken);
public sealed record VariantRef(Guid VariantId, string Name, long PriceCents, bool Available);

/// <summary>An account change the server has read back and is waiting for the caller's yes on (slice 6, decision V-09). The model proposes; only the keypad answer applies it.</summary>
public sealed record AccountChangeRequest(string Kind, string Text, string? Name = null, string? Phone = null, Guid? AddressId = null, Solvelt.Marketplace.Api.Account.AddressBody? Body = null, string? Email = null, bool PhoneProved = false, Guid? CardId = null);
public sealed record PendingCancel(string OrderNumber, string? SellerOrderNumber, string Reason, string Text);

/// <summary>A saved-card payment waiting for the caller's yes (V-16): the card id (the server's, never the model's) and the sentence the SERVER reads back.</summary>
/// <summary>What the PIN flow of this call is doing (V-18): <c>set</c> or <c>remove</c>, and the hash of the first entry while the caller types it again. The digits are never kept.</summary>
public sealed record PinSetup(string Action, string? Hash, Guid? Unlock = null);

public sealed record CardPayRequest(Guid CardId, string Text);

public sealed record CardSaveStep(string AttemptId, System.Xml.Linq.XElement Instruction);

public sealed class PaymentAttempt
{
    public required string AttemptId { get; init; }
    public required string OrderNumber { get; init; }
    public required string IntentId { get; init; }
    public required long AmountCents { get; init; }
    public required string Method { get; init; }
    public string Status { get; set; } = "pending";   // pending, paid, declined, failed
    /// <summary>The provider's cXML for the keypad step (never holds card data).</summary>
    public System.Xml.Linq.XElement? Instruction { get; init; }
}

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
    /// <summary>When the caller typed the right SMS code in THIS call (the time a cancellation counts as authenticated).</summary>
    public DateTimeOffset? VerifiedAt { get; private set; }
    public void SetVerifiedAt(DateTimeOffset at) { lock (_gate) VerifiedAt = at; }
    /// <summary>A cancellation waiting for the caller's yes, read back by the server (slice 5, decision V-08).</summary>
    public PendingCancel? Cancel { get; private set; }
    public int CancelStarts { get; private set; }
    public void SetCancel(PendingCancel? c) { lock (_gate) Cancel = c; }
    /// <summary>The account change waiting for the caller's yes (create the account, change the name, an address, or the phone number).</summary>
    public AccountChangeRequest? AccountChange { get; private set; }
    public int AccountChangeStarts { get; private set; }
    public void SetAccountChange(AccountChangeRequest? c) { lock (_gate) { AccountChange = c; PendingAccountChange = c is null ? null : c.Kind switch { "create" => "creating an account", "name" => "changing the name", "phone" => "changing the phone number", _ => "changing a saved address" }; } }
    public int AccountLinks { get; private set; }
    public int CountAccountLink() { lock (_gate) return ++AccountLinks; }
    public int CountAccountChangeStart() { lock (_gate) return ++AccountChangeStarts; }
    /// <summary>A change to who the caller is (a new name, a new account) is part of the order: the quote and the confirmation go, a fresh read-back is required.</summary>
    public void AccountDetailsChanged(string? firstName) { lock (_gate) { if (firstName is not null) FirstName = firstName; Version++; OrderChangedLocked(); Quote = null; } }
    public string? LastChallengePurpose { get; private set; }
    public int CountCancelStart() { lock (_gate) return ++CancelStarts; }
    /// <summary>Off-topic requests in THIS call (V2, V-11). Only ever counted up: it survives keypad hand-offs and resumes, and there is no way to reset it.</summary>
    public int OffTopicCount { get; private set; }
    public int CountOffTopic() { lock (_gate) return ++OffTopicCount; }
    /// <summary>The server ended the AI part of the call at the off-topic limit; the after-stream webhook reads it (the call row keeps <c>order_placed</c> if an order was already placed).</summary>
    public bool OffTopicEnded { get; private set; }
    /// <summary>Option 6 (decision V-35): the call is turn by turn (cXML Gather speech), not a stream. Set once, when the caller picks it.</summary>
    public bool SpeechMode { get; private set; }
    /// <summary>Set for a turn that must never reach the diagnostics (a spoken verification code, anything sensitive). Cleared by <see cref="AllowDiagnostics"/>.</summary>
    public bool SuppressDiagnostics => SensitiveTurn;
    public void SuppressDiagnosticsNow() => MarkSensitiveTurn();
    public void AllowDiagnostics() => ClearSensitiveTurn();
    /// <summary>
    /// The caller is saying something that must not be kept or passed on beyond what the step needs (a spoken verification code, decision V-41): the diagnostics store nothing for it and the recogniser's alternatives
    /// are NOT given to the text model (V-42). One flag for every such rule, set by the step that asks and cleared when it is over.
    /// </summary>
    public bool SensitiveTurn { get; private set; }
    public void MarkSensitiveTurn() { lock (_gate) SensitiveTurn = true; }
    public void ClearSensitiveTurn() { lock (_gate) SensitiveTurn = false; }
    private int _diagnosticTurns;
    public int NextDiagnosticTurn() { lock (_gate) return ++_diagnosticTurns; }
    public void EnterSpeechMode() { lock (_gate) SpeechMode = true; }
    public void LeaveSpeechMode() { lock (_gate) SpeechMode = false; }
    /// <summary>The conversation of an option 6 call, in memory only (what the caller said as the recogniser wrote it, and the replies). Never stored or logged; it goes with the call's state.</summary>
    public List<LlmMessage> SpeechHistory { get; } = [];
    private int _speechEmpties;
    /// <summary>Counts a turn with no words; returns how many in a row. A turn with words resets it.</summary>
    public int NoteSpeechTurn(bool empty) { lock (_gate) return _speechEmpties = empty ? _speechEmpties + 1 : 0; }
    public void MarkOffTopicEnded() { lock (_gate) OffTopicEnded = true; }
    /// <summary>The orders last read out (numbers by position), so "the first one" means an order the server knows.</summary>
    public IReadOnlyList<string> ListedOrders { get; private set; } = [];
    public void SetListedOrders(IReadOnlyList<string> numbers) { lock (_gate) ListedOrders = numbers; }
    public int OrderTexts { get; private set; }
    public int CountOrderText() { lock (_gate) return ++OrderTexts; }
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
    public void SetChallenge(PhoneChallenge? c) { lock (_gate) { if (c is not null) { LastChallengePhone = c.Phone; LastChallengeAccount = c.AccountId; LastChallengePurpose = c.Purpose; } Challenge = c; } }
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

    // ---- delivery, quote and read-back (slice 3, decision V-04) ----
    /// <summary>The full delivery address. Memory only: the model never receives it, only the server's spoken read-back does.</summary>
    public CheckoutAddress? DeliveryAddress { get; private set; }
    /// <summary>The caller has heard the address read back and said it is right (a saved address counts as right when chosen).</summary>
    public bool AddressConfirmed { get; private set; }
    public IReadOnlyList<Guid> SavedAddressIds { get; private set; } = [];
    /// <summary>The saved cards the server read out, in order (V-16): the model names a position, the server holds the ids, and only the account's own.</summary>
    public IReadOnlyList<Guid> SavedCardIds { get; private set; } = [];
    public void SetSavedCards(IReadOnlyList<Guid> ids) { lock (_gate) SavedCardIds = ids; }
    /// <summary>The account whose PIN the caller must still type before this call is signed in as it (V-18).</summary>
    public Guid? PinFor { get; private set; }
    public void SetPinFor(Guid? account) { lock (_gate) PinFor = account; }
    public int PinTries { get; private set; }
    public int CountPinTry() { lock (_gate) return ++PinTries; }
    public void ResetPinTries() { lock (_gate) PinTries = 0; }
    public PinSetup? Pin { get; private set; }
    public void SetPin(PinSetup? pin) { lock (_gate) Pin = pin; }
    public CardPayRequest? CardPay { get; private set; }
    public void SetCardPay(CardPayRequest? request) { lock (_gate) CardPay = request; }
    public int CardSaveStarts { get; private set; }
    public int CountCardSaveStart() { lock (_gate) return ++CardSaveStarts; }
    /// <summary>The card-saving step that is running on the keypad (V-16): the attempt and the provider's instruction. No card data, ever.</summary>
    public CardSaveStep? CardSave { get; private set; }
    public void SetCardSave(CardSaveStep? step) { lock (_gate) CardSave = step; }
    public IReadOnlyList<GroupSelection> Selections { get; private set; } = [];
    public IReadOnlyList<DeliveryChoice> Choices { get; private set; } = [];
    public QuoteView? Quote { get; private set; }
    public string? ReadBackText { get; private set; }
    public int ReadBackVersion { get; private set; }
    public int ConfirmFailures { get; private set; }

    public void SetSavedAddresses(IReadOnlyList<Guid> ids) { lock (_gate) SavedAddressIds = ids; }

    /// <summary>A new delivery address: options and prices may change, so the choices, the quote and any confirmation go.</summary>
    public void SetDeliveryAddress(CheckoutAddress address, bool confirmed, string label)
    {
        lock (_gate) { DeliveryAddress = address; AddressConfirmed = confirmed; Selections = []; Choices = []; Quote = null; AddressLabel = label; Version++; OrderChangedLocked(); }
    }

    public void ConfirmAddress() { lock (_gate) { if (DeliveryAddress is not null) AddressConfirmed = true; } }

    public void SetChoices(IReadOnlyList<DeliveryChoice> choices) { lock (_gate) Choices = choices; }

    public void SetSelection(GroupSelection pick, string label)
    {
        lock (_gate)
        {
            Selections = Selections.Where(x => !(x.SellerId == pick.SellerId && x.BrandId == pick.BrandId)).Append(pick).ToList();
            DeliveryChoice = label; Version++; OrderChangedLocked(); Quote = null;
        }
    }

    public void SetQuoteView(QuoteView view) { lock (_gate) { Quote = view; QuoteKey = view.QuoteHash; QuoteTotalCents = view.TotalCents; Confirmation = ConfirmationStatus.None; Version++; } }

    /// <summary>The server is about to speak this read-back; a yes counts only for it, and only while nothing changes.</summary>
    public void BeginReadBack(string text) { lock (_gate) { ReadBackText = text; ReadBackVersion = Version; Confirmation = QuoteKey is null ? ConfirmationStatus.None : ConfirmationStatus.ReadBack; ConfirmFailures = 0; } }

    /// <summary>A yes (spoken or on the keypad) after the server's own read-back of the CURRENT order. Nothing else confirms.</summary>
    public bool ConfirmReadBack()
    {
        lock (_gate)
        {
            if (Confirmation != ConfirmationStatus.ReadBack || QuoteKey is null || Version != ReadBackVersion) return false;
            Confirmation = ConfirmationStatus.Confirmed;
            return true;
        }
    }

    public int CountConfirmFailure() { lock (_gate) return ++ConfirmFailures; }

    // ---- guest email, payment and the placed order (slice 4, decision V-06) ----
    /// <summary>A guest's order is mailed its access link: the address the caller spelled and confirmed. Memory only; never given back to the model after the read-back.</summary>
    public string? GuestEmail { get; private set; }
    public string? GuestEmailDraft { get; private set; }
    public PaymentAttempt? Payment { get; private set; }
    public int PaymentStarts { get; private set; }
    public string? OrderNumber { get; private set; }

    public void SetGuestEmailDraft(string? email) { lock (_gate) GuestEmailDraft = email; }
    public bool ConfirmGuestEmail() { lock (_gate) { if (GuestEmailDraft is null) return false; GuestEmail = GuestEmailDraft; GuestEmailDraft = null; Version++; OrderChangedLocked(); return true; } }
    public int CountPaymentStart() { lock (_gate) return ++PaymentStarts; }
    public void SetPayment(PaymentAttempt? attempt) { lock (_gate) Payment = attempt; }
    public void SetOrderPlaced(string number) { lock (_gate) { OrderNumber = number; Task = "placed"; } }

    public void Connected(DateTimeOffset now) { lock (_gate) { Connections++; LastTouched = now; } }
    public void Touch(DateTimeOffset now) { lock (_gate) LastTouched = now; }

    // a keypad step and the current task are not order changes: they must not make a read-back stale
    public void BeginKeypadStep(string step) { lock (_gate) KeypadStep = step; }
    public void EndKeypadStep() { lock (_gate) KeypadStep = null; }

    public void SetTask(string task) { lock (_gate) Task = task; }

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
            if (LastKeypadOutcome == "pin_set") sb.Append("The caller just set a PIN on the keypad and it is saved: say so, and never ask for or repeat it. ");
            if (LastKeypadOutcome == "pin_removed") sb.Append("The caller's PIN was removed. ");
            if (LastKeypadOutcome == "pin_failed") sb.Append("The caller did not give the right PIN (or the PIN could not be set): they are NOT signed in to the account on this call; they can still order as a guest. Never ask for the PIN aloud. ");
            if (LastKeypadOutcome == "card_pay_declined") sb.Append("The caller did not confirm paying with the saved card: nothing was charged. Offer the keypad or a text link. ");
            if (LastKeypadOutcome == "card_saved") sb.Append("The caller just saved a card at the payment step: it is in their saved cards now (say only its last four digits if they ask, never more). ");
            if (LastKeypadOutcome == "card_pending") sb.Append("The card the caller typed went through at the payment step but the confirmation has not arrived yet: say it is being confirmed and they can ask to hear their saved cards in a moment. ");
            if (LastKeypadOutcome == "card_failed") sb.Append("The card could not be saved (declined or not completed): offer to try once more or to leave it. ");
            if (LastKeypadOutcome == "account_changed") sb.Append("The caller just confirmed an account change by keypad and it was saved. ");
            if (LastKeypadOutcome == "account_created") sb.Append("The caller just confirmed creating an account by keypad: it exists now and they are signed in as it (their cart is in it). ");
            if (LastKeypadOutcome == "account_declined") sb.Append("The caller did not confirm the account change: nothing was changed. ");
            if (LastKeypadOutcome == "account_refused") sb.Append("The account change could not be saved (a rule refused it, or the number is already in use): say so and offer a person or the web site. ");
            if (LastKeypadOutcome == "cancel_sent") sb.Append("The caller just confirmed a cancellation request and it was sent to the seller; they are told by email what the seller decides. ");
            if (LastKeypadOutcome == "cancel_refused") sb.Append("The cancellation could not be requested (the order can no longer be cancelled, or something went wrong). ");
            if (LastKeypadOutcome == "cancel_declined") sb.Append("The caller decided to keep the order: nothing was cancelled. ");
            if (OrderNumber is not null) sb.Append("An order has been placed and paid for in this call: its number is ").Append(VoiceSpeech.Spelled(OrderNumber)).Append(". Do not discuss card details. ");
            else if (Payment is { Status: "pending" }) sb.Append("A payment is in progress for the order. ");
            if (LastKeypadOutcome == "pay_pending") sb.Append("The caller's card payment went through at the payment step but the confirmation has not arrived yet: say it is being confirmed, that they will get a text when the order is placed, and use check_payment if they ask. ");
            if (LastKeypadOutcome == "pay_failed") sb.Append("The payment did not go through (declined or not completed): offer to try again or another way. ");
            if (LastKeypadOutcome == "confirmed") sb.Append("The caller just confirmed the order read-back by keypad or voice. ");
            if (LastKeypadOutcome == "change_requested") sb.Append("The caller wants to change something in the order after the read-back: ask what. ");
            if (LastKeypadOutcome == "unclear") sb.Append("The read-back was not confirmed (no clear yes or no): ask what they want to do. ");
            if (LastKeypadOutcome == "failed") sb.Append("The caller typed a wrong code too often or it expired: they are NOT verified. ");
            sb.Append(CartItemCount == 0 ? "The cart is empty. " : $"The cart has {CartItemCount} item{(CartItemCount == 1 ? "" : "s")}. ");
            if (AddressLabel is not null) sb.Append("Delivery address chosen: ").Append(AddressLabel).Append(". ");
            if (DeliveryChoice is not null) sb.Append("Delivery option: ").Append(DeliveryChoice).Append(". ");
            if (DeliveryAddress is not null) sb.Append(AddressConfirmed ? "The delivery address is set and confirmed. " : "A delivery address was given and still needs the caller to confirm it. ");
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
