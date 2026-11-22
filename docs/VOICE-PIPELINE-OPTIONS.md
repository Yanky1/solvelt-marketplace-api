# Voice pipeline options 2 to 6: which vendor, why, and what must be true first

Status: **design note, nothing here is built.** Options 2 to 5 exist only as the vendor-neutral skeleton merged in V-P1 (`IVoicePipeline`, `ChainedBridge`, `ParallelStt`, all tested against fakes); an option without a configured adapter answers "not available on this line" and the call runs on option 1. This note chooses ONE vendor per option, says why from the vendors' own documentation, lists the final runtime hosts and the Azure setting names, and lists what is still OPEN. Decision record: V-22 in `DECISIONS.md`. Facts were read on 2026-11-19 from the pages named in section 9; a figure that could not be confirmed is marked **OPEN**.

The test line only (V-19): the menu is off by default and limited to `Voice:PipelineMenu:TestNumbers`. Option 1 (OpenAI Realtime, `RealtimeBridge`) stays the default and the fallback of every other option. Nothing in this note changes the keypad payment (it stays outside every pipeline) or the rule that transcripts are held in memory only.

## 1. The options

| Option | What the caller gets | Speech to text (STT) | Text to speech (TTS) | Our model |
|---|---|---|---|---|
| p1 | today's line | OpenAI Realtime (speech to speech) | OpenAI Realtime | OpenAI Realtime |
| p2 | a mainstream English-first chain, to compare cost and latency | **Deepgram** streaming | **ElevenLabs** streaming | **Claude** (decisions V-33 and V-40; this note first named an OpenAI text model), same tools and instructions |
| p3 | the caller may speak Yiddish and is understood live; the assistant answers in English | **Gladia** live (Solaria-1) | **ElevenLabs** streaming | same |
| p4 | Yiddish both ways, one vendor | **Yiddish Labs** Flash (one request per caller turn) | **Yiddish Labs** realtime TTS | same |
| p5 | Yiddish both ways with a second opinion on what was said | **Gladia** live + **Yiddish Labs** Flash in parallel, a judge picks the text (`ParallelStt`) | **Yiddish Labs** realtime TTS | same, plus the judge |

p3 answering in English is a deliberate choice, not a gap: it keeps Yiddish Labs out of p3, so the consent line in section 6 is needed only for p4 and p5, as instructed. If Ronald wants a Yiddish voice in p3 it must use Yiddish Labs TTS and then p3 needs the consent line too (his decision).

## 2. Why these vendors (and who was not chosen)

**Yiddish is the constraint.** Of the vendors whose documentation could be read, only two document Yiddish speech recognition for a live call, and only one documents Yiddish speech output.

| Vendor | Yiddish STT | Yiddish TTS | Phone audio | Notes |
|---|---|---|---|---|
| Yiddish Labs | yes (`language` `yi`, default) | yes (their realtime TTS; voices Mair, Chaim, Tzvi, Yoel *(relayed)*) | STT file upload of any common format; TTS raw PCM 16 kHz (48 kHz HD), we resample to 8 kHz mu-law | the only Yiddish TTS found |
| Gladia | **yes, live**: the language table marks `yi` supported on Solaria-1, and live transcription "supports solaria-1 only" | no | live examples are `wav/pcm` 16 kHz; mu-law 8 kHz **OPEN** | Yiddish also supports code switching |
| AssemblyAI | marketing page says Yiddish with "real-time voice agents"; but the streaming documentation lists 32 streaming languages and **Yiddish is not among them** (Hebrew is) | TTS product exists; Yiddish no | PCM, AAC or Opus; no mu-law in the page read | **conflict, OPEN**: ask the vendor; not used until resolved |
| Deepgram | no (Hebrew `he` only) | n/a here | streaming accepts `encoding=mulaw` and `sample_rate=8000` directly | best fit for an English baseline |
| ElevenLabs | no (Hebrew only; no Yiddish string in any page read) | no | TTS `output_format` includes `ulaw_8000` and `alaw_8000`; realtime STT accepts `ulaw_8000` and `pcm_8000` | lowest-latency English voice |
| Microsoft, Google, Cartesia | no Yiddish found in the pages read (Microsoft and Cartesia) / page not reachable (Google redirects to `docs.cloud.google.com`, not allowed here) | | | not chosen; Google **OPEN** until that host is allowed |

**p2 = Deepgram + ElevenLabs.** The cleanest phone fit: Deepgram takes mu-law 8 kHz as it comes off the telephone and ElevenLabs can return mu-law 8 kHz, so no resampling or transcoding adds delay. It answers one question the owner can ask without Yiddish: how much cheaper or faster is a chained pipeline than the OpenAI speech-to-speech line for the same conversation. No Yiddish.

**p3 = Gladia + ElevenLabs.** The only documented live Yiddish recognition, paired with the same English voice as p2, so the difference between p2 and p3 is the recognizer alone.

**p4 = Yiddish Labs end to end.** The only vendor with Yiddish voice output. Its recognizer is documented as a file upload, so each caller turn is cut by our own silence detector (`SilenceTurnDetector`, `EndpointMs`) and sent as one request; its latency on two to five second clips is **OPEN** until a key exists. The adapter is shaped so a streaming recognizer can replace the upload later without touching the pipeline (`IStreamingStt` already hides the difference).

**p5 = Gladia and Yiddish Labs in parallel, Yiddish Labs voice.** Gladia gives the live events (speech started, partials, speech ended) that drive turn taking; Yiddish Labs Flash adds a second transcript of the same turn; a judge (a small OpenAI text model) picks, within the hard budget (`ParallelWaitMs`, `ParallelBudgetMs`, fallback to the primary engine's text). See section 5 for the one open question about the judge.

## 3. Yiddish Labs in detail

Sources: their public developer documentation (`https://app.yiddishlabs.com/developer/api-docs`, tabs Flash API, Realtime TTS, TTS API; it is a single-page app, so it was read from the site's own script bundle), plus the account check Ronald ran, relayed by the orchestrator. Items from the relay only are marked *(relayed)*.

**STT (Flash): one synchronous request per caller turn.**
- `POST https://flash.yiddishlabs.com/v1/audio/transcriptions`, `Authorization: Bearer yl_flash_...`
- multipart: `file` (WAV), `model=yiddishlabs-3-turbo`, `language=yi|en|he|auto`, optional `prompt` (bias words such as product names), `response_format=json` giving `{text, duration}` *(relayed)*.
- Price *(relayed)*: about 100 credits per audio minute, billed per second. Price per credit: **OPEN**.
- Response headers: `X-Credits-Charged`, `X-Billed-Seconds`, `X-RateLimit-*`, `X-Request-ID` (read from the docs; rate limit 60 a minute in the example).
- Errors *(relayed)*: 401 `invalid_api_key`; 402 `insufficient_credits` (fall back); 413; 429 (honour `Retry-After`); 503 `no_capacity` and `feature_unavailable` (retry inside the budget, then fall back).
- Never use `app.yiddishlabs.com/api/v1/transcriptions/sync` *(relayed)*: it bills a one-minute minimum, needs a different key type (`yl_live_`) and stores transcripts. The admin and internal endpoints of the site (including its Ably live-transcription page) are **not** public API and are not used unless Yiddish Labs confirms them.
- Retention, as quoted *(relayed)*: "audio is processed in memory and never written to disk; transcripts are never stored".
- OPEN until a key exists: whether 8 kHz audio is accepted (the upload rate is configurable, 8 kHz or 16 kHz PCM WAV, upsampling when needed) and the latency on short clips.

**TTS (realtime, PREVIEW).**
- `wss://rt.yiddishlabs.com/v1/tts/realtime?voice=ID[&hd=true][&idle_timeout_s=N]` (idle 5 to 120 s, default 30); voices at `GET https://rt.yiddishlabs.com/v1/tts/voices`; same `yl_flash_` key; server side only.
- Client messages: `input.text` (up to 10,000 characters, fragments allowed), `input.done`, `cancel` (barge-in: discards pending text and audio, unbilled; the server answers `cancelled`), `playback {position_s}` about every 0.5 s, `keepalive`, `finish`. Server output: binary PCM s16le mono at the `sample_rate` in `session.ready` (always read it) and JSON `session.ready`, `segment.start`, `segment.done`, `done`, `cancelled`, `error`, `session.closed`.
- It cuts segments at `.`, `?`, `!` or a newline: our sentence chunker already sends whole sentences and sends `input.done` at the end of a reply.
- Billing per synthesized word at the voice's credit rate (an example voice: 4 credits a word). Credit price **OPEN**.
- Limits *(relayed)*: 5 concurrent sessions per account (so at most five Yiddish calls at once; the sixth falls back), 30-minute sessions, one session per call. Close codes *(relayed)*: reconnect with backoff on 4001, 4002, 4010, 4012; 4003 (out of credits) falls back to the OpenAI voice; 4011 (voice failed to load) falls back.
- Our side: resample 16 kHz PCM to 8 kHz mu-law for SignalWire; on `cancel` also send `clear` to SignalWire (the same mark/clear logic as option 1); report `playback` from the marks the phone echoes.

**Terms constraints that shape the design** (from the relay; the terms text itself was not read here):
1. Terms §5: no third-party personal data without consent. See the consent line, section 6.
2. Terms §7 and Privacy §4: anonymised inputs and outputs may be used to train their models; the opt-out is a written request that has **not been made yet**. **OWNER ACTION required before any real caller uses p4 or p5** (listed in section 8).
3. Terms §6: their outputs may not be used to train or evaluate other models. See section 5 (the judge).
4. No SLA, and the TTS is a preview: p4 and p5 are test-line options only and are never the default.

## 4. Fallbacks, limits and what the code will do

- **A vendor that cannot start hands the call to option 1; one that fails during the call ends it with an apology (decision V-25, built; this paragraph first described only the intent).** Before the call begins the speech recogniser is opened before the phone socket is accepted; when it refuses, the call continues on the OpenAI Realtime line on the same call, with the model and tier it started with put back (the menu keeps them in `BaseTier` and `BaseModel`), priced by that real model, and the failed option recorded in `FallbackFrom`, so the comparison stays honest. During the call, continuing on Realtime is not possible without a second greeting (the Realtime bridge needs the phone stream's `start` event, which the chained bridge has already consumed), so when the recogniser goes away or the voice cannot speak a sentence the platform says a short apology itself (never through the failing vendor) and ends the call with the outcome `vendor_lost`. A failed text-model turn is logged by type and does not end the call (V-19). **Still to be built for p4:** the Yiddish Labs specifics of this paragraph (a sixth concurrent TTS session, close codes 4003 and 4011) become the same two paths once the Yiddish Labs adapter exists: a refusal at the start hands over, a failure during the call apologises.
- **Budgeted retries.** Flash 503: retry inside the turn's latency budget, then use the other engine's text (p5) or fall back (p4). 429: wait the `Retry-After` only if it fits the budget.
- **Concurrency gate** for the five-session limit: `Voice:YiddishLabs:MaxConcurrentTtsSessions` (default 5), counted in memory per process; a second process would need a shared count, which is **not** designed (the test line is one instance).
- **Nothing stored or logged:** adapters log counts, status codes and exception types only; request and response bodies (audio and text) are never logged or persisted, and the vendors' own retention is listed in section 7.
- **Keypad payment and digits** never reach any vendor (already tested for the skeleton; the adapters inherit it because digits are dropped before the STT).

## 5. Option 5 and the judge (Terms §6) — a question for Ronald

The judge is a text model (Claude by default since V-33; this note first named an OpenAI one) that reads the candidate transcripts of one turn (Gladia's and Yiddish Labs') and returns the one to use. Whether choosing between a competitor's output and another engine's output counts as "evaluating other models" under Terms §6 is a legal reading this note cannot make. **Flagged for Ronald.** Until he answers, p5 is designed so Yiddish Labs output can be kept out of the judge by one setting: `Voice:Pipelines:P5:YiddishLabsInJudge` (default **false**). With it false, p5 uses Gladia for the text the model sees and Yiddish Labs only for the voice; with it true, Flash's text is a candidate as designed. Either way the judge's choice is never logged or stored. *(Built in decision V-29: with the switch false Flash is never contacted at all.)*

## 6. Consent line for p4 and p5 (draft; the wording is Ronald's decision)

Terms §5 forbids sending third parties' personal data without consent, and a caller's voice and the order details they speak are exactly that. The consent line plays after the existing disclosure and before the pipeline starts, only for p4 and p5, and needs one keypad press:

> *DRAFT, for Ronald to word:* "For this test, what you say is processed by a Yiddish speech partner, Yiddish Labs, which may use anonymised recordings to improve its service. Press 1 to continue, or any other key to use our standard assistant instead."

Any key but 1, or no key, runs option 1. The choice is not stored beyond the call's `Tier` column (`p4` or `p5` records that the caller continued). The menu code for the line is not written until Ronald has approved the wording; the setting `Voice:YiddishLabs:ConsentLine` holds the approved text (not a secret).

## 7. Retention and training per vendor (what the choice depends on)

| Vendor | Retention and training as documented |
|---|---|
| Yiddish Labs | *(relayed)* audio in memory only, transcripts never stored; but Terms §7 / Privacy §4 allow anonymised inputs and outputs to be used for training until the written opt-out is made (OWNER ACTION) |
| Gladia | a data retention page exists (windows for audio, transcripts and metadata; "Enterprise zero data retention"); **the figures were not extracted: OPEN, read before p3 or p5 are used by a real caller** |
| Deepgram | its Model Improvement Partnership Program page says it stores fractional increments of data for model improvement; opt-out parameter and terms **OPEN** to confirm |
| ElevenLabs | zero-retention page needs a login (403); **OPEN** |
| AssemblyAI (not used) | if opted out of training, zero data retention of audio and transcripts for streaming |
| OpenAI (our model and the judge) | as for option 1 today (VOICE.md section 3) |

## 8. Owner actions and decisions before any real caller

1. Make Yiddish Labs' written training opt-out request (Terms §7 / Privacy §4). **Required before real callers on p4 or p5.**
2. Approve the consent line wording (section 6).
3. Decide the Terms §6 question for the p5 judge (section 5), or leave `YiddishLabsInJudge` false.
4. Ask Yiddish Labs by email (Ronald is doing this): is there a streaming recognizer, what is the price per credit, is 8 kHz audio accepted, what is the latency on short clips, which voices exist, and a test key. Until a key exists nothing can be measured and the adapters are built and tested against fakes only.
5. Ask AssemblyAI whether Yiddish streaming exists (the pages conflict).
6. Read Gladia's, Deepgram's and ElevenLabs' retention and training terms (section 7) before p2, p3 or p5 touch real callers; allow `docs.cloud.google.com` if Google is to be assessed.
7. Put the keys in Azure (section 10). No key goes in the repository, chat or a log.

## 9. Sources (read 2026-11-19)

Yiddish Labs: `https://app.yiddishlabs.com/developer/api-docs` (served as a script bundle; Flash API and Realtime TTS tabs) and the relay above. Gladia: `docs.gladia.io/chapters/language/supported-languages`, `docs.gladia.io/chapters/live-stt/getting-started`, `docs.gladia.io/chapters/limits-and-specifications/data-retention`, `www.gladia.io/pricing`. AssemblyAI: `www.assemblyai.com/languages/yiddish`, `www.assemblyai.com/docs/speech-to-text/universal-streaming/multilingual-transcription.md`, `.../docs/faq/data-retention.md`, `.../pricing`. Deepgram: `developers.deepgram.com/docs/models-languages-overview.md`, `.../docs/encoding.md`, `.../docs/the-deepgram-model-improvement-partnership-program.md`, `deepgram.com/pricing`. ElevenLabs: `elevenlabs.io/docs/overview/models.md`, `.../docs/api-reference/text-to-speech/convert.md` and `.../v-1-text-to-speech-voice-id-stream-input.md`, `.../speech-to-text/v-1-speech-to-text-realtime.md`, `elevenlabs.io/pricing/api`. Re-read these, and re-test any host that was refused, before building (prices and models change).

Prices seen (pay as you go, list prices; ours will differ with volume and plan): Deepgram roughly $0.005 to $0.008 a minute depending on model and plan (its page shows both current and struck-through prices), billed per second; ElevenLabs v4 Turbo text to speech $0.04 per 1,000 characters (Flash/Turbo 32 languages also $0.04) and Scribe v3 Live speech to text $0.22 an hour; Gladia: the self-serve tab that appears to be real-time "starts at 0.68 EUR an hour" (the async tab starts at 0.16 EUR; confirm which tab is real-time before relying on it); AssemblyAI streaming not extracted; Yiddish Labs about 100 credits an audio minute and 4 credits a word in the example, credit price OPEN. A rough ElevenLabs speaking cost: about 900 characters is a minute of assistant speech, so about $0.036 a minute at $0.04 per 1,000. Every option's cost and latency are recorded per call by the menu, which is how these figures get checked against reality.

## 10. Runtime hosts and Azure setting names

**Runtime hosts the line will call** (outbound HTTPS and WSS, port 443; the docs hosts above are not needed at runtime):

| Host | Used by |
|---|---|
| `api.openai.com` | p1 (as today); our text model and the judge for p2 to p5 |
| `api.deepgram.com` | p2 STT (`wss`) |
| `api.elevenlabs.io` (regional variants `api.us.elevenlabs.io`, `api.eu.residency.elevenlabs.io` exist) | p2 and p3 TTS (`wss`) |
| `api.gladia.io` | p3 and p5 live STT (`https` to open a session, then `wss`) |
| `flash.yiddishlabs.com` | p4 and p5 STT (`https`) |
| `rt.yiddishlabs.com` | p4 and p5 TTS (`wss`) |

`app.yiddishlabs.com` is the dashboard only (never called by the line). The Google and Microsoft hosts are not used.

**Setting names (names only; values live in the host's secret store; environment variables use `__` for `:`).** None of these is read by any code yet; the adapter slice adds them with start-up validation like `Voice:PipelineMenu`. A provider switch left empty means the option stays "not available on this line".

| Setting | Meaning |
|---|---|
| `Voice:Pipelines:P2:Stt`, `Voice:Pipelines:P2:Tts` | provider per option and part: `deepgram` / `elevenlabs` (p2), `gladia` / `elevenlabs` (p3), `yiddishlabs` / `yiddishlabs` (p4), `gladia+yiddishlabs` / `yiddishlabs` (p5); the same two keys exist for `P3`, `P4`, `P5` |
| `Voice:Pipelines:Llm:Model` | the text model id of the chained options: empty means the Claude default; an OpenAI id only with the explicit `Voice:Pipelines:Llm:Provider=openai` override (V-40; it then reuses `Voice:OpenAiApiKey`) |
| `Voice:Pipelines:P5:JudgeModel`, `Voice:Pipelines:P5:YiddishLabsInJudge` | option 5's judge and the Terms §6 switch (default false) |
| `Voice:Deepgram:ApiKey` (**secret**), `:Model`, `:Language` | p2 |
| `Voice:ElevenLabs:ApiKey` (**secret**), `:VoiceId`, `:TtsModel`, `:Region` | p2, p3 |
| `Voice:Gladia:ApiKey` (**secret**), `:Model` (`solaria-1`), `:Language` (`yi`) | p3, p5 |
| `Voice:YiddishLabs:FlashApiKey` (**secret**, the `yl_flash_` key for both STT and TTS) | p4, p5 |
| `Voice:YiddishLabs:SttModel`, `:SttLanguage`, `:UploadSampleRate` (`8000` or `16000`) | Flash |
| `Voice:YiddishLabs:TtsVoiceId`, `:TtsHd`, `:MaxConcurrentTtsSessions` (default 5), `:ConsentLine` | TTS, the five-session gate, the approved consent text |
| `Voice:PipelineMenu:Prices:P2` to `:P5` with `SttPerMinute`, `TtsPerMillionChars`, `LlmInPerMillionTokens`, `LlmOutPerMillionTokens` | cost estimates per option (today the menu has one price set; the adapter slice makes it per option) |

Already built and unchanged: `Voice:PipelineMenu:Enabled`, `:TestNumbers`, `:MenuTimeoutSeconds`, `:EndpointMs`, `:ParallelWaitMs`, `:ParallelBudgetMs` (VOICE.md section 12).

## 11. Order of work after this note

1. This note merges (docs only).
2. Adapters behind the settings above, **off by default**, one slice per vendor, each tested against fakes only (no key exists): Deepgram and ElevenLabs (p2), Gladia (p3), Yiddish Labs (p4), the parallel option (p5); p4 and p5 wait for the approved consent line.
3. With keys in Azure, a manual listening and measuring session on the test line (VOICE.md section 9), reading the per-option cost and latency the menu records.

## 12. Option 6: SignalWire's own speech recognition (decision V-35)

Ronald asked for "SignalWire's speech recognition" as a sixth option ("Approved, both"; "you do the research"). The research below was done by the orchestrator (read 2026-11-22); this session read none of these pages itself and states them as reported. The built option is **A**; **B** and **C** were considered and not built.

**A. cXML `<Gather input="speech">`, one turn per webhook (BUILT).** Page: `https://signalwire.com/docs/compatibility-api/cxml/reference/voice/gather`. Attributes: `language` (default en-US), `hints`, `speechTimeout` (seconds or `auto`), `timeout`, `partialResultCallback`, `enhanced` (extra cost) and `speechModel` (`phone_call` is the 8 kHz model). The result is posted to the `action` URL as `SpeechResult` and `Confidence`. **No Yiddish is listed for recognition: OPEN, probably unsupported**, so option 6 is an English (or listed-language) comparison, not a Yiddish one. Price (`https://signalwire.com/pricing/voice`): speech-to-text $0.015 per 15 seconds per use; premium $0.03 per 15 seconds. The Gather works with the cXML we already use, needs no new product and no new secret.

**B. SWML `live_transcribe` (not built).** Page: `https://signalwire.com/docs/swml/methods/live_transcribe`. Deepgram (default) or Google, `lang`, `hints`, `vad`, events by webhook; $0.065 a minute per direction. It is SWML, not cXML: it does not fit our cXML `Connect`/`Stream` and keypad paths without moving the call to SWML.

**C. SWML AI agent (not built).** Pages: `https://signalwire.com/docs/swml/methods/ai`, `https://signalwire.com/docs/swml/reference/calling/ai/params`, `https://signalwire.com/docs/swml/reference/calling/ai/languages`, `https://signalwire.com/docs/platform/ai`. Hosted recognition, model and voice; tools by SWAIG webhooks; default `ai_model` gpt-4o-mini and **no Claude or Anthropic model documented**; recogniser Deepgram Nova-3 by default (falls back to Google); barge-in parameters (`enable_barge`, `transparent_barge`, `barge_min_words`, `interrupt_on_noise`); $0.16 a minute runtime, premium voices extra. It would REPLACE our assistant (our instructions, tools, off-topic rule and keypad flow), so it cannot be "with Claude" and our tools.

**D. Voices.** Page: `https://signalwire.com/docs/platform/voice/tts`. ElevenLabs and OpenAI voices are multilingual; Inworld lists Hebrew; no Yiddish-specific engine is listed. Option 6 speaks with `Say`, whose `voice` is the `Voice:SignalWireSpeech:Voice` setting.

**E. Data retention and training: NOT ESTABLISHED. OPEN owner item.** Before real callers: ask SignalWire what it keeps of the audio and the transcript of a `Gather`, and whether either is used to train anything. Until answered, option 6 stays on the test line only.

**What was built (A).** Turn flow: Gather speech (language, `speechTimeout` auto, `enhanced` and `speechModel` phone_call (the defaults since V-36), `hints` = static words then the catalog's) -> `POST .../speech` with `SpeechResult` -> the shared tool loop with our Claude assistant -> `<Gather input="speech dtmf"><Say>reply</Say></Gather>`. Keypad steps and the endings reuse `AfterStream`. See decision V-35 for the settings, failures and tests.

**Honest limits.** Latency is one webhook round trip per turn with no streaming: slower than options 1 to 5, **unmeasured**. Barge-in: the reply plays inside the Gather, so the page says speech or a key ends it; how quickly and how completely is **OPEN** until a real call is listened to. The recogniser's own price is not metered by us (the per-call cost counts the model's tokens only). Hints are the static words plus the shared catalog vocabulary (V-36), rebuilt on a timer, not looked up per turn.
