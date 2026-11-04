namespace Solvelt.Marketplace.Api.Outbox;

/// <summary>Counts since start-up, shown on <c>/health</c> so a monitor can alert on a rise: messages dead-lettered, admin requests refused.</summary>
public sealed class OpsCounters
{
    private long _dead, _adminDenied;
    public long DeadLettered => Interlocked.Read(ref _dead);
    public long AdminDenied => Interlocked.Read(ref _adminDenied);
    public void MessageDied() => Interlocked.Increment(ref _dead);
    public void AdminRefused() => Interlocked.Increment(ref _adminDenied);
}
