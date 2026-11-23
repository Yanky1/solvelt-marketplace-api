using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;
using Solvelt.Marketplace.Api.Voice;
using Solvelt.Marketplace.Api.Voice.TestSet;

namespace Solvelt.Marketplace.Api.Tests;

/// <summary>Copying a finished recording into the private container, then deleting it at SignalWire (decision V-46): nothing is deleted before the copy is verified, and a recording is never left there on purpose.</summary>
public sealed class VoiceTestSetCopyTests
{
    private const string Sid = "RE0123456789abcdef0123456789abcdef", Name = "set1/en-02-p0a1b2cs1f2e.wav";

    private sealed class FakeSignalWire : HttpMessageHandler
    {
        public readonly List<(HttpMethod Method, string Url, string? Auth)> Calls = [];
        public byte[]? Wav; public HttpStatusCode GetStatus = HttpStatusCode.OK, DeleteStatus = HttpStatusCode.NoContent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls.Add((r.Method, r.RequestUri!.ToString(), r.Headers.Authorization?.ToString()));
            if (r.Method == HttpMethod.Get)
                return Task.FromResult(GetStatus == HttpStatusCode.OK ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Wav ?? []) } : new HttpResponseMessage(GetStatus));
            return Task.FromResult(new HttpResponseMessage(DeleteStatus));
        }
    }

    /// <summary>A store that can lose a write or hold a different length, to prove the delete waits for a verified copy.</summary>
    private sealed class FlakyStore(MemoryTestSetStore inner) : ITestSetStore
    {
        public bool DropWrites, Throw;
        public IAsyncEnumerable<string> ListAsync(string set, CancellationToken ct) => inner.ListAsync(set, ct);
        public Task<byte[]> ReadAsync(string name, CancellationToken ct) => inner.ReadAsync(name, ct);
        public Task<long?> SizeAsync(string name, CancellationToken ct) => inner.SizeAsync(name, ct);
        public Task WriteAsync(string name, byte[] wav, CancellationToken ct)
            => Throw ? throw new IOException("storage down") : DropWrites ? Task.CompletedTask : inner.WriteAsync(name, wav, ct);
    }

    private static readonly byte[] Wav = WavAudio.FromMulaw8k(Enumerable.Range(0, 1600).Select(i => (byte)(i % 200)).ToArray());

    private static (TestSetCopyHandler Handler, FakeSignalWire Sw, MemoryTestSetStore Mem, FlakyStore Store) Make(Action<FakeSignalWire>? sw = null)
    {
        var fake = new FakeSignalWire { Wav = Wav }; sw?.Invoke(fake);
        var mem = new MemoryTestSetStore(); var flaky = new FlakyStore(mem);
        var client = new SignalWireRecordings(new HttpClient(fake), Options.Create(new SignalWireOptions { Space = "acme", ProjectId = "proj-1", ApiToken = "tok" }));
        return (new TestSetCopyHandler(flaky, client, NullLogger<TestSetCopyHandler>.Instance), fake, mem, flaky);
    }

    private static OutboxMessage Msg(string sid = Sid, string name = Name) => new() { Type = TestSetCopyHandler.Type, Payload = System.Text.Json.JsonSerializer.Serialize(new TestSetCopyHandler.Payload(sid, name), Solvelt.Marketplace.Api.Portal.PortalJson.Options) };

    [Fact]
    public async Task A_recording_is_fetched_with_the_project_credentials_stored_verified_and_only_then_deleted()
    {
        var (h, sw, mem, _) = Make();
        var r = await h.HandleAsync(Msg(), default);
        Assert.Equal(OutboxOutcome.Delivered, r.Outcome);
        Assert.Equal(Wav, mem.Blobs[Name]);
        Assert.Equal([HttpMethod.Get, HttpMethod.Delete], sw.Calls.Select(c => c.Method).ToArray());                      // fetch first, delete last
        Assert.Equal($"https://acme.signalwire.com/api/laml/2010-04-01/Accounts/proj-1/Recordings/{Sid}.wav", sw.Calls[0].Url);
        Assert.Equal($"https://acme.signalwire.com/api/laml/2010-04-01/Accounts/proj-1/Recordings/{Sid}", sw.Calls[1].Url);
        Assert.StartsWith("Basic ", sw.Calls[0].Auth);
        Assert.Equal("Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("proj-1:tok")), sw.Calls[1].Auth);
    }

    [Fact]
    public async Task A_recording_that_is_not_ready_is_tried_again_and_nothing_is_stored_or_deleted()
    {
        var (h, sw, mem, _) = Make(f => f.GetStatus = HttpStatusCode.NotFound);
        var r = await h.HandleAsync(Msg(), default);
        Assert.Equal((OutboxOutcome.Retry, "not_ready"), (r.Outcome, r.Error));
        Assert.Empty(mem.Blobs);
        Assert.DoesNotContain(sw.Calls, c => c.Method == HttpMethod.Delete);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_refused_fetch_is_retried_without_deleting(HttpStatusCode status)
    {
        var (h, sw, mem, _) = Make(f => f.GetStatus = status);
        Assert.Equal(OutboxOutcome.Retry, (await h.HandleAsync(Msg(), default)).Outcome);
        Assert.Empty(mem.Blobs);
        Assert.DoesNotContain(sw.Calls, c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task A_copy_that_cannot_be_verified_or_stored_is_never_followed_by_a_delete()
    {
        var (h, sw, mem, store) = Make();
        store.DropWrites = true;                                                                                            // the store says it wrote, but nothing is there
        Assert.Equal((OutboxOutcome.Retry, "size_mismatch"), ((await h.HandleAsync(Msg(), default)).Outcome, (await h.HandleAsync(Msg(), default)).Error));
        Assert.DoesNotContain(sw.Calls, c => c.Method == HttpMethod.Delete);
        store.DropWrites = false; store.Throw = true;
        var r = await h.HandleAsync(Msg(), default);
        Assert.Equal(OutboxOutcome.Retry, r.Outcome);
        Assert.DoesNotContain(sw.Calls, c => c.Method == HttpMethod.Delete);
        Assert.Empty(mem.Blobs);
    }

    [Fact]
    public async Task A_failed_delete_is_retried_and_the_retry_does_not_store_a_second_copy()
    {
        var (h, sw, mem, _) = Make(f => f.DeleteStatus = HttpStatusCode.Forbidden);                                         // a token without the Voice scope
        var first = await h.HandleAsync(Msg(), default);
        Assert.Equal(OutboxOutcome.Retry, first.Outcome);
        Assert.Single(mem.Blobs);                                                                                           // the copy is safe
        sw.DeleteStatus = HttpStatusCode.NoContent;
        var second = await h.HandleAsync(Msg(), default);
        Assert.Equal(OutboxOutcome.Delivered, second.Outcome);
        Assert.Single(mem.Blobs);
        Assert.Equal(2, sw.Calls.Count(c => c.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task A_recording_that_is_already_gone_at_signalwire_counts_as_deleted()
    {
        var (h, _, _, _) = Make(f => f.DeleteStatus = HttpStatusCode.NotFound);
        Assert.Equal(OutboxOutcome.Delivered, (await h.HandleAsync(Msg(), default)).Outcome);
    }

    [Fact]
    public async Task A_different_recording_with_the_same_name_is_never_overwritten_and_the_new_one_is_not_deleted()
    {
        var (h, sw, mem, _) = Make();
        await mem.WriteAsync(Name, [1, 2, 3], default);
        var r = await h.HandleAsync(Msg(), default);
        Assert.Equal((OutboxOutcome.Dead, "name_taken"), (r.Outcome, r.Error));
        Assert.Equal([1, 2, 3], mem.Blobs[Name]);
        Assert.DoesNotContain(sw.Calls, c => c.Method == HttpMethod.Delete);                                                // audio that was not stored is not deleted
    }

    [Fact]
    public async Task Something_that_is_not_audio_or_a_bad_job_is_dead_and_deletes_nothing()
    {
        var (h, sw, mem, _) = Make(f => f.Wav = [1, 2, 3, 4]);
        Assert.Equal((OutboxOutcome.Dead, "unusable_audio"), ((await h.HandleAsync(Msg(), default)).Outcome, (await h.HandleAsync(Msg(), default)).Error));
        Assert.Empty(mem.Blobs);
        Assert.DoesNotContain(sw.Calls, c => c.Method == HttpMethod.Delete);
        foreach (var bad in new[] { Msg("../../x"), Msg(Sid, "../escape.wav"), Msg(Sid, "set1/en-02-Bad.wav"), new OutboxMessage { Type = TestSetCopyHandler.Type, Payload = "nope" }, new OutboxMessage { Type = TestSetCopyHandler.Type } })
            Assert.Equal(OutboxOutcome.Dead, (await h.HandleAsync(bad, default)).Outcome);
        Assert.Equal(2, sw.Calls.Count);                                                                                    // only the two fetches above: a bad job never reaches the network
    }

    [Fact]
    public async Task Missing_signalwire_settings_are_a_retry_not_a_crash_and_a_sid_cannot_change_the_path()
    {
        var client = new SignalWireRecordings(new HttpClient(new FakeSignalWire { Wav = Wav }), Options.Create(new SignalWireOptions()));
        var h = new TestSetCopyHandler(new MemoryTestSetStore(), client, NullLogger<TestSetCopyHandler>.Instance);
        Assert.Equal(OutboxOutcome.Retry, (await h.HandleAsync(Msg(), default)).Outcome);
        Assert.False(SignalWireRecordings.IsValidSid("a/b"));
        Assert.False(SignalWireRecordings.IsValidSid("RE1?x=1&y=2-aaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Assert.True(SignalWireRecordings.IsValidSid(Sid));
    }
}
