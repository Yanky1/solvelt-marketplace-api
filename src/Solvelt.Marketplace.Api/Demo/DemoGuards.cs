using Microsoft.EntityFrameworkCore;
using Solvelt.Marketplace.Api.Auth;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Messaging;

namespace Solvelt.Marketplace.Api.Demo;

/// <summary>
/// "Nothing real leaves the system" (M9). A buyer action on a demo order, booking or thread would queue work for the portal, a payment provider or a mailbox about something
/// that does not exist there. These filters refuse it first, with <c>409 demo_data</c>, before anything is read or written: the buyer can look at everything, change nothing that
/// leaves. They run after the sign-in filter (so they know the account) and only look at the signed-in buyer's own rows.
/// </summary>
public static class DemoRefusal
{
    public static IResult Result() => Problems.Json(409, "demo_data", "This is demo data, so this action is not available. You can remove the demo data at any time.");
}

/// <summary>Routes with the order number in the path: cancel and return requests, delivery confirmation, return labels.</summary>
public sealed class DemoOrderGuard : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.Request.RouteValues["number"] is string number)
        {
            var account = http.Buyer().AccountId;
            var db = http.RequestServices.GetRequiredService<MarketplaceDb>();
            if (await db.Orders.AsNoTracking().AnyAsync(o => o.Number == number && o.AccountId == account && o.IsDemo, http.RequestAborted)) return DemoRefusal.Result();
        }
        return await next(context);
    }
}

/// <summary>Routes with the booking id in the path: cancel, reschedule, a time proposal, acknowledging a change.</summary>
public sealed class DemoBookingGuard : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.Request.RouteValues["id"] is string raw && Guid.TryParse(raw, out var id))
        {
            var account = http.Buyer().AccountId;
            var db = http.RequestServices.GetRequiredService<MarketplaceDb>();
            if (await db.Bookings.AsNoTracking().AnyAsync(b => b.Id == id && b.AccountId == account && b.IsDemo, http.RequestAborted)) return DemoRefusal.Result();
        }
        return await next(context);
    }
}

/// <summary>Sending a message (to a thread, an order or a listing that has a demo thread) and reporting a message in a demo thread.</summary>
public sealed class DemoMessageGuard : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var account = http.Buyer().AccountId;
        var db = http.RequestServices.GetRequiredService<MarketplaceDb>();
        var ct = http.RequestAborted;
        if (context.Arguments.OfType<SendBody>().FirstOrDefault() is { } send)
        {
            if (send.ThreadId is { } tid && await db.MessageThreads.AsNoTracking().AnyAsync(t => t.Id == tid && t.AccountId == account && t.IsDemo, ct)) return DemoRefusal.Result();
            if (send.ListingId is { } lid && await db.MessageThreads.AsNoTracking().AnyAsync(t => t.ListingId == lid && t.AccountId == account && t.IsDemo, ct)) return DemoRefusal.Result();
            if (!string.IsNullOrWhiteSpace(send.MarketplaceBookingId) && await db.Bookings.AsNoTracking().AnyAsync(b => b.MarketplaceBookingId == send.MarketplaceBookingId && b.AccountId == account && b.IsDemo, ct)) return DemoRefusal.Result();   // review L6: a demo booking is never written to
            if (send.ThreadId is { } btid && await (from t in db.MessageThreads.AsNoTracking() join b in db.Bookings.AsNoTracking() on t.BookingId equals b.Id where t.Id == btid && t.AccountId == account && b.IsDemo select t.Id).AnyAsync(ct)) return DemoRefusal.Result();
            if (!string.IsNullOrWhiteSpace(send.OrderNumber) && await db.Orders.AsNoTracking().AnyAsync(o => o.Number == send.OrderNumber && o.AccountId == account && o.IsDemo, ct)) return DemoRefusal.Result();
        }
        if (http.Request.RouteValues["id"] is string raw && Guid.TryParse(raw, out var id)
            && await (from m in db.ThreadMessages.AsNoTracking() join t in db.MessageThreads.AsNoTracking() on m.ThreadId equals t.Id where m.Id == id && t.AccountId == account && t.IsDemo select m.Id).AnyAsync(ct))
            return DemoRefusal.Result();
        return await next(context);
    }
}
