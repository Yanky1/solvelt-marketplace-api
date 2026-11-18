// Offline self-test: the tool against a local scripted "OpenAI" (no key, no network). Checks the session it sends, the WAV files it writes and that the key never leaks.
import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { WebSocketServer } from "ws";
import { main, toPhone, wav } from "./voice-samples.mjs";

const received = []; let auth = null;
const server = new WebSocketServer({ port: 0, host: "127.0.0.1" });
server.on("connection", (ws, req) => {
  auth = req.headers.authorization;
  ws.on("message", (d) => {
    const m = JSON.parse(String(d)); received.push(m);
    if (m.type === "response.create") {
      const tone = Buffer.alloc(24000 * 2 / 5);   // 0.2 s of a 440 Hz tone at 24 kHz
      for (let i = 0; i < tone.length / 2; i++) tone.writeInt16LE(Math.round(8000 * Math.sin((2 * Math.PI * 440 * i) / 24000)), i * 2);
      ws.send(JSON.stringify({ type: "response.output_audio.delta", delta: tone.subarray(0, tone.length / 2).toString("base64") }));
      ws.send(JSON.stringify({ type: "response.output_audio.delta", delta: tone.subarray(tone.length / 2).toString("base64") }));
      ws.send(JSON.stringify({ type: "response.done", response: {} }));
    }
  });
});
await new Promise((r) => server.on("listening", r));
const out = mkdtempSync(join(tmpdir(), "voice-samples-"));
const SECRET = "sk-selftest-not-a-real-key";
const logs = []; const origLog = console.log, origErr = console.error; console.log = (...a) => logs.push(a.join(" ")); console.error = (...a) => logs.push(a.join(" "));
const code = await main({ OPENAI_API_KEY: SECRET, VOICES: "cedar,marin", OUT_DIR: out, OPENAI_REALTIME_URL: `ws://127.0.0.1:${server.address().port}/v1/realtime`, SAMPLE_TEXT: "Hello there." });
console.log = origLog; console.error = origErr;
assert.equal(code, 0, logs.join("\n"));
assert.equal(auth, `Bearer ${SECRET}`);
assert.deepEqual(readdirSync(out).sort(), ["cedar-hd.wav", "cedar-phone.wav", "marin-hd.wav", "marin-phone.wav"]);
const session = received.find((m) => m.type === "session.update").session;
assert.equal(session.type, "realtime"); assert.equal(session.audio.output.voice, "cedar"); assert.deepEqual(session.audio.output.format, { type: "audio/pcm", rate: 24000 });
assert.equal(received.find((m) => m.type === "conversation.item.create").item.content[0].text, "Hello there.");
const hd = readFileSync(join(out, "cedar-hd.wav")), phone = readFileSync(join(out, "cedar-phone.wav"));
for (const [f, rate, bytes] of [[hd, 24000, 24000 * 2 / 5], [phone, 8000, 8000 * 2 / 5]]) {
  assert.equal(f.toString("ascii", 0, 4), "RIFF"); assert.equal(f.toString("ascii", 8, 12), "WAVE"); assert.equal(f.readUInt32LE(24), rate); assert.equal(f.readUInt16LE(22), 1); assert.equal(f.readUInt16LE(34), 16);
  assert.equal(f.readUInt32LE(40), bytes); assert.equal(f.length, 44 + bytes);
}
assert.ok(!logs.join("\n").includes(SECRET), "the key is never printed");
assert.equal(toPhone(Buffer.alloc(12)).length, 4);   // 6 samples at 24 kHz -> 2 samples at 8 kHz
assert.equal(wav(Buffer.alloc(0), 8000).length, 44);
const missing = await main({}); assert.equal(missing, 2);
rmSync(out, { recursive: true }); server.close();
console.log("voice-samples selftest: OK");
process.exit(0);
