using Microsoft.Extensions.Caching.Memory;

namespace Solvelt.Marketplace.Api.Demo;

/// <summary>
/// A per-account limit on "Fill" and "Remove" (security review L4): each one writes or deletes dozens of rows in one transaction, so a script pressing the buttons in a loop is a
/// cheap way to load the database. At most <see cref="Limit"/> presses per <see cref="Window"/> per account, counted together. The counters live in a memory cache with an expiry,
/// so the dictionary cannot grow without bound (an entry disappears a window after its account was last seen). Per server instance, which is what a safety valve needs.
/// </summary>
public sealed class DemoActionLimiter(TimeProvider time)
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions());
    public const int Limit = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private sealed class Counter { public readonly object Gate = new(); public DateTimeOffset Start; public int Count; }

    /// <summary>Null when the press is allowed (and counted); otherwise the seconds to wait.</summary>
    public int? TryAcquire(Guid accountId)
    {
        var now = time.GetUtcNow();
        var counter = cache.GetOrCreate($"demo-actions:{accountId:N}", e => { e.SlidingExpiration = Window * 2; return new Counter { Start = now }; })!;
        lock (counter.Gate)
        {
            if (now - counter.Start >= Window) { counter.Start = now; counter.Count = 0; }
            if (counter.Count >= Limit) return Math.Max(1, (int)Math.Ceiling((counter.Start + Window - now).TotalSeconds));
            counter.Count++;
            return null;
        }
    }
}
