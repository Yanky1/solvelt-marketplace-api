using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Payments;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Which row a failed-refund event is about (review MED-1): judged on the row read under the lock, not on how it was found.</summary>
public sealed class RefundFailedMatchTests
{
    private static OrderRefund Row(string status, string? stripeId, string key = "portal:R1") => new() { Key = key, Status = status, StripeRefundId = stripeId };

    [Fact]
    public void A_row_finalized_as_succeeded_with_the_same_id_matches_even_when_found_by_key()
        => Assert.True(StripeEventProcessor.RefundFailedMatch(Row(RefundStatus.Succeeded, "re_1"), "re_1", "portal:R1"));

    [Fact]
    public void A_succeeded_row_matches_without_metadata()
        => Assert.True(StripeEventProcessor.RefundFailedMatch(Row(RefundStatus.Succeeded, "re_1"), "re_1", null));

    [Fact]
    public void A_succeeded_row_with_another_id_and_the_same_key_is_not_this_refund()
        => Assert.False(StripeEventProcessor.RefundFailedMatch(Row(RefundStatus.Succeeded, "re_2"), "re_1", "portal:R1"));

    [Fact]
    public void A_reserved_row_without_id_matches_by_key()
        => Assert.True(StripeEventProcessor.RefundFailedMatch(Row(RefundStatus.Reserved, null), "re_1", "portal:R1"));

    [Fact]
    public void A_reserved_row_never_matches_without_the_key_or_with_another_key()
    {
        Assert.False(StripeEventProcessor.RefundFailedMatch(Row(RefundStatus.Reserved, null), "re_1", null));
        Assert.False(StripeEventProcessor.RefundFailedMatch(Row(RefundStatus.Reserved, null), "re_1", "portal:OTHER"));
    }

    [Fact]
    public void A_failed_or_reserved_row_with_an_id_does_not_match()
    {
        Assert.False(StripeEventProcessor.RefundFailedMatch(Row(RefundStatus.Failed, "re_1"), "re_1", "portal:R1"));
        Assert.False(StripeEventProcessor.RefundFailedMatch(Row(RefundStatus.Reserved, "re_1"), "re_1", "portal:R1"));
    }
}
