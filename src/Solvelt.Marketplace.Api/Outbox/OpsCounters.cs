namespace Solvelt.Marketplace.Api.Outbox;

/// <summary>Counts since start-up, shown on <c>/health</c> so a monitor can alert on a rise: messages dead-lettered, admin requests refused.</summary>
public sealed class OpsCounters
{
    private long _dead, _adminDenied, _staffEnrolRefused, _catalogQuarantined;
    public long DeadLettered => Interlocked.Read(ref _dead);
    public long AdminDenied => Interlocked.Read(ref _adminDenied);
    /// <summary>Staff enrolment attempts with a link or bootstrap secret that did not work (alert on a rise: someone is guessing).</summary>
    public long StaffEnrolRefused => Interlocked.Read(ref _staffEnrolRefused);
    /// <summary>Portal listing documents set aside as unreadable (a gauge, not a count since start-up: the table <c>catalog_quarantine</c> is the truth, the sync run that holds the lease refreshes this after each run). Alert when it rises.</summary>
    public long CatalogQuarantined => Interlocked.Read(ref _catalogQuarantined);
    public void SetCatalogQuarantined(long n) => Interlocked.Exchange(ref _catalogQuarantined, n);
    public void StaffEnrolRefusedOnce() => Interlocked.Increment(ref _staffEnrolRefused);
    public void MessageDied() => Interlocked.Increment(ref _dead);
    public void AdminRefused() => Interlocked.Increment(ref _adminDenied);
}
