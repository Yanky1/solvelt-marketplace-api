using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Demo;
using Solvelt.Marketplace.Api.Infrastructure;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Xunit;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>M9, the foundation: the flag, its refusal outside Development and Testing, the public config, and the 404 while it is off.</summary>
public sealed class DemoFoundationTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
    private static IConfiguration Config(string? demo) => new ConfigurationBuilder().AddInMemoryCollection(demo is null ? [] : new Dictionary<string, string?> { ["Demo:Enabled"] = demo }).Build();
    private static bool Refused(string env, string? demo) => StartupValidator.Validate(Config(demo), new Env(env)).Any(p => p.Contains("Demo:Enabled"));

    // M9-06: an allowlist. Demo:Enabled may be true only for Development, Testing and Staging (trimmed, case-insensitive); every other name refuses.
    [Theory]
    [InlineData("Production")] [InlineData("production")] [InlineData("PRODUCTION")] [InlineData("Prod")] [InlineData(" Production")] [InlineData("Production ")] [InlineData("Live")]
    [InlineData("Anything")] [InlineData("Demo")] [InlineData("Stagin")] [InlineData("Staging2")] [InlineData("Development-eu")] [InlineData("")] [InlineData("   ")]
    public void The_application_refuses_to_start_with_demo_data_on_in_any_environment_that_is_not_on_the_allowlist(string env)
    {
        Assert.True(Refused(env, "true"), env);
        Assert.True(Refused(env, "TRUE"), env);
        Assert.True(Refused(env, " True "), env);
    }

    [Theory]
    [InlineData("Staging")] [InlineData("staging")] [InlineData("STAGING")] [InlineData(" Staging ")] [InlineData("Development")] [InlineData("development")] [InlineData("Testing")] [InlineData(" testing")]
    public void Only_development_testing_and_staging_may_switch_it_on_so_the_test_site_can_run_as_staging(string env) => Assert.False(Refused(env, "true"), env);

    [Theory]
    [InlineData("Production")] [InlineData("Prod")] [InlineData("Live")]
    public void The_bound_value_is_what_is_checked_so_a_true_with_a_trailing_control_character_is_still_refused_where_demo_is_not_allowed(string env)
    {
        // the configuration binder's bool.Parse also trims control characters, so "true\0" is TRUE for the application though it is not the string "true"
        foreach (var v in new[] { "true\0", "true\0\0", " true\0 ", "TRUE\0" })
            Assert.True(Refused(env, v), $"{env} {v.Replace("\0", "<NUL>")}");
    }

    [Theory]
    [InlineData("Staging")] [InlineData("Development")] [InlineData("Testing")]
    public void The_same_values_are_fine_on_an_allowed_environment(string env)
    {
        foreach (var v in new[] { "true\0", "TRUE\0" }) Assert.False(Refused(env, v), env);
    }

    [Theory]
    [InlineData("Production")] [InlineData("Staging")] [InlineData("Development")]
    public void A_value_the_binder_cannot_read_refuses_the_start_in_every_environment(string env)
    {
        foreach (var v in new[] { "yes", "enabled", "tru", "2", "true false" })
            Assert.True(Refused(env, v), $"{env} {v}");
    }

    [Theory]
    [InlineData("Production")] [InlineData("Staging")] [InlineData("Live")]
    public void Off_unset_or_blank_is_fine_everywhere(string env)
    {
        Assert.False(Refused(env, "false"));
        Assert.False(Refused(env, null));
        Assert.False(Refused(env, ""));
    }

    [Fact]
    public async Task A_blank_value_is_off_all_the_way_the_application_starts_and_says_demoEnabled_false()
    {
        using var f = new TestFactory(new() { ["Demo:Enabled"] = "" });
        var res = await f.CreateClient().GetAsync("/api/v1/config");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("demoEnabled").GetBoolean());
    }

    [Fact]
    public void The_message_says_what_to_do()
        => Assert.Contains(StartupValidator.Validate(Config("true"), new Env("Production")), p => p.Contains("Demo:Enabled") && p.Contains("Staging"));

    [Fact]
    public void There_is_no_override_that_lets_production_start_with_it_on()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Enabled"] = "true", ["Demo:AllowInProduction"] = "true", ["Demo:Force"] = "true", ["ALLOW_DEMO"] = "1" }).Build();
        Assert.Contains(StartupValidator.Validate(cfg, new Env("Production")), p => p.Contains("Demo:Enabled"));
    }

    [Fact]
    public async Task The_public_config_says_whether_test_mode_is_on_and_is_off_by_default()
    {
        using var off = new TestFactory();
        var res = await off.CreateClient().GetAsync("/api/v1/config");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("demoEnabled").GetBoolean());

        using var on = new TestFactory(new() { ["Demo:Enabled"] = "true" });
        Assert.True(JsonDocument.Parse(await (await on.CreateClient().GetAsync("/api/v1/config")).Content.ReadAsStringAsync()).RootElement.GetProperty("demoEnabled").GetBoolean());
    }

    [Fact]
    public async Task The_filter_answers_404_while_disabled_and_lets_the_request_through_when_enabled()
    {
        var called = false;
        EndpointFilterDelegate next = _ => { called = true; return ValueTask.FromResult<object?>("through"); };
        var ctx = EndpointFilterInvocationContext.Create(new DefaultHttpContext());
        var off = await new DemoEnabledFilter(Options.Create(new DemoOptions { Enabled = false })).InvokeAsync(ctx, next);
        Assert.False(called);
        Assert.IsAssignableFrom<IResult>(off);
        var http = new DefaultHttpContext(); http.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        await ((IResult)off!).ExecuteAsync(http);
        Assert.Equal(404, http.Response.StatusCode);
        var on = await new DemoEnabledFilter(Options.Create(new DemoOptions { Enabled = true })).InvokeAsync(ctx, next);
        Assert.True(called);
        Assert.Equal("through", on);
    }
}
