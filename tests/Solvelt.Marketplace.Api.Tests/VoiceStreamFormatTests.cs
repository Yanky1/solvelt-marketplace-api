using System.Text.Json;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The stream stays PCMU (decision V-38); the only code is a guard that notices a different audio format on the start event and says so once.</summary>
public sealed class VoiceStreamFormatTests
{
    private static JsonElement Start(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("""{"streamSid":"MZ1"}""")]                                                                                  // no media format claimed
    [InlineData("""{"streamSid":"MZ1","mediaFormat":{}}""")]
    [InlineData("""{"mediaFormat":{"encoding":"audio/x-mulaw","sampleRate":8000,"channels":1}}""")]                        // what a phone-number call is
    [InlineData("""{"mediaFormat":{"encoding":"audio/x-mulaw"}}""")]
    public void Mulaw_8k_or_no_claim_is_expected(string start) => Assert.False(StreamFormat.IsUnexpected(Start(start), out _, out _));

    [Theory]
    [InlineData("""{"mediaFormat":{"encoding":"audio/x-L16","sampleRate":16000}}""", "audio/x-L16", 16000)]
    [InlineData("""{"mediaFormat":{"encoding":"audio/x-L16","sampleRate":24000,"channels":1}}""", "audio/x-L16", 24000)]
    [InlineData("""{"mediaFormat":{"encoding":"audio/x-mulaw","sampleRate":16000}}""", "audio/x-mulaw", 16000)]
    [InlineData("""{"mediaFormat":{"encoding":"audio/x-mulaw; evil\nline","sampleRate":8000}}""", "unrecognised", 8000)]      // a label from the network is only logged when it is short and plain
    public void Any_other_format_is_noticed_and_only_a_plain_label_is_kept(string start, string encoding, int rate)
    {
        Assert.True(StreamFormat.IsUnexpected(Start(start), out var e, out var r));
        Assert.Equal((encoding, rate), (e, r));
    }

    private static async Task<List<string>> WarningsFor(string startJson, int starts = 1)
    {
        var fake = new FakeRealtime();
        using var f = VoiceFactory.Create(fake);
        using var phone = await FakePhone.Connect(f, await VoiceFactory.Answer(f.CreateClient()));
        for (var i = 0; i < starts; i++) await phone.Send(JsonSerializer.Deserialize<object>(startJson)!);
        await fake.Wait("session.update");
        await Task.Delay(200);
        return f.Logs.Lines.Where(l => l.Contains("V-38")).ToList();
    }

    [Fact]
    public async Task A_stream_that_is_not_mulaw_8k_is_one_warning_in_the_log_and_the_call_carries_on_with_no_audio_value_in_it()
    {
        var warn = await WarningsFor("""{"event":"start","start":{"streamSid":"MZ1","mediaFormat":{"encoding":"audio/x-L16","sampleRate":16000,"channels":1}},"streamSid":"MZ1"}""", starts: 2);
        var line = Assert.Single(warn);                                                                                      // once per stream, even when the start event repeats
        Assert.Contains("audio/x-L16", line); Assert.Contains("16000", line);
    }

    [Fact]
    public async Task A_normal_mulaw_start_logs_nothing()
        => Assert.Empty(await WarningsFor("""{"event":"start","start":{"streamSid":"MZ1","mediaFormat":{"encoding":"audio/x-mulaw","sampleRate":8000,"channels":1}},"streamSid":"MZ1"}"""));
}
