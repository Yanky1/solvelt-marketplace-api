using System.Net;
using System.Text.Json;

namespace Solvelt.Marketplace.Api.Tests.Infrastructure;

/// <summary>
/// The portal's booking endpoints as far as M4 needs them, with the one property the contract promises: a slot is held by exactly one booking (the first writer wins,
/// SERVICES.md §3) and a repeated <c>marketplaceBookingId</c> returns the same booking with <c>created:false</c>. State is a plain dictionary under a lock.
/// </summary>
public sealed class FakeBookingPortal
{
    public sealed class Hold
    {
        public Guid BookingId { get; init; } = Guid.NewGuid();
        public string Mid { get; init; } = "";
        public Guid ServiceId { get; init; }
        public DateTimeOffset Start { get; set; }
        public string Status { get; set; } = "Hold";
        public string Location { get; init; } = "online";
        public int Minutes { get; init; }
        public long Price { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public string? OnlineInstructions { get; set; }
        public string BodyText { get; init; } = "";
        public long Fee { get; set; }
        public DateTimeOffset? ProposedStart { get; set; }
        public List<string> RequestIds { get; } = [];
        public object? Policy { get; set; }
    }

    private readonly object _lock = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Hold> _byMid = new();
    public long PriceCents { get; set; } = 24000;
    public int DurationMinutes { get; set; } = 240;
    public string? OnlineInstructions { get; set; }
    /// <summary>When set, the next hold answers with this status/code (then the flag clears).</summary>
    public (HttpStatusCode Status, string Code)? FailNext { get; set; }
    public bool Down { get; set; }
    /// <summary>The portal creates the hold but the answer is lost on the way (a timeout after the write): the case idempotency exists for.</summary>
    public bool LoseAnswer { get; set; }
    public int HoldCalls;
    public int ConfirmCalls;
    /// <summary>Instant booking (Confirmed) when true, request-to-book (Requested) when false.</summary>
    public bool Instant { get; set; } = true;
    /// <summary>The next confirm answers with this (one shot), for example 409 slot_unavailable once the hold has lapsed and someone else took the time.</summary>
    public (HttpStatusCode Status, string Code)? ConfirmFailNext { get; set; }
    /// <summary>Every confirm answers 503 while set (an outage between the payment and the confirmation).</summary>
    public bool ConfirmDown { get; set; }
    /// <summary>Runs inside the portal's confirm, after the booking is confirmed there and before the answer leaves (a race with an event on our side).</summary>
    public Action? OnConfirmed { get; set; }
    public List<string> ConfirmBodies { get; } = [];
    public List<string> PaymentEvents { get; } = [];
    /// <summary>The fee the portal charges when the buyer cancels (cents); the portal's policy is its own business.</summary>
    public long CancelFeeCents { get; set; }
    public bool RescheduleLimitReached { get; set; }
    public int ActionCalls;
    /// <summary>The policy the NEXT hold is created with (the portal freezes it on the booking); null = the portal does not send one.</summary>
    public object? PolicyForNext { get; set; }

    public FakeBookingPortal(TimeProvider clock) => _clock = clock;

    public IReadOnlyList<Hold> Holds { get { lock (_lock) return _byMid.Values.ToList(); } }
    public Hold? Find(string mid) { lock (_lock) return _byMid.GetValueOrDefault(mid); }

    public HttpResponseMessage? Handle(ReceivedCall c)
    {
        if (c.Method == "POST" && c.PathAndQuery == "/api/internal/v1/bookings/holds") return HoldCall(c);
        if (c.Method == "POST" && c.PathAndQuery == "/api/internal/v1/payment-events")
        {
            lock (_lock) PaymentEvents.Add(c.BodyText);
            return FakePortal.Json(HttpStatusCode.OK, "{\"status\":\"ok\"}");
        }
        if (c.Method == "POST" && c.PathAndQuery.StartsWith("/api/internal/v1/bookings/") && c.PathAndQuery.EndsWith("/confirm")) return ConfirmCall(c);
        if (c.Method == "POST" && c.PathAndQuery.StartsWith("/api/internal/v1/bookings/") && c.PathAndQuery.EndsWith("/cancellations")) return Action(c, "/cancellations", CancelCall);
        if (c.Method == "POST" && c.PathAndQuery.StartsWith("/api/internal/v1/bookings/") && c.PathAndQuery.EndsWith("/reschedule-requests")) return Action(c, "/reschedule-requests", RescheduleCall);
        if (c.Method == "POST" && c.PathAndQuery.StartsWith("/api/internal/v1/bookings/") && c.PathAndQuery.EndsWith("/proposal-responses")) return Action(c, "/proposal-responses", ProposalCall);
        if (c.Method == "GET" && c.PathAndQuery.StartsWith("/api/internal/v1/bookings/"))
        {
            if (Down) return FakePortal.Json(HttpStatusCode.ServiceUnavailable, "{}");
            var mid = c.PathAndQuery["/api/internal/v1/bookings/".Length..];
            lock (_lock) return _byMid.TryGetValue(mid, out var h) ? FakePortal.Json(HttpStatusCode.OK, Summary(h)) : FakePortal.Json(HttpStatusCode.NotFound, "{\"code\":\"not_found\"}");
        }
        return null;
    }

    private HttpResponseMessage Action(ReceivedCall c, string suffix, Func<Hold, JsonElement, HttpResponseMessage> run)
    {
        Interlocked.Increment(ref ActionCalls);
        var mid = c.PathAndQuery["/api/internal/v1/bookings/".Length..^suffix.Length];
        if (Down) return FakePortal.Json(HttpStatusCode.ServiceUnavailable, "{}");
        lock (_lock)
        {
            if (!_byMid.TryGetValue(mid, out var h)) return FakePortal.Json(HttpStatusCode.NotFound, "{\"code\":\"not_found\"}");
            return run(h, JsonDocument.Parse(c.Body).RootElement.Clone());
        }
    }

    private static HttpResponseMessage Err(HttpStatusCode s, string code) => FakePortal.Json(s, JsonSerializer.Serialize(new { code }));

    private HttpResponseMessage CancelCall(Hold h, JsonElement body)
    {
        var rid = body.GetProperty("requestId").GetString()!;
        if (h.Status == "CancelledByBuyer") return FakePortal.Json(HttpStatusCode.OK, Summary(h));           // idempotent
        if (h.Status is not ("Hold" or "Requested" or "Confirmed" or "Rescheduled")) return Err(HttpStatusCode.Conflict, "invalid_state");
        h.RequestIds.Add(rid);
        h.Fee = h.Status is "Confirmed" or "Rescheduled" ? CancelFeeCents : 0;
        h.Status = "CancelledByBuyer";
        return FakePortal.Json(HttpStatusCode.OK, Summary(h));
    }

    private HttpResponseMessage RescheduleCall(Hold h, JsonElement body)
    {
        if (h.Status is not ("Confirmed" or "Rescheduled")) return Err(HttpStatusCode.Conflict, "invalid_state");
        if (RescheduleLimitReached) return Err(HttpStatusCode.Conflict, "reschedule_limit");
        var ns = body.GetProperty("newStart").GetDateTimeOffset();
        if (_byMid.Values.Any(o => o != h && o.ServiceId == h.ServiceId && o.Start == ns && o.Status is "Hold" or "Requested" or "Confirmed")) return Err(HttpStatusCode.Conflict, "slot_unavailable");
        h.RequestIds.Add(body.GetProperty("requestId").GetString()!);
        h.Start = ns; h.Status = "Rescheduled";
        return FakePortal.Json(HttpStatusCode.OK, Summary(h));
    }

    private HttpResponseMessage ProposalCall(Hold h, JsonElement body)
    {
        if (h.Status != "Requested" || h.ProposedStart is null) return Err(HttpStatusCode.Conflict, "invalid_state");
        if (body.GetProperty("accept").GetBoolean()) { h.Start = h.ProposedStart.Value; h.ProposedStart = null; h.Status = "Confirmed"; }
        else { h.ProposedStart = null; h.Status = "CancelledByBuyer"; }
        return FakePortal.Json(HttpStatusCode.OK, Summary(h));
    }

    private HttpResponseMessage ConfirmCall(ReceivedCall c)
    {
        Interlocked.Increment(ref ConfirmCalls);
        var mid = c.PathAndQuery["/api/internal/v1/bookings/".Length..^"/confirm".Length];
        lock (_lock)
        {
            ConfirmBodies.Add(c.BodyText);
            if (ConfirmDown) return FakePortal.Json(HttpStatusCode.ServiceUnavailable, "{}");
            if (ConfirmFailNext is { } f) { ConfirmFailNext = null; return FakePortal.Json(f.Status, JsonSerializer.Serialize(new { code = f.Code })); }
            if (!_byMid.TryGetValue(mid, out var h)) return FakePortal.Json(HttpStatusCode.NotFound, "{\"code\":\"not_found\"}");
            if (h.Status == "Hold") h.Status = Instant ? "Confirmed" : "Requested";
            var body = Summary(h);
            var hook = OnConfirmed; OnConfirmed = null; hook?.Invoke();
            return FakePortal.Json(HttpStatusCode.OK, body);
        }
    }

    private HttpResponseMessage HoldCall(ReceivedCall c)
    {
        Interlocked.Increment(ref HoldCalls);
        if (Down) return FakePortal.Json(HttpStatusCode.ServiceUnavailable, "{}");
        var j = JsonDocument.Parse(c.Body).RootElement;
        var mid = j.GetProperty("marketplaceBookingId").GetString()!;
        var start = j.GetProperty("start").GetDateTimeOffset();
        lock (_lock)
        {
            if (_byMid.TryGetValue(mid, out var existing)) return LoseAnswer ? FakePortal.Json(HttpStatusCode.ServiceUnavailable, "{}") : FakePortal.Json(HttpStatusCode.OK, Wrap(existing, false));
            if (FailNext is { } f) { FailNext = null; return FakePortal.Json(f.Status, JsonSerializer.Serialize(new { code = f.Code })); }
            var serviceId = j.GetProperty("serviceId").GetGuid();
            if (_byMid.Values.Any(h => h.ServiceId == serviceId && h.Start == start && h.Status is "Hold" or "Requested" or "Confirmed"))
                return FakePortal.Json(HttpStatusCode.Conflict, "{\"code\":\"slot_unavailable\"}");
            var now = _clock.GetUtcNow();
            var hold = new Hold
            {
                Mid = mid, ServiceId = serviceId, Start = start, Location = j.GetProperty("location").GetString()!, Minutes = DurationMinutes, Price = PriceCents,
                ExpiresAt = now.AddMinutes(10), OnlineInstructions = OnlineInstructions, BodyText = c.BodyText, Policy = PolicyForNext,
            };
            _byMid[mid] = hold;
            if (LoseAnswer) return FakePortal.Json(HttpStatusCode.ServiceUnavailable, "{}");
            return FakePortal.Json(HttpStatusCode.Created, Wrap(hold, true));
        }
    }

    private static string Wrap(Hold h, bool created) => "{\"created\":" + (created ? "true" : "false") + ",\"booking\":" + Summary(h) + "}";

    public static string Summary(Hold h) => JsonSerializer.Serialize(new
    {
        bookingId = h.BookingId, number = "BK-2026-0001", marketplaceBookingId = h.Mid, status = h.Status, serviceId = h.ServiceId, providerId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        location = h.Location, startAt = h.Start, endAt = h.Start.AddMinutes(h.Minutes), durationMinutes = h.Minutes, holdExpiresAt = h.Status == "Hold" ? h.ExpiresAt : (DateTimeOffset?)null,
        priceCents = h.Price, addOnsCents = 0, travelFeeCents = 0, totalCents = h.Price, cancellationFeeCents = h.Fee, additionalChargeCents = 0, rescheduleCount = 0, onlineInstructions = h.OnlineInstructions, proposedStartAt = h.ProposedStart, policy = h.Policy,
    });
}
