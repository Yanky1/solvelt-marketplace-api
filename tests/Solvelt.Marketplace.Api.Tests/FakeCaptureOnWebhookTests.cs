using Solvelt.Marketplace.Api.Payments;
using Solvelt.Marketplace.Api.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// The real-stack journey (tools/journey) plays Stripe by posting the signed payment_intent.succeeded event; the in-process fake provider then has to know the money was captured, or a later
/// refund of that payment is refused (charge_exceeds_amount). That is opt-in (decision M8-07): off, the fake behaves exactly as before.
/// </summary>
public sealed class FakeCaptureOnWebhookOnTests : OrderFlowBase
{
    public FakeCaptureOnWebhookOnTests() : base(new Dictionary<string, string?> { ["Stripe:FakeCaptureOnWebhook"] = "true" }) { }

    [Fact]
    public async Task A_signed_success_event_captures_the_fake_intent_so_it_can_be_refunded()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        var res = await PostStripe(Event("payment_intent.succeeded", intent, number, total));   // no Stripe.Capture(): the webhook alone does it
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var refund = await Stripe.RefundAsync(intent, total, "journey-refund-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.Equal(total, refund.AmountCents);
    }
}

public sealed class FakeCaptureOnWebhookOffTests : OrderFlowBase
{
    [Fact]
    public async Task Without_the_option_a_success_event_does_not_capture_the_fake_intent()
    {
        await Login();
        await AddToCart("kiddush-cup-pewter");
        var (number, intent, total) = await Checkout();
        Assert.True((await PostStripe(Event("payment_intent.succeeded", intent, number, total))).IsSuccessStatusCode);
        var ex = await Assert.ThrowsAsync<PaymentRejectedException>(() => Stripe.RefundAsync(intent, total, "journey-refund-2", new Dictionary<string, string>(), CancellationToken.None));
        Assert.Equal("charge_exceeds_amount", ex.Code);
    }
}

public sealed class FakeCaptureOnWebhookStartupTests
{
    private static IReadOnlyList<string> Problems(string environment, string? flag)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:FakeCaptureOnWebhook"] = flag }).Build();
        return StartupValidator.Validate(config, new FakeEnv(environment));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void It_is_refused_outside_development_and_testing(string environment) => Assert.Contains(Problems(environment, "true"), p => p.Contains("FakeCaptureOnWebhook"));

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void It_is_allowed_in_development_and_testing(string environment) => Assert.DoesNotContain(Problems(environment, "true"), p => p.Contains("FakeCaptureOnWebhook"));

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
