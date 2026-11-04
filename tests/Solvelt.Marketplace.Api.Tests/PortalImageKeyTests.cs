using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Portal;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The separate image-download key (M2-23; portal N3: Orders:ImageAcceptInboundKey): which key signs which call, and what start-up says about it.</summary>
public class PortalImageKeyTests
{
    private const string Inbound = "unit-test-inbound-secret-0123456789abcdef", Image = "unit-test-IMAGE-key-0123456789abcdefghij";
    private static readonly Guid ImageId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static SignedPortalClient Client(FakePortal portal, FakeTimeProvider clock, string? inbound, string? imageKey)
        => new(new HttpClient(portal) { BaseAddress = new Uri("https://portal.test"), Timeout = Timeout.InfiniteTimeSpan },
            Options.Create(new PortalOptions { BaseUrl = "https://portal.test", InboundSecret = inbound, ImageKey = imageKey, RetryBaseDelayMs = 0, MaxAttempts = 1 }), clock);

    private static HttpResponseMessage Png() => new(HttpStatusCode.OK) { Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]) { Headers = { ContentType = new("image/png") } } };

    // ---------- the client ----------
    [Fact]
    public async Task With_an_image_key_the_image_download_is_signed_with_it_and_every_other_call_still_uses_the_inbound_key()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var imagePortal = new FakePortal(Image, clock).RespondAlways(_ => Png());           // a portal that knows ONLY the image key
        var client = Client(imagePortal, clock, Inbound, Image);
        var ok = await client.GetCatalogImageAsync(ImageId);
        Assert.True(ok.IsSuccess);
        Assert.True(Assert.Single(imagePortal.Calls).Verified);
        var feed = await client.GetCatalogCategoriesAsync();                                // the inbound key signs this: the image-key portal refuses it
        Assert.Equal(401, feed.Status);
        Assert.Equal("bad_signature", feed.Error?.Code);
    }

    [Fact]
    public async Task The_image_key_is_not_accepted_for_anything_but_images_by_a_portal_that_knows_only_the_inbound_key()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var inboundPortal = new FakePortal(Inbound, clock).RespondAlways(_ => Png());      // the portal after Orders:ImageAcceptInboundKey=false would refuse this; here we show the keys differ
        var res = await Client(inboundPortal, clock, Inbound, Image).GetCatalogImageAsync(ImageId);
        Assert.Equal(401, res.Status);                                                      // signed with the image key, the inbound-only portal does not accept it
        Assert.False(res.IsSuccess);
    }

    [Fact]
    public async Task Without_an_image_key_the_inbound_key_signs_image_downloads_as_before()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var portal = new FakePortal(Inbound, clock).RespondAlways(_ => Png());
        foreach (var empty in new string?[] { null, "" })
            Assert.True((await Client(portal, clock, Inbound, empty).GetCatalogImageAsync(ImageId)).IsSuccess);
    }

    [Fact]
    public async Task With_no_key_at_all_the_call_is_refused_before_anything_is_sent()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        var portal = new FakePortal(Inbound, clock);
        await Assert.ThrowsAsync<PortalNotConfiguredException>(() => Client(portal, clock, null, null).GetCatalogImageAsync(ImageId));
        Assert.Empty(portal.Calls);
    }

    // ---------- start-up ----------
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] extra)
    {
        var d = new Dictionary<string, string?>
        {
            ["Portal:InboundSecret"] = new string('a', 40), ["Portal:WebhookSecret"] = new string('b', 40), ["Web:ForwardingKey"] = new string('c', 40), ["Auth:EmailLinkKey"] = new string('e', 40),
            ["Auth:GuestLinkKey"] = new string('g', 40), ["Notifications:UnsubscribeKey"] = new string('n', 40),
            ["Portal:BaseUrl"] = "https://portal.example", ["Marketplace:RelayDomain"] = "relay.example", ["Marketplace:PublicBaseUrl"] = "https://shop.example",
            ["Database:Provider"] = "Postgres", ["ConnectionStrings:Default"] = "Host=db;Database=mp", ["Email:Provider"] = "Smtp", ["Email:SmtpHost"] = "smtp.example", ["Email:From"] = "shop@example.com",
        };
        foreach (var (k, v) in extra) d[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }
    private static IReadOnlyList<string> Problems(params (string, string?)[] extra) => StartupValidator.Validate(Config(extra), new Env("Production"));

    [Fact]
    public void The_image_key_is_optional_until_it_is_made_required()
    {
        Assert.Empty(Problems());
        Assert.Empty(Problems(("Portal:ImageKey", new string('i', 40))));
        var required = Problems(("Portal:RequireImageKey", "true"));
        Assert.Contains(required, p => p.Contains("Portal:ImageKey") && p.Contains("required"));
        Assert.Empty(Problems(("Portal:RequireImageKey", "true"), ("Portal:ImageKey", new string('i', 40))));
        Assert.Empty(Problems(("Portal:RequireImageKey", "false")));
    }

    [Fact]
    public void The_image_key_must_differ_from_every_other_key_and_be_long_enough()
    {
        Assert.Contains(Problems(("Portal:ImageKey", new string('a', 40))), p => p.Contains("Portal:ImageKey") && p.Contains("Portal:InboundSecret") && p.Contains("differ"));
        Assert.Contains(Problems(("Portal:ImageKey", new string('b', 40))), p => p.Contains("Portal:ImageKey") && p.Contains("Portal:WebhookSecret"));
        Assert.Contains(Problems(("Portal:ImageKey", new string('c', 40))), p => p.Contains("Portal:ImageKey") && p.Contains("Web:ForwardingKey"));
        Assert.Contains(Problems(("Portal:ImageKey", "short")), p => p.Contains("Portal:ImageKey") && p.Contains("32"));
    }

    [Fact]
    public void Production_warns_while_the_image_key_is_missing_and_stays_quiet_when_it_is_set_or_in_development()
    {
        var warnings = StartupValidator.Warnings(Config(), new Env("Production"));
        Assert.Contains(warnings, w => w.Contains("Portal:ImageKey") && w.Contains("Portal:InboundSecret"));
        Assert.Empty(StartupValidator.Warnings(Config(("Portal:ImageKey", new string('i', 40))), new Env("Production")));
        Assert.Empty(StartupValidator.Warnings(Config(), new Env("Development")));
        Assert.Empty(StartupValidator.Warnings(Config(), new Env("Testing")));
        Assert.DoesNotContain(warnings, w => w.Contains(new string('a', 40)));                    // a warning never carries a key
    }
}
