using Solvelt.Marketplace.Api.Data;
using Microsoft.Extensions.Options;
using System.Net.WebSockets;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>What a pipeline run ended with: <see cref="KeypadPause"/> is true when the stream ended for a keypad step (the call goes on and the next stream resumes it).</summary>
public sealed record VoicePipelineResult(bool KeypadPause);

/// <summary>One call on one pipeline. Disposed when the call's stream ends.</summary>
public interface IVoicePipelineSession : IAsyncDisposable
{
    /// <summary>Runs until the caller hangs up, the AI part ends, a transfer is done or the call is too long. The phone socket is already accepted.</summary>
    Task<VoicePipelineResult> RunAsync(WebSocket phone, VoiceCallState state, CancellationToken ct);
}

/// <summary>
/// The seam between the phone side (SignalWire stream, keypad steps, tokens, per-call state) and what listens and speaks (decision V-19). Option p1 is the Realtime line (<see cref="RealtimePipeline"/>, the
/// <see cref="RealtimeBridge"/> exactly as it was); the others are chained pipelines (<see cref="ChainedPipeline"/>), present only when their parts are configured.
/// </summary>
public interface IVoicePipeline
{
    string Id { get; }
    /// <summary>Opens what the call needs before the phone socket is accepted (for Realtime: the upstream socket). Null: it could not be opened, and the stream answers 503.</summary>
    Task<IVoicePipelineSession?> OpenAsync(VoiceCall call, CancellationToken ct);
}

/// <summary>Option p1: the current Realtime line. Connects the upstream socket as the endpoint always did and runs the unchanged <see cref="RealtimeBridge"/>.</summary>
public sealed class RealtimePipeline(IRealtimeConnector connector, IOptions<VoiceOptions> options, VoiceCallStore store, VoiceToolRunner tools, TimeProvider time, ILoggerFactory logs) : IVoicePipeline
{
    public string Id => VoicePipelineIds.Realtime;

    public async Task<IVoicePipelineSession?> OpenAsync(VoiceCall call, CancellationToken ct)
    {
        var log = logs.CreateLogger("Solvelt.Marketplace.Api.Voice");
        try
        {
            var upstream = await connector.ConnectAsync(call.Model, ct);
            return new Session(upstream, call, options, store, tools, time, log);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError("Voice call {Call}: the model session could not be opened ({Type}).", call.Id, ex.GetType().Name);
            await store.EndAsync(call.Id, VoiceOutcome.Abandoned, CancellationToken.None);
            return null;
        }
    }

    private sealed class Session(WebSocket upstream, VoiceCall call, IOptions<VoiceOptions> options, VoiceCallStore store, VoiceToolRunner tools, TimeProvider time, ILogger log) : IVoicePipelineSession
    {
        public async Task<VoicePipelineResult> RunAsync(WebSocket phone, VoiceCallState state, CancellationToken ct)
        {
            var bridge = new RealtimeBridge(phone, upstream, call, state, options, store, tools, time, log);
            await bridge.RunAsync(ct);
            return new VoicePipelineResult(bridge.KeypadPause);
        }

        public ValueTask DisposeAsync() { upstream.Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>
/// Finds the pipeline for a call: the chosen option when its parts are configured (any <see cref="IChainedPipelineParts"/> registered in the container), otherwise the Realtime line. With no parts registered, which is
/// how production starts, every option but p1 answers "not available on this line".
/// </summary>
public sealed class VoicePipelineRegistry
{
    private readonly RealtimePipeline _realtime;
    private readonly Dictionary<string, IVoicePipeline> _byId;

    public VoicePipelineRegistry(RealtimePipeline realtime, IEnumerable<IChainedPipelineParts> parts, IEnumerable<IChainedPartsSource> sources, IOptions<VoiceOptions> options, VoiceCallStore store, VoiceToolRunner tools, TimeProvider time, ILoggerFactory logs)
    {
        _realtime = realtime;
        _byId = parts.Concat(sources.SelectMany(s => s.Create(options.Value))).Where(p => p.OptionId != VoicePipelineIds.Realtime && VoicePipelineIds.IsOption(p.OptionId)).GroupBy(p => p.OptionId)
            .ToDictionary(g => g.Key, g => (IVoicePipeline)new ChainedPipeline(g.First(), realtime, options, store, tools, time, logs));
    }

    /// <summary>Option p1 is always available; the others only when their parts are configured.</summary>
    public bool IsAvailable(string option) => option == VoicePipelineIds.Realtime || _byId.ContainsKey(option);

    public IVoicePipeline For(VoiceCall call)
    {
        var option = VoicePipelineIds.OptionOf(call.Tier);
        return _byId.TryGetValue(option, out var p) ? p : _realtime;
    }
}
