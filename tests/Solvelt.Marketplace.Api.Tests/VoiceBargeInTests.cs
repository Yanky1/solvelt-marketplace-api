using System.Text.Json.Nodes;
using Solvelt.Marketplace.Api.Tests.Infrastructure;
using Solvelt.Marketplace.Api.Voice;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>
/// Barge-in follows what the PHONE is playing, not what the model has generated (V-12). The model produces audio much faster than real time, so its response is "done" while the phone still has
/// seconds of it queued; the caller talks over it and the agent must stop at once. SignalWire marks tell the bridge what has played; time on the (fake) clock says how much of the item was heard.
/// </summary>
public class VoiceBargeInTests
{
    /// <summary>Mu-law 8 kHz is 8 bytes per millisecond: this is audio of exactly <paramref name="ms"/> milliseconds.</summary>
    private static string Audio(int ms) => Convert.ToBase64String(new byte[ms * 8]);

    private static async Task Delta(VoiceScenario s, string item, int ms) => await s.Fake.Send(new { type = "response.output_audio.delta", item_id = item, delta = Audio(ms) });

    private static Task<JsonNode> Medias(VoiceScenario s, int n) => FakeRealtime.Poll(() => s.Phone!.Got.Count(g => g["event"]!.GetValue<string>() == "media") >= n ? s.Phone.Got.Last(g => g["event"]!.GetValue<string>() == "media") : null, 5);

    /// <summary>A mark follows its media on the same socket: wait until all <paramref name="n"/> have arrived (never read them right after the media).</summary>
    private static async Task<List<string>> WaitMarks(VoiceScenario s, int n) => await FakeRealtime.Poll(() => AudioMarks(s) is { } m && m.Count >= n ? m : null, 5);

    private static List<string> AudioMarks(VoiceScenario s) => s.Phone!.Got.Where(g => g["event"]!.GetValue<string>() == "mark").Select(g => g["mark"]!["name"]!.GetValue<string>()).Where(n => n != "end").ToList();

    /// <summary>SignalWire says it has played (or cleared) these: it echoes the marks. Then a caller-audio message goes through the phone loop and out, so everything before it has been handled.</summary>
    private static async Task Echo(VoiceScenario s, IEnumerable<string> names)
    {
        foreach (var n in names) await s.Phone!.Send(new { @event = "mark", streamSid = "MZ1", mark = new { name = n } });
        var before = s.Fake.Of("input_audio_buffer.append").Count();
        await s.Phone!.Media(Convert.ToBase64String(new byte[16]));
        await FakeRealtime.Poll(() => s.Fake.Of("input_audio_buffer.append").Count() > before ? "x" : null, 5);   // the phone loop is in order: the echoes are done
    }

    /// <summary>The model's next audio goes through after everything sent before it on the same socket has been handled (the two sockets of the model and the phone: this one is the model's).</summary>
    private static async Task Settle(VoiceScenario s) { var n = s.Phone!.Got.Count(g => g["event"]!.GetValue<string>() == "media"); await Delta(s, "sentinel", 10); await Medias(s, n + 1); }

    private static int Clears(VoiceScenario s) => s.Phone!.Got.Count(g => g["event"]!.GetValue<string>() == "clear");

    [Fact]
    public async Task Talking_over_audio_the_phone_is_still_playing_after_the_model_is_done_clears_it_and_cuts_at_the_PLAYED_time()
    {
        using var s = new VoiceScenario();
        await s.Dial();
        await Delta(s, "item_7", 1000); await Delta(s, "item_7", 1000);                                     // two seconds of audio, generated in an instant
        await Medias(s, 2);
        Assert.Equal(2, (await WaitMarks(s, 2)).Count);                                                     // a mark after each: the phone has not echoed them (it is still playing)
        await s.Fake.Send(new { type = "response.done", response = new { output = Array.Empty<object>() } });   // the model is DONE; the phone is not
        s.F.Clock.Advance(TimeSpan.FromMilliseconds(500));                                                  // the caller has heard half a second
        await s.Fake.Send(new { type = "input_audio_buffer.speech_started" });

        var clear = await s.Phone!.Wait("clear");
        Assert.Equal("MZ1", clear["streamSid"]!.GetValue<string>());
        var cut = await s.Fake.Wait("conversation.item.truncate");
        Assert.Equal("item_7", cut["item_id"]!.GetValue<string>());
        Assert.Equal(500, cut["audio_end_ms"]!.GetValue<int>());                                            // what was PLAYED, not the 2000 ms that were sent
        await Settle(s);
        Assert.Empty(s.Fake.Of("response.cancel"));                                                         // the response was already done: nothing to cancel
        // one info-level line per barge-in: ms only, no content
        var line = Assert.Single(s.F.Logs.Lines, l => l.Contains("talked over the agent"));
        Assert.Contains("1500 ms of audio were still queued", line); Assert.Contains("500 ms of the item had played", line);
    }

    [Fact]
    public async Task Talking_over_audio_while_the_response_is_still_going_also_cancels_the_response()
    {
        using var s = new VoiceScenario();
        await s.Dial();
        await Delta(s, "item_3", 1000);
        await Medias(s, 1); await WaitMarks(s, 1);
        s.F.Clock.Advance(TimeSpan.FromMilliseconds(300));
        await s.Fake.Send(new { type = "input_audio_buffer.speech_started" });                              // no response.done yet
        await s.Phone!.Wait("clear");
        Assert.Equal(300, (await s.Fake.Wait("conversation.item.truncate"))["audio_end_ms"]!.GetValue<int>());
        await s.Fake.Wait("response.cancel");                                                               // the model is told to stop generating
    }

    [Fact]
    public async Task Talking_after_the_phone_has_played_everything_clears_and_cuts_nothing()
    {
        using var s = new VoiceScenario();
        await s.Dial();
        await Delta(s, "item_1", 100); await Delta(s, "item_1", 100);
        await Medias(s, 2);
        var marks = await WaitMarks(s, 2);
        await s.Fake.Send(new { type = "response.done", response = new { output = Array.Empty<object>() } });
        await Echo(s, marks);                                                                       // the phone echoed the last mark: it has played it all
        await s.Fake.Send(new { type = "input_audio_buffer.speech_started" });
        await Settle(s);
        Assert.Equal(0, Clears(s));                                                                         // nothing is queued: no clear
        Assert.Empty(s.Fake.Of("conversation.item.truncate"));                                              // and no truncate
        Assert.Empty(s.Fake.Of("response.cancel"));
    }

    [Fact]
    public async Task After_a_clear_the_stale_marks_echo_state_resets_and_the_next_response_plays_normally()
    {
        using var s = new VoiceScenario();
        await s.Dial();
        await Delta(s, "item_1", 1000);
        await Medias(s, 1);
        var stale = await WaitMarks(s, 1);
        s.F.Clock.Advance(TimeSpan.FromMilliseconds(400));
        await s.Fake.Send(new { type = "input_audio_buffer.speech_started" });
        await s.Phone!.Wait("clear");
        Assert.Equal(400, (await s.Fake.Wait("conversation.item.truncate"))["audio_end_ms"]!.GetValue<int>());
        await Echo(s, stale);                                                                               // SignalWire returns the marks of the cleared audio: they are ignored

        s.F.Clock.Advance(TimeSpan.FromMilliseconds(5000));                                                 // time passes; the next answer starts a fresh queue
        await Delta(s, "item_2", 1000);
        await Medias(s, 2);                                                                                 // it plays normally (a new media reached the phone)
        s.F.Clock.Advance(TimeSpan.FromMilliseconds(250));
        await s.Fake.Send(new { type = "input_audio_buffer.speech_started" });
        // the bridge tells the PHONE to clear first and the model to truncate right after: wait for the second truncate itself, not for the clear (which can be seen before the truncate was sent)
        await FakeRealtime.Poll(() => Clears(s) == 2 && s.Fake.Of("conversation.item.truncate").Count() == 2 ? "x" : null, 5);
        var second = s.Fake.Of("conversation.item.truncate").Last();
        Assert.Equal("item_2", second["item_id"]!.GetValue<string>());
        Assert.Equal(250, second["audio_end_ms"]!.GetValue<int>());                                         // counted from the NEW item's start, nothing carried over from the cleared one
    }

    [Fact]
    public async Task A_second_interruption_right_after_a_clear_finds_nothing_left_to_clear_even_before_the_marks_echo()
    {
        using var s = new VoiceScenario();
        await s.Dial();
        await Delta(s, "item_1", 1000);
        await Medias(s, 1); await WaitMarks(s, 1);
        s.F.Clock.Advance(TimeSpan.FromMilliseconds(200));
        await s.Fake.Send(new { type = "input_audio_buffer.speech_started" });
        await s.Phone!.Wait("clear");
        await s.Fake.Wait("conversation.item.truncate");
        await s.Fake.Send(new { type = "input_audio_buffer.speech_started" });                              // the caller goes on talking; SignalWire has echoed nothing yet
        await Settle(s);
        Assert.Equal(1, Clears(s));                                                                         // the phone queue is empty after the first clear: nothing more to clear
        Assert.Single(s.Fake.Of("conversation.item.truncate"));                                             // and nothing more to cut
    }

    [Fact]
    public async Task A_lost_echo_cannot_wedge_the_state_once_the_audio_must_have_played()
    {
        using var s = new VoiceScenario();
        await s.Dial();
        await Delta(s, "item_1", 100);
        await Medias(s, 1); await WaitMarks(s, 1);                                                          // the phone never echoes the mark
        s.F.Clock.Advance(TimeSpan.FromMilliseconds(100) + PhonePlayback.LostEchoGrace + TimeSpan.FromMilliseconds(1));
        await s.Fake.Send(new { type = "input_audio_buffer.speech_started" });
        await Settle(s);
        Assert.Equal(0, Clears(s));                                                                         // long after its end the audio counts as played: no clear on a phantom queue
    }

    [Fact]
    public async Task The_keypad_hand_off_still_ends_the_stream_on_its_own_end_mark_while_audio_marks_are_in_flight()
    {
        using var s = new VoiceScenario();
        await s.Dial();
        await Delta(s, "item_1", 100);                                                                      // the goodbye audio has an audio mark of its own
        await Medias(s, 1); await WaitMarks(s, 1);
        var r = await s.Tool("identify_caller");
        Assert.Equal("code_sent", r["status"]!.GetValue<string>());
        await s.EndStreamAfterGoodbye();                                                                    // waits for the mark named "end", echoes it, and the stream closes
        var gather = await s.AfterStream();
        Assert.NotNull(gather.Element("Gather"));
        Assert.Equal(VoiceScripts.AskCode, gather.Element("Gather")!.Element("Say")!.Value);
    }
}
