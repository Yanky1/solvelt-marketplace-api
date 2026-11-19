using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Tests.Infrastructure;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The Realtime line behind the pipeline seam (decision V-19) keeps the old behaviour when the model session cannot be opened.</summary>
public sealed class VoiceRealtimePipelineTests
{
    [Fact]
    public async Task When_the_model_session_cannot_be_opened_the_error_type_is_logged_the_call_is_Abandoned_and_the_phone_side_gets_a_503()
    {
        var fake = new FakeRealtime { Fail = true };
        using var f = VoiceFactory.Create(fake);
        var tok = await VoiceFactory.Answer(f.CreateClient());

        // the stream endpoint refuses the upgrade: HTTP 503, not an accepted socket
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => FakePhone.Connect(f, tok));
        Assert.Contains("503", ex.ToString());

        using var scope = f.Services.CreateScope();
        var call = await scope.ServiceProvider.GetRequiredService<MarketplaceDb>().VoiceCalls.AsNoTracking().SingleAsync();
        Assert.Equal(VoiceOutcome.Abandoned, call.Outcome);
        Assert.NotNull(call.EndedAt);

        // what is logged is the TYPE of the failure, never its message (which may name the host or carry a key)
        var line = Assert.Single(f.Logs.Lines, l => l.Contains("could not be opened"));
        Assert.Contains(nameof(HttpRequestException), line);
        Assert.DoesNotContain("upstream down", line);
        Assert.DoesNotContain("test-key-not-real", line);
    }
}
