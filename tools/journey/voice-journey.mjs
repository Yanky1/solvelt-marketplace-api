// The voice journey (brief V, slice 8 / V-10): a guest phones in and orders by voice, against the REAL seller portal, the real marketplace API and PostgreSQL (the stack of the other journeys).
// What is scripted (nothing here reaches OpenAI, SignalWire or a card network):
//   - SignalWire: this script signs the webhooks like SignalWire does (X-SignalWire-Signature) and acts as the bidirectional audio stream (a WebSocket client sending "start", "mark", "stop").
//   - OpenAI Realtime: a tiny WebSocket server on Voice__OpenAiRealtimeUrl plays the model: it asks for the same tool calls a real call would (search, cart, identify, address, read-back, pay).
//   - SMS: a tiny HTTP sink on SignalWire__ApiBaseUrl catches the texts (the code, the confirmation).
//   - the keypad: the signed keypad webhook carries the SMS code, the read-back "yes" and the fake payment's "1".
// What is real: the API's voice module, its tools (catalog, cart, quote, checkout), the order, and the seller portal that must receive it (the same check as the product journey).
// This is not a browser journey: no page is involved.
import http from "node:http";
import { createHash, createHmac } from "node:crypto";
import * as L from "./lib.mjs";

const { step, ok, check, until, env, sleep } = L;
const voice = env.voice;
const run = L.uniq();
const MUG = `Voicemug ${run}`;
const FROM = "+15551230" + String(Math.floor(Math.random() * 900) + 100);   // a number nobody proved: the caller is a guest
const SID = `CA${run}`;
const PATHS = { inbound: "/api/v1/voice/signalwire/inbound", afterStream: "/api/v1/voice/signalwire/after-stream", keypad: "/api/v1/voice/signalwire/keypad" };

// ---- a minimal WebSocket server (RFC 6455: text frames, ping, close) for the scripted upstream ----------------------------------------------------------------------------
const GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
function serverFrame(text) {
  const body = Buffer.from(text, "utf8");
  const head = body.length < 126 ? Buffer.from([0x81, body.length]) : body.length < 65536 ? Buffer.from([0x81, 126, body.length >> 8, body.length & 255]) : (() => { const h = Buffer.alloc(10); h[0] = 0x81; h[1] = 127; h.writeBigUInt64BE(BigInt(body.length), 2); return h; })();
  return Buffer.concat([head, body]);
}

function upstreamServer(port) {
  const connections = [];
  const server = http.createServer((q, r) => { r.writeHead(404); r.end(); });
  server.on("upgrade", (req, socket) => {
    const accept = createHash("sha1").update(req.headers["sec-websocket-key"] + GUID).digest("base64");
    socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
    const conn = { req, received: [], closed: false, send: (o) => socket.write(serverFrame(JSON.stringify(o))) };
    connections.push(conn);
    let buf = Buffer.alloc(0), text = "";
    socket.on("data", (chunk) => {
      buf = Buffer.concat([buf, chunk]);
      for (;;) {
        if (buf.length < 2) return;
        const fin = (buf[0] & 0x80) !== 0, op = buf[0] & 15, masked = (buf[1] & 0x80) !== 0;
        let len = buf[1] & 0x7f, off = 2;
        if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); off = 4; }
        else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); off = 10; }
        if (buf.length < off + (masked ? 4 : 0) + len) return;
        const mask = masked ? buf.subarray(off, off + 4) : null; off += masked ? 4 : 0;
        const payload = Buffer.from(buf.subarray(off, off + len)); buf = buf.subarray(off + len);
        if (mask) for (let i = 0; i < payload.length; i++) payload[i] ^= mask[i % 4];
        if (op === 8) { socket.write(Buffer.from([0x88, 0])); socket.end(); conn.closed = true; return; }
        if (op === 9) { socket.write(Buffer.concat([Buffer.from([0x8a, payload.length]), payload])); continue; }
        if (op === 1 || op === 0) { text += payload.toString("utf8"); if (fin) { try { conn.received.push(JSON.parse(text)); } catch { /* not JSON: ignore */ } text = ""; } }
      }
    });
    socket.on("close", () => { conn.closed = true; });
    socket.on("error", () => { conn.closed = true; });
  });
  server.listen(port, "127.0.0.1");
  return { server, connections, current: () => connections[connections.length - 1] };
}

/** Waits for a message the API sent upstream (what the real model would receive) on the connection `conn()` returns. */
async function waitUp(conn, what, pred, timeout = 15_000) {
  return until(what, () => conn().received.find(pred) ?? null, { timeout, every: 20 });
}

// ---- the SMS sink --------------------------------------------------------------------------------------------------------------------------------------------------------------
function smsSink(port) {
  const sent = [];
  const server = http.createServer((q, r) => {
    let body = "";
    q.on("data", (c) => { body += c; });
    q.on("end", () => {
      try { const j = JSON.parse(body); sent.push({ to: j.to, from: j.from, body: j.body, auth: q.headers.authorization, path: q.url }); } catch { /* ignore */ }
      r.writeHead(200, { "content-type": "application/json" }); r.end("{}");
    });
  });
  server.listen(port, "127.0.0.1");
  return { server, sent };
}

// ---- SignalWire: signed webhooks and the audio stream ------------------------------------------------------------------------------------------------------------------------
function signature(url, form) {
  const text = url + Object.keys(form).sort().map((k) => k + form[k]).join("");
  return createHmac("sha1", voice.signingKey).update(text).digest("base64");
}
async function hook(path, extra = {}) {
  const form = { CallSid: SID, From: FROM, To: "+15559876543", ...extra };
  const res = await fetch(env.api + path, {
    method: "POST", headers: { "content-type": "application/x-www-form-urlencoded", "X-SignalWire-Signature": signature(voice.publicBase + path, form) }, body: new URLSearchParams(form),
  });
  return { status: res.status, text: await res.text() };
}
const streamUrlOf = (cxml) => /<Stream[^>]*\burl="([^"]+)"/.exec(cxml)?.[1]?.replaceAll("&amp;", "&") ?? null;
const gatherOf = (cxml) => /<Gather[\s\S]*?<Say>([\s\S]*?)<\/Say>/.exec(cxml)?.[1] ?? null;

class Phone {
  constructor(url) {
    this.got = []; this.closed = false;
    this.ws = new WebSocket(url);
    this.opened = new Promise((res, rej) => { this.ws.addEventListener("open", res); this.ws.addEventListener("error", () => rej(new L.JourneyError(`the stream at ${url.split("?")[0]} could not be opened`))); });
    this.ws.addEventListener("message", (e) => { try { this.got.push(JSON.parse(String(e.data))); } catch { /* ignore */ } });
    this.ws.addEventListener("close", () => { this.closed = true; });
  }
  async start() { await this.opened; this.send({ event: "start", start: { streamSid: "MZ1", callSid: SID }, streamSid: "MZ1" }); }
  send(o) { this.ws.send(JSON.stringify(o)); }
  wait(what, pred, timeout = 15_000) { return until(what, () => this.got.find(pred) ?? null, { timeout, every: 20 }); }
  async waitClosed() { await until("the phone stream to close", () => this.closed, { timeout: 15_000, every: 20 }); }
}

async function main() {
  const upstream = upstreamServer(voice.upstreamPort);
  const sms = smsSink(voice.smsPort);
  try {
    step("a seller creates and activates a product in the real portal");
    const seller = await L.newApprovedSeller(`Voice Shop ${run}`);
    const productId = await L.sellerProduct(seller, { title: MUG, price: 18.05, quantity: 5 });
    ok(`seller ${seller.sellerId} has product ${productId}`);
    await until("the listing in the marketplace catalog", async () => {
      await L.pumpPortal();
      const r = await L.api("GET", `/api/v1/catalog/listings?q=${encodeURIComponent(MUG.split(" ")[0] + " " + run)}`);
      return r.body?.items?.[0] ?? null;
    }, { timeout: 90_000, every: 2000 });
    ok("the listing synced");

    let phone = null;
    const tools = { n: 0 };
    /** The scripted model asks for a tool; returns what the server answered (what the real model would read out). */
    const tool = async (name, args = {}) => {
      const id = `call_${++tools.n}`;
      const up = upstream.current();
      up.send({ type: "response.done", response: { output: [{ type: "function_call", name, call_id: id, arguments: JSON.stringify(args) }] } });
      const item = await waitUp(upstream.current, `the answer to ${name}`, (m) => m.type === "conversation.item.create" && m.item?.call_id === id);
      return JSON.parse(item.item.output);
    };
    /** The call (re)attaches: a stream, a "start", and a model session the API configures. */
    const attach = async (url) => {
      const before = upstream.connections.length;
      phone?.ws.close();
      phone = new Phone(url);
      await phone.start();
      await until("the model session for this stream", () => upstream.connections.length > before && upstream.current().received.some((m) => m.type === "session.update"), { timeout: 15_000, every: 20 });
    };
    /** The model's last words before a keypad step have played: the phone acknowledges the mark, the stream ends. */
    const endStreamAfterGoodbye = async () => {
      upstream.current().send({ type: "response.done", response: { output: [] } });
      const mark = await phone.wait("the end-of-speech mark", (m) => m.event === "mark" && m.mark?.name === "end");   // the hand-off mark; audio marks (V-12) are not it
      phone.send({ event: "mark", streamSid: "MZ1", mark: { name: mark.mark.name } });
      await phone.waitClosed();
    };

    step("a signed inbound call: the platform speaks the AI disclosure, then the stream opens");
    const bad = await fetch(env.api + PATHS.inbound, { method: "POST", headers: { "content-type": "application/x-www-form-urlencoded", "X-SignalWire-Signature": "forged" }, body: new URLSearchParams({ CallSid: SID, From: FROM }) });
    check(bad.status === 403, "an unsigned or forged webhook is refused (403)");
    const inbound = await hook(PATHS.inbound);
    check(inbound.status === 200, `the signed inbound webhook is accepted (got ${inbound.status})`);
    check(/automated A I assistant/.test(inbound.text), "the AI disclosure is spoken by the platform's own <Say>");
    const url = streamUrlOf(inbound.text);
    check(url?.includes("/api/v1/voice/stream?token="), "the answer carries a stream URL with a token");
    await attach(url);
    check(upstream.connections[0].req.headers.authorization === "Bearer journey-not-a-real-openai-key", "the model key goes to the configured upstream only, as a bearer header");
    ok("the stream is open and the model session is configured");

    step("the caller searches and fills the cart");
    const found = await tool("search_products", { query: MUG });
    check(JSON.stringify(found).includes(MUG.split(" ")[0]), "the search finds the seller's product");
    const added = await tool("add_to_cart", { position: 1, option_position: 1, quantity: 1 });
    check(!added.error, `adding to the cart works (${JSON.stringify(added).slice(0, 120)})`);
    ok("one mug is in the guest's cart");

    step("the code is texted and typed on the keypad (the model never hears it)");
    const ident = await tool("identify_caller");
    check(ident.status === "code_sent", "a code is sent");
    await endStreamAfterGoodbye();
    const after = await hook(PATHS.afterStream);
    check(after.status === 200 && /<Gather/.test(after.text), "after the stream the platform asks for the code");
    const code = await until("the code text", () => sms.sent.filter((m) => m.to === FROM).map((m) => /\b(\d{6})\b/.exec(m.body)?.[1]).find(Boolean) ?? null, { timeout: 10_000, every: 50 });
    check(sms.sent[0].auth?.startsWith("Basic "), "the SMS provider is called with basic auth");
    let kp = await hook(PATHS.keypad, { Digits: "000000" });
    check(/<Gather/.test(kp.text) && !streamUrlOf(kp.text), "a wrong code asks again and does not resume the call");
    kp = await hook(PATHS.keypad, { Digits: code });
    check(streamUrlOf(kp.text), "the right code resumes the call with a fresh stream");
    await attach(streamUrlOf(kp.text));
    ok("verified as a guest by SMS code");

    step("name, email and delivery address");
    check((await tool("set_guest_name", { name: "Dana Levi" })).status === "saved", "the name is saved");
    const email = `voice-${run}@example.test`;
    check(!(await tool("set_guest_email", { email })).error, "the email is taken");
    check(!(await tool("confirm_guest_email")).error, "the email is confirmed");
    const addr = await tool("set_delivery_address", { street: "1 Main St", apartment: "4B", city: "Brooklyn", state: "NY", postal_code: "11201" });
    check(addr.status === "read_back_needed", `the address needs a read-back (${JSON.stringify(addr).slice(0, 160)})`);
    check(!(await tool("confirm_address")).error, "the address is confirmed");
    const options = await tool("get_delivery_options");
    check(!options.error && options.groups?.length >= 1, `delivery options come from the real portal's shipping settings (${JSON.stringify(options).slice(0, 200)})`);
    ok("the cheapest delivery option is selected");

    step("the SERVER reads the order back; only the keypad yes confirms it");
    const rb = await tool("read_back_order");
    check(rb.status === "reading_back", `the order is read back (${JSON.stringify(rb).slice(0, 200)})`);
    await endStreamAfterGoodbye();
    const readBack = await hook(PATHS.afterStream);
    const said = gatherOf(readBack.text) ?? "";
    check(/dollars/.test(said) && /yes/i.test(said), `the platform reads the order, its total and asks for a yes (${said.slice(0, 160)})`);
    check(!upstream.connections.some((c) => c.received.some((m) => JSON.stringify(m).includes("To place this order, say yes or press 1"))), "the model never received the read-back text");
    kp = await hook(PATHS.keypad, { Digits: "1" });
    check(streamUrlOf(kp.text), "yes (1) resumes the call");
    await attach(streamUrlOf(kp.text));
    ok("the order was confirmed by the caller on the keypad");

    step("payment on the keypad with the AI disconnected (the fake provider: no card exists)");
    const pay = await tool("pay_for_order", { method: "keypad" });
    check(pay.status === "payment_starting", `payment starts (${JSON.stringify(pay).slice(0, 160)})`);
    await endStreamAfterGoodbye();
    const payAfter = await hook(PATHS.afterStream);
    check(/Test payment/.test(payAfter.text), "the platform plays the provider's own instruction");
    kp = await hook(PATHS.keypad, { Digits: "1" });
    check(streamUrlOf(kp.text) && /Thank you\. Your payment went through/.test(kp.text), "the payment went through and the call resumes");
    await attach(streamUrlOf(kp.text));
    const confirmation = await until("the confirmation text", () => sms.sent.find((m) => m.to === FROM && /MP-\d{8}-[A-Z0-9]+/.test(m.body)) ?? null, { timeout: 10_000, every: 50 });
    const number = /MP-\d{8}-[A-Z0-9]+/.exec(confirmation.body)[0];
    check(!/Main St|11201/.test(confirmation.body), "the confirmation text holds no address");
    ok(`order ${number} placed and paid; the caller was texted`);

    step("what the model never saw");
    const everything = JSON.stringify(upstream.connections.flatMap((c) => c.received));
    for (const secret of ["11201", email, code, "\"digit\""]) check(!everything.includes(secret), `the model session never contained ${secret === code ? "the SMS code" : secret}`);
    ok("no address, no email, no code and no keypad digit went upstream");

    step("the order is in the seller portal (the same check as the product journey)");
    const sellerOrder = await until("the order in the seller portal", async () => { await L.pumpPortal(); return L.portalOrderFor(seller, number); }, { timeout: 90_000, every: 2000 });
    ok(`seller order ${sellerOrder.number ?? sellerOrder.id} received from the phone order ${number}`);

    phone.send({ event: "stop", streamSid: "MZ1" });
    await sleep(200);
    phone.ws.close();
    console.log("voice journey: OK");
  } finally {
    upstream.server.close(); sms.server.close();
  }
}

main().then(() => process.exit(0), (e) => { console.error(`voice journey FAILED: ${e?.message ?? e}`); process.exit(1); });
