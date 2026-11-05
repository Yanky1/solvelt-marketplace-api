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

    [Theory]
    [InlineData("Production")] [InlineData("Staging")] [InlineData("production")] [InlineData("")] [InlineData("Anything")]
    public void The_application_refuses_to_start_with_demo_data_on_outside_development_and_testing(string env)
    {
        Assert.True(Refused(env, "true"), env);
        Assert.True(Refused(env, "TRUE"), env);
        Assert.True(Refused(env, " True "), env);
    }

    [Theory]
    [InlineData("Production")] [InlineData("Staging")]
    public void Off_unset_or_blank_is_fine_everywhere(string env)
    {
        Assert.False(Refused(env, "false"));
        Assert.False(Refused(env, null));
        Assert.False(Refused(env, ""));
    }

    [Theory]
    [InlineData("Development")] [InlineData("Testing")]
    public void Development_and_testing_may_switch_it_on(string env) => Assert.False(Refused(env, "true"));

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
