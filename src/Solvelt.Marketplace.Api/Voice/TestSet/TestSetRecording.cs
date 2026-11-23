using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvelt.Marketplace.Api.Data;
using Solvelt.Marketplace.Api.Outbox;

namespace Solvelt.Marketplace.Api.Voice.TestSet;

/// <summary>
/// Fetches and deletes a recording made by SignalWire's cXML Record (decision V-46). Facts from SignalWire's docs, read by the orchestrator on 2026-11-23: a recording is
/// <c>GET https://{space}.signalwire.com/api/laml/2010-04-01/Accounts/{AccountSid}/Recordings/{Sid}.wav</c>, with Basic authentication (project id and an API token with the Management scope) when
/// "Protect Recording Media URLs" is on; it is a 404 until the recording is completed; the WAV's encoding and rate are not stated (the header is read, never assumed). A recording is deleted with
/// <c>DELETE .../Recordings/{Sid}</c> (the token needs the Voice scope). Recordings otherwise stay stored indefinitely, so one is never left there on purpose.
/// </summary>
public sealed class SignalWireRecordings(HttpClient http, IOptions<SignalWireOptions> options)
{
    private static readonly Regex SidPattern = new("^[0-9A-Za-z-]{20,64}$", RegexOptions.Compiled);
    public static bool IsValidSid(string? sid) => sid is not null && SidPattern.IsMatch(sid);

    private HttpRequestMessage Request(HttpMethod m, string sid, string suffix)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Space) || string.IsNullOrWhiteSpace(o.ProjectId) || string.IsNullOrWhiteSpace(o.ApiToken)) throw new InvalidOperationException("SignalWire:Space, ProjectId and ApiToken are required to fetch a recording.");
        if (!IsValidSid(sid)) throw new ArgumentException("Not a recording sid.", nameof(sid));
        var baseUrl = string.IsNullOrWhiteSpace(o.ApiBaseUrl) ? $"https://{o.Space}.signalwire.com" : o.ApiBaseUrl.TrimEnd('/');
        var req = new HttpRequestMessage(m, $"{baseUrl}/api/laml/2010-04-01/Accounts/{Uri.EscapeDataString(o.ProjectId)}/Recordings/{sid}{suffix}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(o.ProjectId + ":" + o.ApiToken)));
        return req;
    }

    /// <summary>The recording's WAV, or null while it is not available yet (404). Any other failure throws <see cref="HttpRequestException"/> with the status only.</summary>
    public async Task<byte[]?> FetchWavAsync(string sid, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Get, sid, ".wav");
        using var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"SignalWire answered {(int)r.StatusCode}", null, r.StatusCode);
        if (r.Content.Headers.ContentLength is > WavAudio.MaxBytes) throw new InvalidDataException("The recording is too large.");
        await using var s = await r.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[81920]; int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0) { ms.Write(buf, 0, n); if (ms.Length > WavAudio.MaxBytes) throw new InvalidDataException("The recording is too large."); }
        return ms.ToArray();
    }

    /// <summary>True when the recording is gone (deleted now, or already gone: 404).</summary>
    public async Task<bool> DeleteAsync(string sid, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Delete, sid, "");
        using var r = await http.SendAsync(req, ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return true;
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"SignalWire answered {(int)r.StatusCode}", null, r.StatusCode);
        return true;
    }
}

/// <summary>
/// Copies a finished recording into the private container, checks the copy, and only then deletes it at SignalWire (decision V-46). Retried by the outbox until it is done, so a failed delete is tried again
/// and a recording is never kept at SignalWire on purpose; if the copy can never be made the message ends up dead and an error is logged (the Sid only). Idempotent: a retry after a good upload does not
/// upload twice.
/// </summary>
public sealed class TestSetCopyHandler(ITestSetStore store, SignalWireRecordings recordings, ILogger<TestSetCopyHandler> log) : IOutboxHandler
{
    public const string Type = "voice.testset.copy";
    public sealed record Payload(string Sid, string Name);
    public IReadOnlyCollection<string> Types { get; } = [Type];

    public async Task<OutboxResult> HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        Payload? p;
        try { p = message.Payload is null ? null : JsonSerializer.Deserialize<Payload>(message.Payload, Solvelt.Marketplace.Api.Portal.PortalJson.Options); }
        catch (JsonException) { return OutboxResult.Dead("invalid_payload"); }
        if (p is null || !SignalWireRecordings.IsValidSid(p.Sid) || !RecordingName.TryParse(p.Name, out _)) return OutboxResult.Dead("invalid_payload");
        try
        {
            var wav = await recordings.FetchWavAsync(p.Sid, ct);
            if (wav is null) return OutboxResult.Retry("not_ready");
            try { _ = WavAudio.ToMulaw8k(wav); }
            catch (FormatException) { log.LogError("Test set recording {Sid} is not a usable WAV file and was left at SignalWire.", p.Sid); return OutboxResult.Dead("unusable_audio"); }
            var existing = await store.SizeAsync(p.Name, ct);
            if (existing is null)
            {
                await store.WriteAsync(p.Name, wav, ct);
                if (await store.SizeAsync(p.Name, ct) != wav.Length) return OutboxResult.Retry("size_mismatch");   // nothing is deleted until the copy is verified
            }
            else if (existing != wav.Length)
            {
                log.LogError("Test set recording {Sid}: a different recording already has the name; it was left at SignalWire.", p.Sid);   // never delete audio that was not stored
                return OutboxResult.Dead("name_taken");
            }
            await recordings.DeleteAsync(p.Sid, ct);
            log.LogInformation("Test set recording {Sid} stored and deleted at SignalWire.", p.Sid);
            return OutboxResult.Delivered();
        }
        catch (Exception ex) when (ex is HttpRequestException or Azure.RequestFailedException or IOException or TaskCanceledException or InvalidOperationException)
        {
            log.LogWarning("Test set recording {Sid}: {ExceptionType}; it will be tried again.", p.Sid, ex.GetType().Name);
            return OutboxResult.Retry("copy_" + ex.GetType().Name);
        }
    }
}
