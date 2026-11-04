using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

public class FoundationTests
{
    private sealed record Problem(string Code, string Message);

    [Fact]
    public async Task Health_is_ok()
    {
        using var f = new TestFactory();
        var res = await f.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_route_is_a_code_message_404()
    {
        using var f = new TestFactory();
        var res = await f.CreateClient().GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var p = await res.Content.ReadFromJsonAsync<Problem>();
        Assert.Equal("not_found", p!.Code);
    }

    [Fact]
    public async Task Wrong_method_is_a_code_message_405()
    {
        using var f = new TestFactory();
        var res = await f.CreateClient().PostAsync("/health", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
        Assert.Equal("method_not_allowed", (await res.Content.ReadFromJsonAsync<Problem>())!.Code);
    }

    [Fact]
    public async Task Public_endpoints_are_rate_limited_with_a_problem_body()
    {
        using var f = new TestFactory(new() { ["RateLimits:PublicPerMinute"] = "3" });
        var client = f.CreateClient();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/ping")).StatusCode);
        var res = await client.GetAsync("/api/v1/ping");
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal("rate_limited", (await res.Content.ReadFromJsonAsync<Problem>())!.Code);
    }

    private static IConfiguration Config(Dictionary<string, string?> d) => new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static Dictionary<string, string?> GoodProd() => new()
    {
        ["Portal:InboundSecret"] = new string('a', 40),
        ["Portal:WebhookSecret"] = new string('b', 40),
        ["Web:ForwardingKey"] = new string('c', 40), ["Auth:EmailLinkKey"] = new string('e', 40), ["Email:Provider"] = "Smtp", ["Email:SmtpHost"] = "smtp.example", ["Email:From"] = "no-reply@shop.example",
        ["Portal:BaseUrl"] = "https://portal.example",
        ["Marketplace:RelayDomain"] = "relay.example",
        ["Marketplace:PublicBaseUrl"] = "https://shop.example",
        ["Database:Provider"] = "Postgres",
        ["ConnectionStrings:Default"] = "Host=db;Database=mp",
    };

    [Fact]
    public void Production_with_complete_config_is_valid()
        => Assert.Empty(StartupValidator.Validate(Config(GoodProd()), new Env("Production")));

    [Theory]
    [InlineData("Portal:InboundSecret")]
    [InlineData("Portal:WebhookSecret")]
    [InlineData("Portal:BaseUrl")]
    [InlineData("Marketplace:RelayDomain")]
    [InlineData("ConnectionStrings:Default")]
    public void Production_fails_when_a_required_value_is_missing(string key)
    {
        var d = GoodProd();
        d.Remove(key);
        Assert.NotEmpty(StartupValidator.Validate(Config(d), new Env("Production")));
    }

    [Fact]
    public void Production_fails_when_inbound_and_webhook_secrets_are_equal()
    {
        var d = GoodProd();
        d["Portal:WebhookSecret"] = d["Portal:InboundSecret"];
        Assert.Contains(StartupValidator.Validate(Config(d), new Env("Production")), p => p.Contains("must differ"));
    }

    [Fact]
    public void Production_rejects_dev_default_secrets_and_short_secrets()
    {
        var d = GoodProd();
        d["Portal:InboundSecret"] = DevDefaults.PortalInboundSecret;
        d["Portal:WebhookSecret"] = "short";
        var errs = StartupValidator.Validate(Config(d), new Env("Production"));
        Assert.Contains(errs, p => p.Contains("Development default"));
        Assert.Contains(errs, p => p.Contains("at least 32"));
    }

    [Fact]
    public void App_refuses_to_start_in_Production_without_secrets()
    {
        using var f = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Portal:InboundSecret", ex.ToString());
    }
}
