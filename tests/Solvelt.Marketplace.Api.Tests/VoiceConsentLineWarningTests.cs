using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Solvelt.Marketplace.Api.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>V-45: a consent line that says to press a key is warned about once at start-up (the owner's wording is never edited in code), and nothing else is.</summary>
public sealed class VoiceConsentLineWarningTests
{
    private static string[] Warn(string? line)
    {
        var kv = line is null ? [] : new[] { new KeyValuePair<string, string?>("Voice:YiddishLabs:ConsentLine", line) };
        return StartupValidator.Warnings(new ConfigurationBuilder().AddInMemoryCollection(kv).Build(), new Env()).ToArray();
    }

    [Theory]
    [InlineData("To continue, press 1.")]
    [InlineData("Press one to agree")]
    [InlineData("please PRESS a key")]
    public void A_consent_line_that_says_press_is_warned_about_once(string line)
    {
        var w = Assert.Single(Warn(line));
        Assert.Contains("Voice:YiddishLabs:ConsentLine", w);
        Assert.DoesNotContain(line, w);                                   // the owner's words are not echoed
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("This call uses a third party. Say yes to continue.")]
    public void A_line_without_press_or_no_line_says_nothing(string? line) => Assert.Empty(Warn(line));

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
