// Renders one short sentence in each OpenAI Realtime voice and writes local .wav files, so the voice can be CHOSEN BY LISTENING (Voice:OpenAiVoice).
// Run it by hand with your own key; it is not part of the API and nothing it writes is ever committed (.gitignore).
//
//   cd tools/voice-samples && npm install
//   OPENAI_API_KEY=sk-...  node voice-samples.mjs                      # all ten voices
//   OPENAI_API_KEY=sk-...  VOICES=cedar,marin,echo node voice-samples.mjs
//
// Environment (the key is read from OPENAI_API_KEY only: never a file, never an argument, never printed):
//   VOICES        comma list; default: alloy,ash,ballad,coral,echo,sage,shimmer,verse,marin,cedar
//   SAMPLE_TEXT   the sentence; default: a short, typical line of the phone assistant
//   MODEL         default gpt-realtime-2.1 (the API's Voice:OpenAiModel default)
//   OUT_DIR       default ./out
//   OPENAI_REALTIME_URL  default wss://api.openai.com/v1/realtime (the self-test points it at a local script)
// Two files per voice: <voice>-hd.wav (24 kHz, how the voice sounds) and <voice>-phone.wav (8 kHz G.711-style, what a caller hears on the phone).
// It makes one short Realtime session per voice; the cost is a few cents for all ten. Nothing is stored by this tool but the files.
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import WebSocket from "ws";

export const ALL_VOICES = ["alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar"];
export const DEFAULT_TEXT = "Hi, thanks for calling Solvelt. I can help you find something, check on an order, or place a new one. What would you like to do?";

/** A 16-bit mono PCM WAV file. */
export function wav(pcm16le, rate) {
  const h = Buffer.alloc(44);
  h.write("RIFF", 0); h.writeUInt32LE(36 + pcm16le.length, 4); h.write("WAVE", 8); h.write("fmt ", 12);
  h.writeUInt32LE(16, 16); h.writeUInt16LE(1, 20); h.writeUInt16LE(1, 22); h.writeUInt32LE(rate, 24); h.writeUInt32LE(rate * 2, 28); h.writeUInt16LE(2, 32); h.writeUInt16LE(16, 34);
  h.write("data", 36); h.writeUInt32LE(pcm16le.length, 40);
  return Buffer.concat([h, pcm16le]);
}

function mulawRoundTrip(sample) {
  // G.711 mu-law companding (what a phone line does to the sound), encoded and decoded again
  const BIAS = 0x84, CLIP = 32635;
  let s = Math.max(-CLIP, Math.min(CLIP, sample)); const sign = s < 0 ? 0x80 : 0; if (s < 0) s = -s; s += BIAS;
  let exp = 7; for (let m = 0x4000; (s & m) === 0 && exp > 0; m >>= 1) exp--;
  const mant = (s >> (exp + 3)) & 0x0f, u = ~(sign | (exp << 4) | mant) & 0xff;
  const v = ~u & 0xff, e = (v >> 4) & 7, mm = v & 15; let out = (((mm << 3) + BIAS) << e) - BIAS;
  return (v & 0x80) ? -out : out;
}

/** 24 kHz PCM16 to what the phone carries: 8 kHz (average of three samples) and mu-law companded. */
export function toPhone(pcm24) {
  const n = Math.floor(pcm24.length / 2 / 3), out = Buffer.alloc(n * 2);
  for (let i = 0; i < n; i++) {
    const a = pcm24.readInt16LE(i * 6), b = pcm24.readInt16LE(i * 6 + 2), c = pcm24.readInt16LE(i * 6 + 4);
    out.writeInt16LE(mulawRoundTrip(Math.round((a + b + c) / 3)), i * 2);
  }
  return out;
}

/** One session: the model says the sentence in `voice`; resolves with the 24 kHz PCM16 audio. */
export function speak({ url, key, model, voice, text, timeoutMs = 60_000 }) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(`${url}?model=${encodeURIComponent(model)}`, { headers: { Authorization: `Bearer ${key}` } });
    const chunks = []; let done = false;
    const finish = (err, value) => { if (done) return; done = true; clearTimeout(timer); try { ws.close(); } catch { /* closed */ } err ? reject(err) : resolve(value); };
    const timer = setTimeout(() => finish(new Error(`voice ${voice}: no answer within ${timeoutMs / 1000} s`)), timeoutMs);
    ws.on("error", (e) => finish(new Error(`voice ${voice}: the connection failed (${e.message})`)));
    ws.on("open", () => {
      ws.send(JSON.stringify({
        type: "session.update",
        session: {
          type: "realtime", output_modalities: ["audio"], model,
          instructions: "You are a voice sample generator. When the user gives you a sentence, say exactly that sentence, naturally and warmly, as a friendly phone assistant would, and nothing else.",
          audio: { output: { format: { type: "audio/pcm", rate: 24000 }, voice } },
        },
      }));
      ws.send(JSON.stringify({ type: "conversation.item.create", item: { type: "message", role: "user", content: [{ type: "input_text", text } ] } }));
      ws.send(JSON.stringify({ type: "response.create" }));
    });
    ws.on("message", (data) => {
      let m; try { m = JSON.parse(String(data)); } catch { return; }
      if (m.type === "response.output_audio.delta" || m.type === "response.audio.delta") chunks.push(Buffer.from(m.delta, "base64"));
      else if (m.type === "error") finish(new Error(`voice ${voice}: OpenAI said ${m.error?.code ?? "error"}: ${m.error?.message ?? ""}`));
      else if (m.type === "response.done") chunks.length ? finish(null, Buffer.concat(chunks)) : finish(new Error(`voice ${voice}: the response held no audio`));
    });
  });
}

export async function main(env = process.env) {
  const key = env.OPENAI_API_KEY;
  if (!key) { console.error("Set OPENAI_API_KEY in the environment (it is read from there only)."); return 2; }
  const voices = (env.VOICES ?? ALL_VOICES.join(",")).split(",").map((v) => v.trim()).filter(Boolean);
  const bad = voices.filter((v) => !ALL_VOICES.includes(v));
  if (bad.length) { console.error(`Unknown voice(s): ${bad.join(", ")}. Known: ${ALL_VOICES.join(", ")}`); return 2; }
  const out = env.OUT_DIR ?? "out"; mkdirSync(out, { recursive: true });
  const url = env.OPENAI_REALTIME_URL ?? "wss://api.openai.com/v1/realtime", model = env.MODEL ?? "gpt-realtime-2.1", text = env.SAMPLE_TEXT ?? DEFAULT_TEXT;
  let failed = 0;
  for (const voice of voices) {
    try {
      const pcm = await speak({ url, key, model, voice, text });
      writeFileSync(join(out, `${voice}-hd.wav`), wav(pcm, 24000));
      writeFileSync(join(out, `${voice}-phone.wav`), wav(toPhone(pcm), 8000));
      console.log(`${voice}: ${(pcm.length / 2 / 24000).toFixed(1)} s -> ${join(out, voice)}-hd.wav and -phone.wav`);
    } catch (e) { failed++; console.error(String(e.message).replaceAll(key, "***")); }
  }
  console.log(failed ? `${failed} voice(s) failed.` : `Done: listen to the files in ${out}/ and set Voice:OpenAiVoice to the one you like.`);
  return failed ? 1 : 0;
}

if (import.meta.url === `file://${process.argv[1]}`) process.exit(await main());
