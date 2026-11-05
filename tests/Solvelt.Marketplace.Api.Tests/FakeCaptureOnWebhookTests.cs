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

/// <summary>The run-time guard: with a Stripe secret key configured the processor is the real one, so the option must do nothing to the in-process fake (mutant M8 of the review survived without this).</summary>
public sealed class FakeCaptureOnWebhookWithAKeyTests : OrderFlowBase
{
    public FakeCaptureOnWebhookWithAKeyTests() : base(new Dictionary<string, string?> { ["Stripe:FakeCaptureOnWebhook"] = "true", ["Stripe:SecretKey"] = "sk_test_journeyfakekey000000000000", ["Stripe:WebhookSecret"] = "whsec_dev_only_not_for_production_0000000" }) { }

    [Fact]
    public async Task With_a_secret_key_a_success_event_does_not_capture_the_fake()
    {
        var intent = await Stripe.CreateIntentAsync(5000, "usd", "guard-1", new Dictionary<string, string> { ["marketplace_order_id"] = "MP-GUARD-1" }, CancellationToken.None);
        await PostStripe(Event("payment_intent.succeeded", intent.Id, "MP-GUARD-1", 5000));   // (an unknown young order is a 5xx for Stripe to retry: only the fake's state matters here)
        var ex = await Assert.ThrowsAsync<PaymentRejectedException>(() => Stripe.RefundAsync(intent.Id, 5000, "guard-refund-1", new Dictionary<string, string>(), CancellationToken.None));
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
    [InlineData("Production", "true")]
    [InlineData("Staging", "true")]
    [InlineData("Production", " true")]          // the options binder trims: these ARE "on" at run time, so the refusal must see them too (review LOW-1)
    [InlineData("Staging", "True\t")]
    [InlineData("Production", "TRUE ")]
    public void It_is_refused_outside_development_and_testing(string environment, string value) => Assert.Contains(Problems(environment, value), p => p.Contains("FakeCaptureOnWebhook"));

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("yes")]
    public void Anything_that_is_not_true_is_not_a_request(string value) => Assert.DoesNotContain(Problems("Production", value), p => p.Contains("FakeCaptureOnWebhook"));

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
