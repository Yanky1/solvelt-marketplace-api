using System.Net;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;
using Solvelt.Marketplace.Api.Voice.TestSet;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Recording the recognition test set by phone (decision V-46): the owner's test numbers only, off by default; the audio is copied into the private container and then deleted at SignalWire.</summary>
public sealed class VoiceTestSetPhoneTests : IDisposable
{
    private const string Number = "+15551234567", Other = "+15557770000";
    private VoiceScenario? _s;
    private MemoryTestSetStore _store = new();
    public void Dispose() => _s?.Dispose();
    private VoiceScenario S => _s!;

    private static Dictionary<string, string?> On(bool testSet = true, string[]? numbers = null)
        => VoicePipelineMenuTests.MenuConfig(numbers, new() { ["Voice:TestSet:Enabled"] = testSet ? "true" : "false", ["Voice:TestSet:StorageAccountUrl"] = "https://acct.blob.core.windows.net", ["Voice:TestSet:SetName"] = "set1", ["Voice:TestSet:MaxRecordSeconds"] = "15" });

    private async Task Start(bool testSet = true, string from = Number, string[]? numbers = null)
    {
        _store = new MemoryTestSetStore();
        _s = new VoiceScenario(from, On(testSet, numbers), services: s => { s.RemoveAll<ITestSetStore>(); s.AddSingleton<ITestSetStore>(_store); });
        await S.SyncCatalog();
        Assert.Equal(HttpStatusCode.OK, (await VoiceFactory.Hook(S.C, VoiceEndpoints.InboundPath, S.Sid, from)).StatusCode);
    }

    private async Task<(HttpResponseMessage Res, XElement Xml)> Post(string path, string from = Number, Dictionary<string, string>? extra = null, string? signature = null)
    {
        var r = await VoiceFactory.Hook(S.C, path, S.Sid, from, signature, extra);
        return (r, r.IsSuccessStatusCode && r.Content.Headers.ContentLength != 0 && (r.Content.Headers.ContentType?.MediaType ?? "") == "text/xml" ? XDocument.Parse(await r.Content.ReadAsStringAsync()).Root! : new XElement("none"));
    }
    private Task<(HttpResponseMessage Res, XElement Xml)> Step(string query, string from = Number, Dictionary<string, string>? extra = null, string? signature = null)
        => Post(TestSetFlow.Path + "?" + query, from, extra, signature);
    private Task<(HttpResponseMessage Res, XElement Xml)> Menu(string digits, string from = Number) => Post(VoiceEndpoints.PipelinePath, from, new() { ["Digits"] = digits });

    private static string Speaker => "p" + "0a1b2c";

    // ---------- reaching it ----------

    [Fact]
    public async Task The_menu_offers_the_test_set_and_nine_starts_it_only_when_switched_on()
    {
        await Start();
        var xml = (await Post(VoiceEndpoints.InboundPath)).Xml;
        Assert.Contains(TestSetFlow.MenuLine, xml.Descendants("Say").Select(e => e.Value).Last());
        var nine = (await Menu("9")).Xml;
        var redirect = nine.Element("Redirect")!.Value;
        Assert.Contains(TestSetFlow.Path + "?step=phrase&i=0&t=1&b=p", redirect);
        Assert.Null(nine.Descendants("Stream").FirstOrDefault());
        Assert.Contains("40 phrases", nine.Element("Say")!.Value);
        _s!.Dispose();

        await Start(testSet: false);
        var off = (await Post(VoiceEndpoints.InboundPath)).Xml;
        Assert.DoesNotContain(TestSetFlow.MenuLine, string.Join(" ", off.Descendants("Say").Select(e => e.Value)));
        var nineOff = (await Menu("9")).Xml;
        Assert.Null(nineOff.Descendants("Redirect").FirstOrDefault(r => r.Value.Contains(TestSetFlow.Path)));          // nine is an unknown key again: the standard line
        Assert.NotNull(nineOff.Descendants("Stream").SingleOrDefault());
    }

    [Fact]
    public async Task The_speaker_is_an_anonymous_label_and_never_the_number()
    {
        await Start();
        var url = (await Menu("9")).Xml.Element("Redirect")!.Value;
        Assert.DoesNotContain("5551234567", url);
        Assert.Matches("b=p[0-9a-f]{6}$", url);
        var o = new VoiceOptions { CallerHashKey = new string('k', 40) };
        Assert.Equal(TestSetFlow.SpeakerLabel(o, "+15551234567"), TestSetFlow.SpeakerLabel(o, "(555) 123-4567"));          // the same number, the same label
        Assert.NotEqual(TestSetFlow.SpeakerLabel(o, "+15551234567"), TestSetFlow.SpeakerLabel(o, "+15557770000"));
        Assert.NotEqual(TestSetFlow.SpeakerLabel(o, "+15551234567"), TestSetFlow.SpeakerLabel(new VoiceOptions { CallerHashKey = new string('j', 40) }, "+15551234567"));
        Assert.True(RecordingName.TryParse($"set1/en-01-{TestSetFlow.RecordingLabel(TestSetFlow.SpeakerLabel(o, "+15551234567"), "CA123", 2)}.wav", out _));   // and it fits a recording name
    }

    // ---------- the steps ----------

    [Fact]
    public async Task A_phrase_is_read_and_recorded_with_the_attributes_the_provider_documents_and_no_transcription()
    {
        await Start();
        var (_, x) = await Step($"step=phrase&i=1&t=1&b={Speaker}");
        Assert.Equal("Phrase 2. Do you have a pewter kiddush cup?", x.Element("Say")!.Value);
        var rec = x.Element("Record")!;
        Assert.Equal(("true", "trim-silence", "3", "15", "false"), (rec.Attribute("playBeep")!.Value, rec.Attribute("trim")!.Value, rec.Attribute("timeout")!.Value, rec.Attribute("maxLength")!.Value, rec.Attribute("transcribe")!.Value));
        Assert.Null(rec.Attribute("finishOnKey"));                                                                          // speech driven: no key
        Assert.Equal("completed", rec.Attribute("recordingStatusCallbackEvent")!.Value);
        Assert.Contains("step=done&i=1&t=1&b=" + Speaker, rec.Attribute("action")!.Value);
        Assert.Contains("step=recorded&i=1&t=1&b=" + Speaker, rec.Attribute("recordingStatusCallback")!.Value);
        Assert.Equal(1, x.Elements().Count(e => e.Name == "Record"));
    }

    [Fact]
    public async Task A_yiddish_phrase_is_not_read_by_the_voice_the_speaker_reads_it_from_the_list()
    {
        await Start();
        var (_, x) = await Step($"step=phrase&i=25&t=1&b={Speaker}");      // yi-01
        Assert.Equal("Phrase 26 is Yiddish. Read it from your list.", x.Element("Say")!.Value);
        Assert.DoesNotMatch("[֐-׿]", x.Element("Say")!.Value);
        Assert.NotNull(x.Element("Record"));
    }

    [Fact]
    public async Task After_a_recording_the_speaker_says_next_again_or_stop_and_silence_is_asked_again_then_ends()
    {
        await Start();
        var done = (await Step($"step=done&i=0&t=1&b={Speaker}", extra: new() { ["RecordingDuration"] = "4" })).Xml;
        var g = done.Element("Gather")!;
        Assert.Equal(("speech", "next, again, stop"), (g.Attribute("input")!.Value, g.Attribute("hints")!.Value));
        Assert.Equal(TestSetFlow.AskNext, g.Element("Say")!.Value);
        Assert.Contains("step=next&i=0&t=1", g.Attribute("action")!.Value);
        // nothing was recorded: the phrase is offered again, three times, then the call ends
        var quiet = (await Step($"step=done&i=0&t=1&b={Speaker}", extra: new() { ["RecordingDuration"] = "0" })).Xml;
        Assert.Equal(TestSetFlow.NothingHeard, quiet.Element("Say")!.Value);
        Assert.Contains("step=phrase&i=0&t=1&b=" + Speaker + "&k=1", quiet.Element("Redirect")!.Value);
        var last = (await Step($"step=done&i=0&t=1&b={Speaker}&k=2", extra: new() { ["RecordingDuration"] = "0" })).Xml;
        Assert.NotNull(last.Element("Hangup"));
    }

    [Theory]
    [InlineData("next", "step=phrase&i=4&t=1&b=")]
    [InlineData("okay, next one", "step=phrase&i=4&t=1&b=")]
    [InlineData("again", "step=phrase&i=3&t=2&b=")]
    [InlineData("let me do that again", "step=phrase&i=3&t=2&b=")]
    public async Task Next_and_again_move_the_phrase_or_the_take(string said, string expected)
    {
        await Start();
        var x = (await Step($"step=next&i=3&t=1&b={Speaker}", extra: new() { ["SpeechResult"] = said })).Xml;
        Assert.Contains(expected, x.Element("Redirect")!.Value);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("I am done")]
    public async Task Stop_says_goodbye_and_hangs_up(string said)
    {
        await Start();
        var x = (await Step($"step=next&i=3&t=1&b={Speaker}", extra: new() { ["SpeechResult"] = said })).Xml;
        Assert.Equal(TestSetFlow.Goodbye, x.Element("Say")!.Value);
        Assert.NotNull(x.Element("Hangup"));
    }

    [Fact]
    public async Task Something_unclear_is_asked_once_more_and_then_ends_and_the_last_phrase_finishes_the_set()
    {
        await Start();
        var again = (await Step($"step=next&i=3&t=1&b={Speaker}", extra: new() { ["SpeechResult"] = "banana" })).Xml;
        Assert.Equal(TestSetFlow.AskNextAgain, again.Element("Gather")!.Element("Say")!.Value);
        Assert.Contains("&k=1", again.Element("Gather")!.Attribute("action")!.Value);
        var end = (await Step($"step=next&i=3&t=1&b={Speaker}&k=1", extra: new() { ["SpeechResult"] = "" })).Xml;
        Assert.NotNull(end.Element("Hangup"));
        var finish = (await Step($"step=next&i=39&t=1&b={Speaker}", extra: new() { ["SpeechResult"] = "next" })).Xml;
        Assert.StartsWith("That was the last phrase.", finish.Element("Say")!.Value);
        Assert.NotNull(finish.Element("Hangup"));
        var capped = (await Step($"step=next&i=3&t=9&b={Speaker}", extra: new() { ["SpeechResult"] = "again" })).Xml;
        Assert.Null(capped.Element("Redirect"));                                                                            // nine takes of one phrase is the limit
    }

    [Theory]
    [InlineData("next", TestSetIntent.Next)]
    [InlineData("Next.", TestSetIntent.Next)]
    [InlineData("again", TestSetIntent.Again)]
    [InlineData("stop", TestSetIntent.Stop)]
    [InlineData("stop, then again", TestSetIntent.Stop)]          // stop beats again beats next
    [InlineData("", TestSetIntent.Unclear)]
    [InlineData(null, TestSetIntent.Unclear)]
    [InlineData("one two three four five six seven", TestSetIntent.Unclear)]   // too long to be an answer
    public void The_spoken_answer_is_parsed_with_stop_first(string? said, TestSetIntent expected) => Assert.Equal(expected, TestSetFlow.Parse(said));

    // ---------- the guards ----------

    [Fact]
    public async Task Every_step_is_signed_for_test_numbers_only_and_off_when_switched_off()
    {
        await Start();
        Assert.Equal(HttpStatusCode.Forbidden, (await Step($"step=phrase&i=0&t=1&b={Speaker}", signature: "forged")).Res.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Step($"step=phrase&i=0&t=1&b={Speaker}", from: Other)).Res.StatusCode);              // not a test number
        foreach (var bad in new[] { "step=phrase&i=40&t=1&b=" + Speaker, "step=phrase&i=-1&t=1&b=" + Speaker, "step=phrase&i=0&t=0&b=" + Speaker, "step=phrase&i=0&t=1&b=../x", "step=phrase&i=0&t=1&b=", "step=nonsense&i=0&t=1&b=" + Speaker })
            Assert.Equal(HttpStatusCode.BadRequest, (await Step(bad)).Res.StatusCode);
        _s!.Dispose();
        await Start(testSet: false);
        Assert.Equal(HttpStatusCode.NotFound, (await Step($"step=phrase&i=0&t=1&b={Speaker}")).Res.StatusCode);
    }

    // ---------- the recording is queued for copying ----------

    private const string Sid = "RE0123456789abcdef0123456789abcdef";

    private async Task<List<OutboxMessage>> Jobs() => await S.K.Db(db => db.OutboxMessages.AsNoTracking().Where(m => m.Type == TestSetCopyHandler.Type).ToListAsync());

    [Fact]
    public async Task A_finished_recording_queues_one_copy_job_with_a_unique_name_and_a_repeated_callback_changes_nothing()
    {
        await Start();
        var extra = new Dictionary<string, string> { ["RecordingSid"] = Sid, ["RecordingStatus"] = "completed", ["RecordingDuration"] = "4" };
        Assert.Equal(HttpStatusCode.OK, (await Step($"step=recorded&i=1&t=2&b={Speaker}", extra: extra)).Res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Step($"step=recorded&i=1&t=2&b={Speaker}", extra: extra)).Res.StatusCode);       // SignalWire retries
        var job = Assert.Single(await Jobs());
        var p = System.Text.Json.JsonSerializer.Deserialize<TestSetCopyHandler.Payload>(job.Payload!, Solvelt.Marketplace.Api.Portal.PortalJson.Options)!;
        Assert.Equal(Sid, p.Sid);
        Assert.True(RecordingName.TryParse(p.Name, out var name));
        Assert.Equal(("set1", "en-02"), (name.Set, name.PhraseId));
        Assert.StartsWith(Speaker, name.Speaker);
        Assert.EndsWith("r2", name.Speaker);                                                                                // the second take
        Assert.Equal("voice.testset.copy:" + Sid, job.DedupeKey);
    }

    [Fact]
    public async Task Only_a_completed_recording_with_a_valid_sid_is_queued()
    {
        await Start();
        Assert.Equal(HttpStatusCode.OK, (await Step($"step=recorded&i=1&t=1&b={Speaker}", extra: new() { ["RecordingSid"] = Sid, ["RecordingStatus"] = "in-progress" })).Res.StatusCode);
        Assert.Empty(await Jobs());
        Assert.Equal(HttpStatusCode.BadRequest, (await Step($"step=recorded&i=1&t=1&b={Speaker}", extra: new() { ["RecordingSid"] = "../../etc", ["RecordingStatus"] = "completed" })).Res.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Step($"step=recorded&i=1&t=1&b={Speaker}", extra: new() { ["RecordingSid"] = Sid, ["RecordingStatus"] = "completed" }, signature: "forged")).Res.StatusCode);
        Assert.Empty(await Jobs());
    }

    [Fact]
    public async Task The_copy_handler_is_registered_with_the_outbox()
    {
        await Start();
        using var scope = S.F.Services.CreateScope();
        Assert.IsType<TestSetCopyHandler>(scope.ServiceProvider.GetRequiredService<OutboxHandlerRegistry>().Find(TestSetCopyHandler.Type));
    }
}
