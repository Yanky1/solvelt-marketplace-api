namespace Solvelt.Marketplace.Api.Demo;

/// <summary>
/// A top-level row that "Fill with demo data" may create (M9): an address, a cart, an order, a booking, a message thread, a notification. <see cref="IsDemo"/> is what
/// "Remove demo data" and every job that talks to the outside world look at; <see cref="DemoBatchId"/> says which press created it (the audit log carries the same id).
/// Children (cart lines, order groups and lines, messages, shipments) are not marked: they go with their parent.
/// </summary>
public interface IDemoMarked
{
    bool IsDemo { get; set; }
    Guid? DemoBatchId { get; set; }
}
