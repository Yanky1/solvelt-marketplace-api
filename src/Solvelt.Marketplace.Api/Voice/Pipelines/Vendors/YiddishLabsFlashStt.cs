using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Solvelt.Marketplace.Api.Voice;

/// <summary>
/// Yiddish Labs "Flash" speech to text (options p4 and p5). The vendor documents ONE synchronous request per recording (<c>POST https://flash.yiddishlabs.com/v1/audio/transcriptions</c>, a WAV, a Bearer key) and no streaming
/// recogniser, so this adapter listens for the end of speech itself: a plain energy detector on the telephone's mu-law (all sample-based, no clock), the whole utterance is uploaded when the caller stops, and the text comes
/// back as one <c>Final</c>. What is said is held in memory for the turn only: nothing is written, logged or kept, and the vendor's own statement is that audio is processed in memory (relayed, V-22).
/// The site's other transcription endpoints (which bill a minute and store transcripts) are never called.
/// </summary>
public sealed class YiddishLabsFlashStt(YiddishLabsOptions options, HttpClient http, ILogger log) : IStreamingStt
{
    internal static readonly Uri Endpoint = new($"https://{VendorHosts.YiddishLabsFlash}/v1/audio/transcriptions");
    private const int FrameBytes = 160, FrameMs = 20, LoudFramesToStart = 3, PreRollFrames = 15, MaxUtteranceMs = 15_000, MinSpeechMs = 200;

    public Task<ISttSession> OpenAsync(CancellationToken ct) => Task.FromResult<ISttSession>(new Session(options, http, log));

    private sealed class Session : ISttSession
    {
        private readonly YiddishLabsOptions _o; private readonly HttpClient _http; private readonly ILogger _log;
        private readonly Channel<SttEvent> _events = Channel.CreateUnbounded<SttEvent>();
        private readonly Channel<short[]> _uploads = Channel.CreateUnbounded<short[]>();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _worker;
        private readonly List<byte> _pending = [];
        private readonly Queue<short[]> _preRoll = new();
        private readonly List<short> _utterance = [];
        private bool _inSpeech, _failed; private int _loudRun, _silenceMs, _utteranceMs, _speechMs, _uploadsDone;

        public Session(YiddishLabsOptions o, HttpClient http, ILogger log) { _o = o; _http = http; _log = log; _worker = Task.Run(UploadLoopAsync); }

        public ValueTask SendAudioAsync(ReadOnlyMemory<byte> mulaw8k, CancellationToken ct)
        {
            if (_failed || mulaw8k.IsEmpty) return ValueTask.CompletedTask;
            _pending.AddRange(mulaw8k.Span.ToArray());
            while (_pending.Count >= FrameBytes)
            {
                var frame = PhoneAudio.DecodeMulaw(_pending.GetRange(0, FrameBytes).ToArray());
                _pending.RemoveRange(0, FrameBytes);
                OnFrame(frame);
            }
            return ValueTask.CompletedTask;
        }

        private void OnFrame(short[] pcm)
        {
            long sum = 0; foreach (var s in pcm) sum += Math.Abs((int)s);
            var loud = sum / pcm.Length >= _o.VadEnergy;
            if (!_inSpeech)
            {
                _preRoll.Enqueue(pcm); while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
                _loudRun = loud ? _loudRun + 1 : 0;
                if (_loudRun < LoudFramesToStart) return;
                _inSpeech = true; _silenceMs = 0; _utteranceMs = _preRoll.Count * FrameMs; _speechMs = LoudFramesToStart * FrameMs;      // the pre-roll is for the recogniser, it is not speech
                foreach (var f in _preRoll) _utterance.AddRange(f);
                _preRoll.Clear();
                _events.Writer.TryWrite(new SttEvent(SttEventKind.SpeechStarted));
                return;
            }
            _utterance.AddRange(pcm); _utteranceMs += FrameMs; _speechMs += FrameMs;
            _silenceMs = loud ? 0 : _silenceMs + FrameMs;
            if (_silenceMs >= _o.VadSilenceMs || _utteranceMs >= MaxUtteranceMs) EndUtterance();
        }

        private void EndUtterance()
        {
            _inSpeech = false; _loudRun = 0;
            var speechMs = _speechMs - _silenceMs;           // the speech, without the silence that ended it
            var samples = _utterance.ToArray(); _utterance.Clear();
            _events.Writer.TryWrite(new SttEvent(SttEventKind.SpeechEnded));
            if (speechMs >= MinSpeechMs) _uploads.Writer.TryWrite(samples);          // a click or a cough is not sent
        }

        public async IAsyncEnumerable<SttEvent> Events([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var e in _events.Reader.ReadAllAsync(ct)) yield return e;
        }

        private async Task UploadLoopAsync()
        {
            try
            {
                await foreach (var samples in _uploads.Reader.ReadAllAsync(_cts.Token))
                {
                    var text = await TranscribeAsync(samples, _cts.Token);
                    _uploadsDone++;
                    if (!string.IsNullOrWhiteSpace(text)) _events.Writer.TryWrite(new SttEvent(SttEventKind.Final, text.Trim(), _o.SttLanguage is "auto" ? null : _o.SttLanguage));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _failed = true; _log.LogWarning("Yiddish Labs recogniser stopped ({Type}) after {Count} uploads.", ex.GetType().Name, _uploadsDone); }   // never the message
            finally { _events.Writer.TryComplete(); }
        }

        private async Task<string?> TranscribeAsync(short[] pcm8k, CancellationToken ct)
        {
            var wav = _o.UploadSampleRate == 16000 ? PhoneAudio.Wav(PhoneAudio.Upsample(pcm8k, 2), 16000) : PhoneAudio.Wav(pcm8k, 8000);
            for (var attempt = 0; ; attempt++)
            {
                using var form = new MultipartFormDataContent();
                var file = new ByteArrayContent(wav); file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form.Add(file, "file", "turn.wav");
                form.Add(new StringContent(_o.SttModel), "model"); form.Add(new StringContent(_o.SttLanguage), "language"); form.Add(new StringContent("json"), "response_format");
                if (!string.IsNullOrWhiteSpace(_o.SttPrompt)) form.Add(new StringContent(_o.SttPrompt), "prompt");
                using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = form };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.FlashApiKey);
                using var resp = await _http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    return doc.RootElement.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                }
                // 503 (no capacity) and 429 (rate limit, honour Retry-After) are retried once inside a short budget; everything else (401, 402 out of credits, 413, ...) ends the recogniser: the pipeline then tells the caller
                var wait = resp.StatusCode == HttpStatusCode.TooManyRequests ? (resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1)) : TimeSpan.FromMilliseconds(250);
                if (attempt == 0 && resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && wait <= TimeSpan.FromSeconds(2)) { await Task.Delay(wait, ct); continue; }
                throw new HttpRequestException($"The recogniser answered {(int)resp.StatusCode}.", null, resp.StatusCode);       // the body is not read: it could echo the audio's text
            }
        }

        public async ValueTask DisposeAsync()
        {
            _uploads.Writer.TryComplete(); _cts.Cancel();
            try { await _worker; } catch { /* ended by cancellation */ }
            _events.Writer.TryComplete(); _cts.Dispose();
        }
    }
}
