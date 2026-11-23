using Microsoft.Extensions.Configuration;
using System.Runtime.CompilerServices;
using Solvelt.Marketplace.Api.Voice;
using Solvelt.Marketplace.Api.Voice.TestSet;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>The recorded recognition test set and its scoring (decision V-46). No vendor is contacted and nothing is written to disk: a fake recogniser and a memory store.</summary>
public sealed class VoiceTestSetTests
{
    // ---------- the phrase list ----------

    [Fact]
    public void The_shipped_phrase_list_is_valid_with_english_approved_and_yiddish_still_draft()
    {
        var list = PhraseList.Embedded();
        Assert.Equal(40, list.Phrases.Count);
        Assert.Equal(25, list.Phrases.Count(p => p.Language == "en"));
        Assert.Equal(15, list.Phrases.Count(p => p.Language == "yi"));
        Assert.All(list.Phrases.Where(p => p.Language == "en"), p => Assert.False(p.IsDraft));
        Assert.All(list.Phrases.Where(p => p.Language == "yi"), p => Assert.True(p.IsDraft));          // no Yiddish speaker has approved the wording yet
        Assert.All(list.Phrases.Where(p => p.Language == "yi"), p => Assert.Matches("[֐-׿]", p.Text));
        Assert.Equal(list.Phrases.Count, list.Phrases.Select(p => p.Id).Distinct().Count());
        Assert.Contains(list.Phrases, p => p.Kind == "digits");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"phrases\":[]}")]
    [InlineData("{\"phrases\":[{\"id\":\"EN-1\",\"language\":\"en\",\"kind\":\"plain\",\"text\":\"hi\",\"status\":\"approved\"}]}")]
    [InlineData("{\"phrases\":[{\"id\":\"en-01\",\"language\":\"yi\",\"kind\":\"plain\",\"text\":\"hi\",\"status\":\"approved\"}]}")]
    [InlineData("{\"phrases\":[{\"id\":\"en-01\",\"language\":\"en\",\"kind\":\"weird\",\"text\":\"hi\",\"status\":\"approved\"}]}")]
    [InlineData("{\"phrases\":[{\"id\":\"en-01\",\"language\":\"en\",\"kind\":\"plain\",\"text\":\"!!!\",\"status\":\"approved\"}]}")]
    [InlineData("{\"phrases\":[{\"id\":\"en-01\",\"language\":\"en\",\"kind\":\"plain\",\"text\":\"a\",\"status\":\"approved\"},{\"id\":\"en-01\",\"language\":\"en\",\"kind\":\"plain\",\"text\":\"b\",\"status\":\"approved\"}]}")]
    public void A_bad_phrase_list_is_refused(string json) => Assert.Throws<FormatException>(() => PhraseList.Parse(json));

    [Theory]
    [InlineData("set1/en-01-moshe.wav", true)]
    [InlineData("set1/yi-12-a1.wav", true)]
    [InlineData("set1/en-01-Moshe.wav", false)]            // lower case only
    [InlineData("set1/en-01.wav", false)]
    [InlineData("../en-01-x.wav", false)]
    [InlineData("set1/en-01-x.mp3", false)]
    [InlineData("set1/sub/en-01-x.wav", false)]
    public void A_recording_name_is_a_set_a_phrase_and_a_short_speaker_label(string blob, bool valid)
    {
        Assert.Equal(valid, RecordingName.TryParse(blob, out var n));
        if (valid) Assert.Equal(blob, n.ToString());
    }

    // ---------- the settings ----------

    [Fact]
    public void The_settings_are_off_by_default_and_refuse_odd_values()
    {
        var o = new TestSetOptions();
        Assert.False(o.Enabled);
        Assert.Contains(o.Problems(), p => p.Contains("StorageAccountUrl"));                                 // nothing to keep audio in yet
        o.StorageAccountUrl = "https://acct.blob.core.windows.net";
        Assert.Empty(o.Problems());
        foreach (var bad in new[] { "http://acct.blob.core.windows.net", "https://acct.blob.core.windows.net/container", "acct", "https://acct.blob.core.windows.net?sas=1", "https://user:pw@acct.blob.core.windows.net" })
            Assert.NotEmpty(new TestSetOptions { StorageAccountUrl = bad }.Problems());
        Assert.NotEmpty(new TestSetOptions { StorageAccountUrl = "https://a.blob.core.windows.net", ConnectionString = "UseDevelopmentStorage=true" }.Problems());   // a key beside the identity: no
        Assert.Empty(new TestSetOptions { ConnectionString = "UseDevelopmentStorage=true" }.Problems());                                                         // the development fallback alone
        Assert.NotEmpty(new TestSetOptions { StorageAccountUrl = "https://a.blob.core.windows.net", SetName = "Set One" }.Problems());
        Assert.NotEmpty(new TestSetOptions { StorageAccountUrl = "https://a.blob.core.windows.net", MaxRecordSeconds = 0 }.Problems());
        Assert.NotEmpty(new TestSetOptions { StorageAccountUrl = "https://a.blob.core.windows.net", Container = "A" }.Problems());
    }

    // ---------- audio ----------

    [Fact]
    public void A_mulaw_wav_round_trips_and_pcm_is_resampled_to_8k_mulaw()
    {
        var mulaw = Enumerable.Range(0, 1600).Select(i => (byte)(i % 251)).ToArray();
        Assert.Equal(mulaw, WavAudio.ToMulaw8k(WavAudio.FromMulaw8k(mulaw)));
        var pcm16k = Enumerable.Range(0, 3200).Select(i => (short)(8000 * Math.Sin(i / 20.0))).ToArray();
        var from16k = WavAudio.ToMulaw8k(PhoneAudio.Wav(pcm16k, 16000));
        Assert.Equal(1600, from16k.Length);                                                                  // half as many samples at 8 kHz
        Assert.True(from16k.Distinct().Count() > 10);                                                        // not silence
        Assert.Equal(1600, WavAudio.ToMulaw8k(PhoneAudio.Wav(pcm16k[..1600], 8000)).Length);
        Assert.Equal(8000, WavAudio.ToMulaw8k(PhoneAudio.Wav(new short[11025], 11025)).Length);              // another rate: interpolated
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(43)]
    public void Something_that_is_not_a_wav_file_is_refused(int length) => Assert.Throws<FormatException>(() => WavAudio.ToMulaw8k(new byte[length]));

    // ---------- replay and scoring with a fake recogniser ----------

    /// <summary>Answers a fixed sentence once it has heard some audio; counts what it was sent.</summary>
    private sealed class FakeStt(Func<int, string> answer, bool failToOpen = false, bool silent = false) : IStreamingStt
    {
        public int Opens, FramesSent;
        public async Task<ISttSession> OpenAsync(CancellationToken ct)
        {
            Opens++;
            if (failToOpen) throw new InvalidOperationException("secret vendor detail that must not be reported");
            return new Session(this);
        }
        private sealed class Session(FakeStt o) : ISttSession
        {
            private readonly System.Threading.Channels.Channel<SttEvent> _c = System.Threading.Channels.Channel.CreateUnbounded<SttEvent>();
            private int _bytes; private bool _said;
            public ValueTask SendAudioAsync(ReadOnlyMemory<byte> mulaw8k, CancellationToken ct)
            {
                o.FramesSent++; _bytes += mulaw8k.Length;
                if (!_said && !silent(o) && _bytes >= 800) { _said = true; _c.Writer.TryWrite(new SttEvent(SttEventKind.Final, o.AnswerFor(_bytes))); }
                return ValueTask.CompletedTask;
            }
            private static bool silent(FakeStt o) => o._silent;
            public async IAsyncEnumerable<SttEvent> Events([EnumeratorCancellation] CancellationToken ct)
            {
                while (await _c.Reader.WaitToReadAsync(ct)) while (_c.Reader.TryRead(out var e)) yield return e;
            }
            public ValueTask DisposeAsync() { _c.Writer.TryComplete(); return ValueTask.CompletedTask; }
        }
        private readonly Func<int, string> _answer = answer; private readonly bool _silent = silent;
        private string AnswerFor(int bytes) => _answer(bytes);
    }

    private static readonly ReplayOptions Fast = new(RealTime: false, TailSilenceMs: 100, IdleMs: 60, MaxWaitMs: 1500);

    private static byte[] Audio(int bytes = 1600) => Enumerable.Range(0, bytes).Select(i => (byte)(i % 200)).ToArray();

    [Fact]
    public async Task A_recording_is_played_in_20_ms_frames_and_the_final_words_come_back_with_a_latency()
    {
        var stt = new FakeStt(_ => "do you have a pewter kiddush cup");
        var r = await SttReplay.RunAsync(stt, Audio(1600), Fast, CancellationToken.None);
        Assert.Equal("do you have a pewter kiddush cup", r.Text);
        Assert.Null(r.Error); Assert.False(r.TimedOut); Assert.NotNull(r.LatencyMs);
        Assert.Equal(1600 / 160 + 100 / 20, stt.FramesSent);                                              // the audio, then the silence tail
    }

    [Fact]
    public async Task A_recogniser_that_hears_nothing_times_out_and_one_that_fails_reports_only_the_kind_of_failure()
    {
        var quiet = await SttReplay.RunAsync(new FakeStt(_ => "x", silent: true), Audio(), new ReplayOptions(false, 40, 40, 300), CancellationToken.None);
        Assert.True(quiet.TimedOut); Assert.Equal("", quiet.Text);
        var broken = await SttReplay.RunAsync(new FakeStt(_ => "x", failToOpen: true), Audio(), Fast, CancellationToken.None);
        Assert.Equal("InvalidOperationException", broken.Error);
        Assert.DoesNotContain("secret", broken.Error);
    }

    private static async Task<MemoryTestSetStore> Store(params (string Name, byte[] Wav)[] blobs)
    {
        var s = new MemoryTestSetStore();
        foreach (var (n, w) in blobs) await s.WriteAsync(n, w, default);
        return s;
    }

    [Fact]
    public async Task Every_recording_goes_through_every_variant_and_the_numbers_are_corpus_numbers()
    {
        var wav = WavAudio.FromMulaw8k(Audio(1600));
        var store = await Store(("set1/en-02-a1.wav", wav), ("set1/en-05-a1.wav", wav), ("set1/yi-01-a1.wav", wav), ("set1/notes.txt", [1]), ("set1/en-99-a1.wav", wav), ("other/en-01-a1.wav", wav));
        var phrases = PhraseList.Embedded();
        var exact = new FakeStt(_ => "ignored");
        // the fakes answer with a fixed sentence per variant, so the expected error counts are known
        var good = new ScoringVariant("good", new FakeStt(_ => "do you have a pewter kiddush cup"));
        var bad = new ScoringVariant("bad", new FakeStt(_ => "do you have a pewter kiddush cap"));
        var run = await TestSetScorer.RunAsync(store, phrases, [good, bad], new ScoringOptions("set1", Replay: Fast), CancellationToken.None);
        Assert.Equal(4, run.Rows.Count);                                                                      // en-02 and en-05 for two variants; the draft yi-01 is left out
        Assert.Equal(["en-02", "en-05"], run.Rows.Select(r => r.PhraseId).Distinct().ToArray());
        Assert.Contains(run.Skipped, s => s.Contains("yi-01") && s.Contains("draft"));
        Assert.Contains(run.Skipped, s => s.Contains("notes.txt"));
        Assert.Contains(run.Skipped, s => s.Contains("en-99"));
        var en02good = run.Rows.Single(r => r.Variant == "good" && r.PhraseId == "en-02");
        Assert.Equal(0, en02good.Wer.Errors);
        var en02bad = run.Rows.Single(r => r.Variant == "bad" && r.PhraseId == "en-02");
        Assert.Equal((1, 7), (en02bad.Wer.Errors, en02bad.Wer.ReferenceLength));                              // "cap" for "cup"
        Assert.Equal(1, en02bad.Cer.Errors);
        var summary = ScoreReport.Summarise(run.Rows);
        var allBad = summary.Single(s => s.Variant == "bad" && s.Group == "all");
        Assert.Equal(2, allBad.Recordings);
        Assert.Equal(allBad.Wer.Errors, run.Rows.Where(r => r.Variant == "bad").Sum(r => r.Wer.Errors));      // sums, not a mean of rates
        Assert.Equal(allBad.Wer.ReferenceLength, run.Rows.Where(r => r.Variant == "bad").Sum(r => r.Wer.ReferenceLength));
    }

    [Fact]
    public async Task Draft_phrases_are_scored_only_when_asked_and_a_speaker_filter_applies()
    {
        var wav = WavAudio.FromMulaw8k(Audio(1600));
        var store = await Store(("set1/yi-01-a1.wav", wav), ("set1/en-02-a1.wav", wav), ("set1/en-02-b2.wav", wav));
        var v = new ScoringVariant("v", new FakeStt(_ => "x"));
        var withDraft = await TestSetScorer.RunAsync(store, PhraseList.Embedded(), [v], new ScoringOptions("set1", IncludeDraft: true, Replay: Fast), CancellationToken.None);
        Assert.Equal(3, withDraft.Rows.Count);
        Assert.True(withDraft.Rows.Single(r => r.PhraseId == "yi-01").Draft);
        var onlyB = await TestSetScorer.RunAsync(store, PhraseList.Embedded(), [v], new ScoringOptions("set1", ["b2"], Replay: Fast), CancellationToken.None);
        Assert.Equal(["b2"], onlyB.Rows.Select(r => r.Speaker).ToArray());
        var phoneStore = await Store(("set1/en-02-p0a1b2cs1f2e.wav", wav), ("set1/en-02-p0a1b2cs1f2er2.wav", wav), ("set1/en-02-p999999sabcd.wav", wav));
        var bySpeaker = await TestSetScorer.RunAsync(phoneStore, PhraseList.Embedded(), [v], new ScoringOptions("set1", ["p0a1b2c"], Replay: Fast), CancellationToken.None);
        Assert.Equal(2, bySpeaker.Rows.Count);                                                                       // both takes of the speaker, not the other speaker
    }

    [Fact]
    public async Task The_report_names_the_failures_and_the_csv_cannot_run_a_formula()
    {
        var wav = WavAudio.FromMulaw8k(Audio(1600));
        var store = await Store(("set1/en-02-a1.wav", wav));
        var run = await TestSetScorer.RunAsync(store, PhraseList.Embedded(), [new ScoringVariant("evil", new FakeStt(_ => "=HYPERLINK(\"http://x\",\"y\")")), new ScoringVariant("down", new FakeStt(_ => "x", failToOpen: true))],
            new ScoringOptions("set1", Replay: Fast), CancellationToken.None);
        var csv = ScoreReport.Csv(run);
        Assert.DoesNotContain(",=HYPERLINK", csv);
        Assert.Contains("'=HYPERLINK", csv);
        var text = ScoreReport.Text(run);
        Assert.Contains("ERROR InvalidOperationException", text);
        Assert.Contains("WER", text);
        Assert.DoesNotContain("secret", text);
        Assert.Equal(1, ScoreReport.Summarise(run.Rows).Single(s => s.Variant == "down" && s.Group == "all").Failed);
    }

    [Fact]
    public async Task A_second_recording_never_replaces_the_first_in_the_store()
    {
        var store = new MemoryTestSetStore();
        await store.WriteAsync("set1/en-01-a1.wav", [1], default);
        await Assert.ThrowsAsync<RecordingExistsException>(() => store.WriteAsync("set1/en-01-a1.wav", [2], default));
        Assert.Equal([1], await store.ReadAsync("set1/en-01-a1.wav", default));
    }
}

/// <summary>The start-up checks of the test set (V-46): they apply only when it is switched on.</summary>
public sealed class VoiceTestSetStartupTests
{
    private static IReadOnlyList<string> Problems(Action<VoiceOptions> set, bool devLike = true)
    {
        var v = new VoiceOptions { Enabled = true, OpenAiApiKey = "k", StreamTokenKey = new string('a', 40), PhoneCodeKey = new string('b', 40), CallerHashKey = new string('c', 40), PublicBaseUrl = "https://api.example.com" };
        set(v);
        return VoiceConfig.Problems(v, new SignalWireOptions { SigningKey = "s", ProjectId = "p", ApiToken = "t", Space = "x", FromNumber = "+15550000000" }, devLike);
    }

    [Fact]
    public void Switched_off_it_asks_for_nothing()
        => Assert.DoesNotContain(Problems(v => { }), p => p.Contains("TestSet"));

    [Fact]
    public void Switched_on_it_needs_a_private_container_address_and_the_test_numbers()
    {
        var p = Problems(v => v.TestSet.Enabled = true);
        Assert.Contains(p, x => x.Contains("StorageAccountUrl"));
        Assert.Contains(p, x => x.Contains("TestNumbers"));
        Assert.Empty(Problems(v => { v.TestSet.Enabled = true; v.TestSet.StorageAccountUrl = "https://acct.blob.core.windows.net"; v.PipelineMenu.TestNumbers = ["+15551234567"]; }));
    }

    [Fact]
    public void Recording_by_phone_needs_the_signalwire_project_and_a_token_to_fetch_and_delete_the_recording()
    {
        var v = new VoiceOptions { Enabled = true, OpenAiApiKey = "k", StreamTokenKey = new string('a', 40), PhoneCodeKey = new string('b', 40), CallerHashKey = new string('c', 40), PublicBaseUrl = "https://api.example.com" };
        v.TestSet.Enabled = true; v.TestSet.StorageAccountUrl = "https://acct.blob.core.windows.net"; v.PipelineMenu.TestNumbers = ["+15551234567"];
        var without = VoiceConfig.Problems(v, new SignalWireOptions { SigningKey = "s" }, devLike: true);
        Assert.Contains(without, x => x.Contains("SignalWire:ApiToken") && x.Contains("Management and Voice"));
        Assert.DoesNotContain(VoiceConfig.Problems(v, new SignalWireOptions { SigningKey = "s", Space = "acme", ProjectId = "p", ApiToken = "t" }, devLike: true), x => x.Contains("SignalWire:ApiToken"));
    }

    [Fact]
    public void A_storage_connection_string_is_for_development_only()
    {
        void On(VoiceOptions v) { v.TestSet.Enabled = true; v.TestSet.ConnectionString = "UseDevelopmentStorage=true"; v.PipelineMenu.TestNumbers = ["+15551234567"]; }
        Assert.Empty(Problems(On, devLike: true));
        Assert.Contains(Problems(On, devLike: false), x => x.Contains("development only"));
    }
}

/// <summary>Building the recognisers of the scoring tool from configuration (V-46). Nothing connects: only the adapters are built.</summary>
public sealed class VoiceScoringSetupTests
{
    private static Microsoft.Extensions.Configuration.IConfiguration Config(params (string, string)[] kv)
        => new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(kv.Select(x => new KeyValuePair<string, string?>(x.Item1, x.Item2))).Build();

    private static readonly (string, string)[] Deepgram = [("Voice:Pipelines:P2:Stt", "deepgram"), ("Voice:Pipelines:P2:Tts", "elevenlabs"), ("Voice:Deepgram:ApiKey", "dg-test-key"), ("Voice:Deepgram:Model", "nova-3")];

    [Fact]
    public void Only_the_options_whose_recogniser_settings_are_complete_are_replayed_and_the_voice_settings_are_not_needed()
    {
        Assert.Empty(ScoringSetup.ReplayableOptions(Config()));
        Assert.Equal(["p2"], ScoringSetup.ReplayableOptions(Config(Deepgram)));                              // no ElevenLabs key needed: nothing is spoken
        Assert.Equal(["p2"], ScoringSetup.DefaultVariants(Config(Deepgram)).Select(v => v.Name).ToArray());
        Assert.Empty(ScoringSetup.ReplayableOptions(Config(Deepgram.Where(x => x.Item1 != "Voice:Deepgram:ApiKey").ToArray())));
    }

    [Fact]
    public void A_variant_is_built_with_its_overrides_and_a_missing_setting_is_named_without_its_value()
    {
        using var setup = new ScoringSetup();
        var v = setup.Build(Config(Deepgram), new VariantSpec("dg-fast", "p2", new Dictionary<string, string> { ["Voice:Deepgram:EndpointingMs"] = "400" }, ["challah", "kiddush"]));
        Assert.Equal("dg-fast", v.Name);
        Assert.IsType<DeepgramStt>(v.Stt);
        var ex = Assert.Throws<InvalidOperationException>(() => setup.Build(Config(("Voice:Pipelines:P3:Stt", "gladia"), ("Voice:Pipelines:P3:Tts", "elevenlabs"), ("Voice:Gladia:Model", "solaria")), new VariantSpec("g", "p3", new Dictionary<string, string>())));
        Assert.Contains("Voice:Gladia:ApiKey", ex.Message);
        Assert.Throws<InvalidOperationException>(() => setup.Build(Config(Deepgram), new VariantSpec("x", "p6", new Dictionary<string, string>())));      // option 6 has no audio to replay
        Assert.Throws<InvalidOperationException>(() => setup.Build(Config(Deepgram), new VariantSpec("x", "p1", new Dictionary<string, string>())));
    }

    [Fact]
    public void A_matrix_file_names_variants_options_and_settings_and_refuses_nonsense()
    {
        var m = ScoringSetup.ParseMatrix("""{"variants":[{"name":"dg-nova3","option":"p2"},{"name":"dg-400ms","option":"p2","settings":{"Voice:Deepgram:EndpointingMs":400},"vocabulary":["challah"]}]}""");
        Assert.Equal(["dg-nova3", "dg-400ms"], m.Select(v => v.Name).ToArray());
        Assert.Equal("400", m[1].Settings["Voice:Deepgram:EndpointingMs"]);
        Assert.Equal(["challah"], m[1].Vocabulary);
        foreach (var bad in new[]
        {
            "not json", "{}", """{"variants":[]}""",
            """{"variants":[{"name":"a","option":"p6"}]}""",
            """{"variants":[{"name":"a","option":"p2"},{"name":"a","option":"p3"}]}""",
            """{"variants":[{"name":"bad,name","option":"p2"}]}""",
            """{"variants":[{"name":"a","option":"p2","settings":{"Portal:InboundSecret":"x"}}]}""",   // only Voice:... can be changed
        }) Assert.Throws<FormatException>(() => ScoringSetup.ParseMatrix(bad));
    }
}

/// <summary>The recorded audio is a caller's voice: none of it may ever be in the repository (V-46).</summary>
public sealed class VoiceTestSetRepositoryTests
{
    private static string? Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Solvelt.Marketplace.slnx"))) return d.FullName;
        return null;
    }

    [Fact]
    public void No_audio_file_is_in_the_repository_and_git_is_told_to_ignore_them()
    {
        var root = Root();
        if (root is null) return;   // not run from a checkout (a packaged test run): nothing to scan
        var audio = new[] { ".wav", ".mp3", ".m4a", ".ogg", ".opus", ".flac", ".amr", ".aac", ".ulaw", ".mulaw" };
        var found = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "node_modules" or ".git" or "TestResults"))
            .Where(f => audio.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => Path.GetRelativePath(root, f)).ToList();
        Assert.Empty(found);
        var ignore = File.ReadAllText(Path.Combine(root, ".gitignore"));
        foreach (var ext in audio) Assert.Contains("*" + ext, ignore);
    }
}
