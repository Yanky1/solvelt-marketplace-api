namespace Solvelt.Marketplace.Api.Voice;

/// <summary>What was on the phone when the caller started to talk over the agent.</summary>
internal sealed record BargeIn(string? ItemId, long QueuedMs, long PlayedMs);

/// <summary>
/// Tracks what the PHONE is playing, not what the model has generated (V-12). OpenAI produces audio much faster than real time, so the model's response is "done" while the phone still has seconds of
/// it queued; the only truth about playback is SignalWire's own: after every audio message the bridge sends a mark with a unique name, SignalWire echoes it when the audio before it has played (or was
/// cleared), so "the phone is playing" means some mark we sent has not come back yet (https://signalwire.com/docs/compatibility-api/cxml/reference/voice/stream.md: "Send a mark message", "Send a clear message").
/// How much of the current item the caller HEARD is the wall time since playback began (the first audio sent while the phone queue was empty), capped by the audio actually sent (mu-law 8 kHz: 8 bytes per ms).
/// A lost echo cannot wedge it: when the wall time passes the end of everything sent plus a short grace, the queue counts as played. Thread-safe: the two sockets are read concurrently.
/// </summary>
internal sealed class PhonePlayback(TimeProvider time)
{
    private const int BytesPerMs = 8;
    public static readonly TimeSpan LostEchoGrace = TimeSpan.FromSeconds(3);
    private readonly object _gate = new();
    private readonly HashSet<string> _outstanding = [];
    private long _seq, _queueStart, _queueBytes, _itemOffsetBytes, _itemBytes;
    private string? _itemId;

    /// <summary>Audio of <paramref name="bytes"/> bytes is about to be sent to the phone: returns the unique name of the mark to send right after it.</summary>
    public string NoteSent(long bytes, string? itemId)
    {
        lock (_gate)
        {
            ExpireLocked();
            if (_outstanding.Count == 0) { _queueStart = time.GetTimestamp(); _queueBytes = 0; _itemId = null; }   // the phone queue was empty: playback starts now
            if (_itemId is null || (itemId is not null && itemId != _itemId)) { _itemId = itemId ?? _itemId; _itemOffsetBytes = _queueBytes; _itemBytes = 0; }   // a new item starts behind what is queued
            _queueBytes += bytes; _itemBytes += bytes;
            var name = "a" + (++_seq);
            _outstanding.Add(name);
            return name;
        }
    }

    /// <summary>SignalWire echoed one of our marks. A name we no longer know (cleared earlier) is ignored.</summary>
    public void Echoed(string name)
    {
        lock (_gate) { if (_outstanding.Remove(name) && _outstanding.Count == 0) ResetLocked(); }
    }

    /// <summary>True while some mark we sent has not come back (and the lost-echo grace has not passed).</summary>
    public bool Playing { get { lock (_gate) { ExpireLocked(); return _outstanding.Count > 0; } } }

    /// <summary>
    /// The caller spoke. If the phone still has audio queued: what was queued and how much of the current item was played, and the state resets (SignalWire echoes the cleared marks later; they are ignored).
    /// Null when nothing is queued: there is nothing to clear and nothing to cut.
    /// </summary>
    public BargeIn? TryBargeIn()
    {
        lock (_gate)
        {
            ExpireLocked();
            if (_outstanding.Count == 0) return null;
            var elapsed = ElapsedMsLocked();
            var queued = Math.Max(0, _queueBytes / BytesPerMs - elapsed);
            var played = Math.Clamp(elapsed - _itemOffsetBytes / BytesPerMs, 0, _itemBytes / BytesPerMs);
            var cut = new BargeIn(_itemId, queued, played);
            ResetLocked();
            return cut;
        }
    }

    private long ElapsedMsLocked() => (long)time.GetElapsedTime(_queueStart).TotalMilliseconds;

    private void ExpireLocked()
    {
        if (_outstanding.Count > 0 && ElapsedMsLocked() > _queueBytes / BytesPerMs + (long)LostEchoGrace.TotalMilliseconds) ResetLocked();
    }

    private void ResetLocked() { _outstanding.Clear(); _queueBytes = 0; _itemOffsetBytes = 0; _itemBytes = 0; _itemId = null; }
}
