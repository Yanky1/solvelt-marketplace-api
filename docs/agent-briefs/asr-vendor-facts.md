# ASR vendor facts (for feat/v-asr-tuning)

The buyer-marketplace orchestrator read the live vendor docs on 2026-11-22 and wrote down what they say. Build against this file and cite it as the source, together with the vendor URL quoted for each fact. Where the docs are silent the file says "not stated", and you must not fill that gap from memory. Quotes are verbatim.

Each vendor serves its pages as Markdown if you add `.md` to the URL.

---

## Deepgram (https://developers.deepgram.com)

### Keyterm prompting: `keyterm` (https://developers.deepgram.com/docs/keyterm)
- **Models:** "Keyterm Prompting is available for both monolingual and multilingual transcription using the Nova-3 Models, as well as Flux STT. To boost recognition of keywords using another Deepgram model (such as Nova-2), use the Keywords feature."
- **Streaming:** supported. `keyterm` is a query parameter of live `/v1/listen` (https://developers.deepgram.com/reference/speech-to-text/listen-streaming).
- **Several terms:** repeat the parameter: `?keyterm=term1&keyterm=term2`. A phrase is a single term, encoded with `%20` or `+`.
- **No weights or separators:** "None of the Don't forms return an error—the API accepts the value and treats it as a single literal keyterm, so it silently boosts nothing instead of failing." So never join terms with commas or semicolons.
- **Formatting:** "Keyterms preserve formatting (including case and punctuation)".
- **Limits:**
  - "Key Terms are limited to 500 tokens per request." The error text is "Keyterm limit exceeded. The maximum number of tokens across all keyterms is 500."
  - Best practice: "Stay well under the 500 token limit; focus on the most important 20-50 terms".
  - Character limit: not stated.
- **Mid-stream updates** (https://developers.deepgram.com/docs/configure): "Nova-3 streaming (`/v1/listen`) | Send a `keyterms` array in a Configure message".
  - "each `keyterms` array replaces the whole list, and an empty array `[]` clears all keyterms."
  - Nova-3 only; other models get `CONFIGURE_KEYTERMS_NOT_SUPPORTED`.
  - Over the limit: `CONFIGURE_KEYTERM_LIMIT_EXCEEDED`, and the old list is kept.
- **Languages:** no per-language restriction is stated. "How much keyterms improve recognition depends on the language and the terms".
- **Privacy:** keyterms appear in usage records. "add `redact_usage=true` to store them as `[REDACTED]`." **Decision for us:** send `redact_usage=true`.

### Legacy `keywords` (https://developers.deepgram.com/docs/keywords)
- "Keywords is only available for use with Nova-2, Nova-1, Enhanced, and Base speech-to-text models. For Nova-3, use Keyterm prompting."
- Syntax: `keywords=WORD:INTENSIFIER`, repeated. "Keywords are limited to 100 keywords per request."

### Model for 8 kHz phone audio
- **There is no `nova-3-phonecall`.** The Nova-3 variants are `nova-3` / `nova-3-general`, `nova-3-medical` and `nova-3-pharma` (https://developers.deepgram.com/docs/models-languages-overview).
- **`nova-2-phonecall`:** "Optimized for low-bandwidth audio phone calls". English only (`en`, `en-US`).
  - It is **missing from the live WebSocket `model` enum**, which lists `nova-phonecall`, `enhanced-phonecall` and `phonecall`.
  - Whether `nova-2-phonecall` streams: not stated.
- **Deepgram's own Twilio guide** (https://developers.deepgram.com/docs/twilio-and-deepgram-stt) uses plain `nova-3` with `encoding="mulaw", sample_rate=8000, channels=1`: "Requesting mulaw at 8 kHz mono matches Twilio's Media Streams format exactly, so the caller's bytes flow to Deepgram with no resampling."
- **Flux STT** is recommended for real-time agents: `flux-general-en`, and `flux-general-multi` for en, es, fr, de, hi, ru, pt, ja, it and nl.
- A stated "best model for 8 kHz": not stated.
- **Recommendation for us:** stay on `nova-3` with mulaw/8000 plus keyterms, because it is what the vendor's own phone guide uses. Make the model configurable. If you add a `nova-2-phonecall` choice, mark it "unverified for streaming" and fall back cleanly if Deepgram rejects it.

### Streaming audio and endpointing
- `encoding`: `mulaw` and `linear16`, among others. It is "required when raw, headerless audio packets are sent". `sample_rate` is required with `encoding`, and `channels` defaults to 1.
- `endpointing`: "enabled by default and set to 10 milliseconds". It takes an integer ms or `false`. Guidance: "300-500ms: Better for conversations where speakers pause mid-thought". Maximum: not stated.
- `utterance_end_ms`: minimum 1000 ms (also the default), maximum 5000. It requires `interim_results=true`.
- `vad_events`: default false.
- `smart_format`: default false. Note: "such as when speaking a phone number, Deepgram may purposefully wait for additional audio", and it finalizes after 3 s of silence.
- `interim_results`: default false.

---

## Gladia live v2 (https://docs.gladia.io; the main schema is https://docs.gladia.io/api-reference/v2/live/init)

### Custom vocabulary (https://docs.gladia.io/chapters/audio-intelligence/custom-vocabulary)
```json
{"realtime_processing": {
  "custom_vocabulary": true,
  "custom_vocabulary_config": {
    "vocabulary": ["Gladia", {"value":"Solaria"},
      {"value":"Salesforce","pronunciations":["sell force","sale forces"],"intensity":0.5,"language":"en"}],
    "default_intensity": 0.4 }}}
```
- `vocabulary` is required. Each item is a string or `{value (required), intensity 0..1, pronunciations string[], language}`.
- `default_intensity` ranges from 0 to 1. "When an entry omits `intensity`, `default_intensity` applies."
- **Both fields are needed** (https://docs.gladia.io/chapters/live-stt/recommended-parameters): "**Always provide both** the `custom_vocabulary` flag and a `custom_vocabulary_config`."
- **How it works:** it is post-processing. "Gladia applies phoneme-based matching after transcription"; "It does not retrain the speech model." `pronunciations` "are spelling variants, not IPA symbols".
- **Suggested intensity:** "Start at `default_intensity` 0.4 … The suggested 0.4–0.6 range is guidance".
- **Limits:**
  - Maximum number of entries: not stated.
  - "every transcribed word is compared against every entry; long lists increase false positives." **So keep the list short.**

### Audio / telephony
- `encoding`: `wav/pcm` (default), `wav/alaw`, `wav/ulaw`. "No need to add WAV headers to raw audio".
- `bit_depth`: 8, 16, 24 or 32 (default 16).
- `sample_rate`: **8000**, 16000, 32000, 44100 or 48000 (default 16000).
- `channels`: 1 to 8.
- "Each configured value must match the audio being sent."
- Whether to send μ-law 8 kHz directly or upsample it: **not stated**. Sending `wav/ulaw` with 8 bits at 8000 is a valid configuration.
- **Call-centre preset** (recommended-parameters):
  - `endpointing` 0.2 to 0.4;
  - `maximum_duration_without_endpointing` 15;
  - set `language_config.languages` explicitly; "avoids detection errors on noisy recordings";
  - `custom_vocabulary` true.

### Language
- `language_config.languages`: "If one language is set, it will be used for the transcription. Otherwise, language will be auto-detected".
- `code_switching`: "Do not enable `code_switching` with an empty `languages` list."
- **Yiddish is supported** (https://docs.gladia.io/chapters/language/supported-languages): `| Yiddish | yi | Yes | No | Yes | Yes |`. The columns are Solaria-1, Solaria-3, auto-discovery and code-switching, and translation.
- Hebrew: `he`, supported.

### Endpointing and model
- `endpointing`: default 0.05 s, range 0.01 to 10.
- `maximum_duration_without_endpointing`: default 5 s, range 5 to 60.
- `model`: the live API accepts `solaria-1` only. Solaria-3 is pre-recorded only.
- `pre_processing.audio_enhancer`: default false.
- `pre_processing.speech_threshold`: default 0.6, range 0 to 1. "A value close to 1 will apply stricter thresholds, making it less likely to detect background sounds as speech."
- Telephony advice for either pre-processing setting: not stated.

---

## OpenAI Realtime GA, transcription and TTS (https://developers.openai.com/api/...)

Sources:
- [CE] https://developers.openai.com/api/reference/resources/realtime/client-events.md
- [VAD] https://developers.openai.com/api/docs/guides/realtime-vad.md
- [RT-TX] https://developers.openai.com/api/docs/guides/realtime-transcription.md
- [TTS] https://developers.openai.com/api/docs/guides/text-to-speech.md
- [SPEECH-REF] https://developers.openai.com/api/reference/resources/audio/subresources/speech/methods/create.md

### ⚠ Deprecations that affect the plan
- **Transcription:** "`whisper-1`, `gpt-4o-transcribe`, `gpt-4o-mini-transcribe`, `gpt-4o-transcribe-diarize`, and `/v1/audio/translations` are deprecated and will shut down on **February 26, 2027**." Source: https://developers.openai.com/api/docs/guides/speech-to-text.md
- **TTS:** "The `tts-1`, `tts-1-hd`, `gpt-4o-mini-tts`, `gpt-4o-mini-tts-2025-03-20`, and `gpt-4o-mini-tts-2025-12-15` models are deprecated and scheduled to shut down on January 6, 2027 … for new integrations, migrate to `gpt-realtime-2.1-mini` through the Realtime API." [SPEECH-REF] Also: "`gpt-realtime-2.1-mini` isn't a supported model for `/v1/audio/speech`." [TTS]
- **Recommended live transcription model:** "Start with `gpt-live-transcribe`" [RT-TX].

### Noise reduction (Realtime): `session.audio.input.noise_reduction`
- The object is `{type, strength}`. "you must set `type` to `near_field` or `far_field`."
- "`near_field` is for close-talking microphones such as headphones, `far_field` is for far-field microphones such as laptop or conference room microphones." [CE]
- `strength`: 0 to 1, default 1.
- It "filters audio added to the input audio buffer before it is sent to VAD and the model."
- Which value suits a phone call: **not stated**. `near_field` is our inference, because a handset sits close to the mouth. Make it configurable and record it as an inference.

### Turn detection: `session.audio.input.turn_detection`
- The default is `server_vad`; the other mode is `semantic_vad`.
- `semantic_vad.eagerness`: "`low` will wait longer for the user to continue speaking, `high` will respond more quickly. `auto` is the default and is equivalent to `medium`. `low`, `medium`, and `high` have max timeouts of 8s, 4s, and 2s respectively." [CE]
- `server_vad`:
  - `threshold`: default 0.5. "A higher threshold will require louder audio to activate the model, and thus might perform better in noisy environments."
  - `prefix_padding_ms`: default 300.
  - `silence_duration_ms`: default 500.
  - `idle_timeout_ms` (server_vad only): "useful for situations in which a long pause from the user is unexpected, such as a phone call", 5000 to 30000.
- `create_response` and `interrupt_response` both default to true.

### Input transcription: `session.audio.input.transcription`
- **Models** [CE]: `whisper-1`, `gpt-transcribe`, `gpt-live-transcribe`, `gpt-4o-mini-transcribe`, `gpt-4o-mini-transcribe-2025-12-15`, `gpt-4o-transcribe`, `gpt-4o-transcribe-diarize`, `gpt-realtime-whisper`.
- **`language`:** ISO-639-1. **`languages`** (an array) is for `gpt-transcribe` and `gpt-live-transcribe`: "`gpt-live-transcribe` uses `languages` instead of the singular `language` field. Don't send both." [RT-TX]
- **`keywords`** (an array) is supported by `gpt-transcribe` and `gpt-live-transcribe`. **Use this for catalog words on those models.** The API rejects the update if a keyword contains certain forbidden characters; see [RT-TX] for the list.
- **`prompt`:**
  - "For `whisper-1`, the prompt is a list of keywords. For `gpt-4o-transcribe` models … the prompt is a free text string".
  - "Prompt is not supported with `gpt-realtime-whisper`".
  - Length: 224 tokens for whisper-1; for other models the limit is not stated, and the API rejects an over-long prompt.

### Transcription-only Realtime session (for option 7)
- Connect to `wss://api.openai.com/v1/realtime?intent=transcription` **without `model` in the URL**, then send `session.update` with `session.type: "transcription"`. [RT-TX]
- "`gpt-live-transcribe` and `gpt-realtime-whisper` require turn detection to be omitted or set to `null`. Send `input_audio_buffer.commit` to finish each audio turn with these models." [VAD] For `gpt-live-transcribe` we therefore need our own end-of-speech detection.
- **Events:**
  - `conversation.item.input_audio_transcription.delta` and `.completed` (which carries `transcript`), plus `.failed`;
  - `input_audio_buffer.committed` gives the `item_id`.
  - "Ordering between completion events from different speech turns isn't guaranteed. Use `item_id` to match."
- "`gpt-live-transcribe` doesn't return word-level timestamps, speaker labels, or transcription confidence scores." Logprobs come via `"include": ["item.input_audio_transcription.logprobs"]`; which models return them is not stated.
- **For n-best (slice C):** OpenAI gives no n-best alternatives, and `gpt-live-transcribe` gives no confidence. Treat OpenAI as a single hypothesis.

### Audio formats (Realtime GA)
- Input and output are set separately: `session.audio.input.format` and `session.audio.output.format`.
  - `{type:"audio/pcm", rate:24000}`: "Only a 24kHz sample rate is supported."
  - `{type:"audio/pcmu"}`: "The G.711 μ-law format."
  - `{type:"audio/pcma"}`: A-law.
- Whether Realtime pcmu is 8 kHz is not stated in the Realtime reference. The GPT-Live docs, a different API, do state it: `{"type":"audio/pcmu","rate":8000}` is "G.711 μ-law at 8 kHz".
- Input and output formats can differ: https://developers.openai.com/api/docs/guides/realtime-conversations.md has an example with pcm in and pcmu out.

### TTS: `POST /v1/audio/speech` [SPEECH-REF]
- **Models:** `tts-1`, `tts-1-hd`, `gpt-4o-mini-tts`, `gpt-4o-mini-tts-2025-12-15`. **All are deprecated and shut down on 2027-01-06** (see above).
- **Voices:** `alloy`, `ash`, `ballad`, `coral`, `echo`, `fable`, `onyx`, `nova`, `sage`, `shimmer`, `verse`, `marin`, `cedar`. "For best quality, we recommend using `marin` or `cedar`."
- **Formats:** `mp3`, `opus`, `aac`, `flac`, `wav`, `pcm`. **There is no μ-law.** `pcm` is "raw samples in 24kHz (16-bit signed, low-endian), without the header". "For the fastest response times, we recommend using `wav` or `pcm`."
- **Streaming:** chunked transfer is supported. `stream_format`: `sse` or `audio`.
- **`instructions`:** up to 4096 characters; does not work with `tts-1` or `tts-1-hd`.
- **`input`:** at most 4096 characters. `speed`: 0.25 to 4.0.
- **Realtime voices** (`audio.output.voice`): `alloy`, `ash`, `ballad`, `coral`, `echo`, `sage`, `shimmer`, `verse`, `marin`, `cedar`.

---

## SignalWire (already researched; see docs/VOICE-PIPELINE-OPTIONS.md section 6 and docs/VOICE.md section 12)
- `<Stream>` supports PCMU@8000h (the default), plus L16@16000h and L16@24000h; codec_error if the codec is not enabled.
- Gather speech: `language`, `hints`, `speechTimeout=auto`, `enhanced` plus `speechModel=phone_call`.
