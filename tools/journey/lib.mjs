// Shared helpers of the buyer journey (brief M8). Plain Node 22, no dependencies: fetch, crypto, zlib.
// Everything talks to the REAL services started by stack.sh (portal, API, web); there is no mock anywhere in this folder.
import { createHmac, randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";
import { deflateSync, crc32 } from "node:zlib";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
export const env = JSON.parse(readFileSync(process.env.JOURNEY_ENV ?? join(HERE, "..", "..", ".journey", "env.json"), "utf8"));

export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
export const uniq = () => randomBytes(4).toString("hex");

let stepName = "start";
export const step = (name) => { stepName = name; console.log(`-- ${name}`); };
export const ok = (msg) => console.log(`   ok  ${msg}`);
export class JourneyError extends Error {}
export function check(cond, what) { if (!cond) throw new JourneyError(`${what} (at step "${stepName}")`); }
export function currentStep() { return stepName; }

/** One JSON call. Returns { status, body, headers }. Never throws on a status; callers assert. */
export async function call(base, method, path, { body, token, headers = {}, raw } = {}) {
  const init = { method, headers: { ...headers } };
  if (token) init.headers.authorization = `Bearer ${token}`;
  if (body !== undefined) { init.headers["content-type"] ??= "application/json"; init.body = raw ? body : JSON.stringify(body); }
  const res = await fetch(base + path, init);
  const text = await res.text();
  let parsed = text;
  try { parsed = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  return { status: res.status, body: parsed, headers: res.headers };
}
export async function must(promise, status, what) {
  const r = await promise;
  check(Array.isArray(status) ? status.includes(r.status) : r.status === status, `${what}: expected ${status}, got ${r.status} ${typeof r.body === "string" ? r.body.slice(0, 200) : JSON.stringify(r.body).slice(0, 300)}`);
  return r;
}
export const portal = (m, p, o) => call(env.portal, m, p, o);
export const api = (m, p, o) => call(env.api, m, p, o);

/** Polls until fn returns a truthy value; on timeout fails with `what` and the last value. */
export async function until(what, fn, { timeout = 30_000, every = 500 } = {}) {
  const end = Date.now() + timeout; let last;
  for (;;) {
    last = await fn();
    if (last) return last;
    if (Date.now() > end) throw new JourneyError(`timed out waiting for ${what} (at step "${stepName}")`);
    await sleep(every);
  }
}

// ---- a real PNG (valid chunk CRCs; the marketplace resizes the image, so a synthetic one would not do) -----------------------------------------------------------------------
export function png(w = 1200, h = 1200, [r, g, b] = [200, 120, 60]) {
  const row = Buffer.alloc(1 + w * 3);
  const raw = Buffer.alloc(h * row.length);
  for (let y = 0; y < h; y++) {
    const o = y * row.length; raw[o] = 0;
    for (let x = 0; x < w; x++) { const p = o + 1 + x * 3; const t = (x + y) / (w + h); raw[p] = Math.min(255, r + 60 * t); raw[p + 1] = Math.min(255, g + 80 * t); raw[p + 2] = Math.min(255, b + 100 * t); }
  }
  const chunk = (type, data) => { const len = Buffer.alloc(4); len.writeUInt32BE(data.length); const td = Buffer.concat([Buffer.from(type, "ascii"), data]); const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(td) >>> 0); return Buffer.concat([len, td, crc]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 2;
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", ihdr), chunk("IDAT", deflateSync(raw)), chunk("IEND", Buffer.alloc(0))]);
}

// ---- seller portal (real, Testing mode) -----------------------------------------------------------------------------------------------------------------------------------------
export const SELLER_PASSWORD = "Tr1cky-Horse-Battery!";

/** Pumps the portal's outbox (Testing mode runs no background worker) and the catalog version job, so a change becomes a webhook / a feed item now. */
export async function pumpPortal() {
  await portal("POST", "/api/dev/catalog/refresh");
  for (let i = 0; i < 3; i++) await portal("POST", "/api/dev/outbox/dispatch");
}

export async function sellerLogin(email, password = SELLER_PASSWORD) {
  const r = await must(portal("POST", "/api/v1/auth/login", { body: { email, password } }), 200, "seller login");
  return r.body.accessToken;
}

/** Signs a seller up (mail code read from the portal's Testing mail sink), fills the application, has the seeded staff admin approve it. Returns { email, token, sellerId, brandId }. */
export async function newApprovedSeller(label = "Journey Seller") {
  const email = `seller-${uniq()}@example.com`;
  await must(portal("POST", "/api/v1/auth/signup", { body: { email, password: SELLER_PASSWORD, acceptTerms: true } }), 202, "seller sign-up");
  const mails = await until("the portal's verification mail", async () => { const m = (await portal("GET", `/api/test/emails?to=${encodeURIComponent(email)}&kind=verify-code`)).body; return Array.isArray(m) && m.length ? m : null; });
  const code = /\b(\d{6})\b/.exec(mails.at(-1).text)?.[1];
  check(code, "the portal's verification mail has no code");
  const v = await must(portal("POST", "/api/v1/auth/verify-email", { body: { email, code } }), 200, "seller verify");
  const token = v.body.accessToken, sellerId = v.body.sellerId;
  await must(portal("PUT", "/api/v1/seller/application/business", { token, body: { vendorName: label, contactName: "Dana Seller", contactPhone: "+1 555 123 4567", addressLine1: "1 Main St", city: "Brooklyn", region: "NY", postalCode: "11201", country: "US", smsOptIn: false } }), [200, 204], "application: business");
  await must(portal("PUT", "/api/v1/seller/application/kosher", { token, body: { answers: { sells_food: "no" } } }), [200, 204], "application: answers");
  await must(portal("POST", "/api/v1/seller/application/submit", { token, body: { acceptSellerAgreement: true } }), 200, "application: submit");
  const admin = await must(portal("POST", "/api/v1/auth/login", { body: { email: env.adminEmail, password: env.adminPassword } }), 200, "staff login");
  await must(portal("POST", `/api/v1/admin/sellers/${sellerId}/status`, { token: admin.body.accessToken, body: { status: "Approved" } }), 200, "approve seller");
  const brands = await must(portal("GET", "/api/v1/brands", { token }), 200, "brands");
  const brandId = brands.body[0].id;
  // a default shipping profile: flat 5.00 standard shipping inside the US (without one the checkout says "this seller can't deliver to that address")
  await must(portal("POST", "/api/v1/seller/shipping/profiles", { token, body: { name: "Default", currency: "USD", isDefault: true, zones: [{ name: "US", kind: "domestic", countries: ["US"], speeds: [{ speed: "standard", enabled: true, method: "flat", flatCents: 500 }] }] } }), [200, 201], "shipping profile");
  return { email, token, sellerId, brandId };
}

/** Creates, fills, photographs and ACTIVATES a product as the seller (portal API). Returns the product id. */
export async function sellerProduct(seller, { title, price = 24.5, quantity = 10, sku, categorySlug = "kitchen-dining", color } = {}) {
  const cats = await must(portal("GET", "/api/v1/categories", { token: seller.token }), 200, "categories");
  const category = cats.body.find((c) => c.slug === categorySlug) ?? cats.body[0];
  const created = await must(portal("POST", "/api/v1/seller/products", { token: seller.token, body: { title, categoryId: category.id, brandId: seller.brandId } }), 201, "create product");
  const id = created.body.id;
  const form = new FormData();
  form.append("file", new Blob([png(1200, 1200, color)], { type: "application/octet-stream" }), "photo.png");
  const up = await fetch(`${env.portal}/api/v1/seller/products/${id}/images?alt=${encodeURIComponent(title)}`, { method: "POST", headers: { authorization: `Bearer ${seller.token}` }, body: form });
  check(up.status === 201, `photo upload answered ${up.status}`);
  const cur = (await must(portal("GET", `/api/v1/seller/products/${id}`, { token: seller.token }), 200, "read product")).body;
  Object.assign(cur, { price, quantity, sku: sku ?? `SKU-${uniq().toUpperCase()}`, noBarcode: true, description: `${title}. A journey test product.` });
  await must(portal("PUT", `/api/v1/seller/products/${id}`, { token: seller.token, body: cur }), 200, "fill product");
  await must(portal("POST", `/api/v1/seller/products/${id}/status`, { token: seller.token, body: { status: "active" } }), 200, "activate product");
  return id;
}

// ---- marketplace API ---------------------------------------------------------------------------------------------------------------------------------------------------------------
export const BUYER_PASSWORD = "Correct-horse-battery-1";

/** Reads the newest mail to `to` from the mail sandbox that matches, or null. */
export async function mailTo(to, subjectRe) {
  const all = await (await fetch(`${env.mail}/messages`)).json();
  return all.filter((m) => m.to.some((a) => a.toLowerCase() === to.toLowerCase()) && (!subjectRe || subjectRe.test(m.subject))).at(-1) ?? null;
}
export function decodeMail(raw) {
  // quoted-printable or plain; enough to find a link
  const body = raw.split("\r\n\r\n").slice(1).join("\r\n\r\n");
  return body.replace(/=\r\n/g, "").replace(/=([0-9A-F]{2})/g, (_, h) => String.fromCharCode(parseInt(h, 16)));
}

/** A Stripe webhook exactly as Stripe signs it, with our (test) endpoint secret. */
export async function stripeEvent(type, object) {
  const body = JSON.stringify({ id: `evt_journey_${uniq()}${uniq()}`, type, created: Math.floor(Date.now() / 1000), data: { object } });
  const t = Math.floor(Date.now() / 1000);
  const sig = createHmac("sha256", env.stripeWebhookSecret).update(`${t}.${body}`).digest("hex");
  return call(env.api, "POST", "/api/stripe/webhooks", { body, raw: true, headers: { "stripe-signature": `t=${t},v1=${sig}` } });
}

/** Seller-portal view of the buyer's marketplace order (by marketplace id the portal stored), or null while it is not there. */
export async function portalOrderFor(seller, marketplaceOrderNumber) {
  const list = await portal("GET", `/api/v1/seller/orders?q=${encodeURIComponent(marketplaceOrderNumber)}`, { token: seller.token });
  if (list.status !== 200) return null;
  const items = list.body.items ?? list.body.orders ?? list.body;
  const hit = Array.isArray(items) ? items.find((o) => JSON.stringify(o).includes(marketplaceOrderNumber)) : null;
  return hit ?? null;
}

// ---- the database (read-only look-ups the API does not expose: our own ids behind a Stripe metadata field) --------------------------------------------------------------------
import { execFileSync } from "node:child_process";
export function sql(query) {
  const pg = env.pg;
  return execFileSync("psql", ["-h", pg.host, "-p", String(pg.port), "-U", pg.user, "-d", pg.db, "-At", "-c", query], { env: { ...process.env, PGPASSWORD: pg.password }, encoding: "utf8" }).trim();
}

// ---- a seller SERVICE (portal) ------------------------------------------------------------------------------------------------------------------------------------------------------
/** Makes the seller's owner a provider (Mon-Fri 9-17, New York), creates a service with one add-on and activates it. Returns the product (listing) id. */
export async function sellerService(seller, { title, price = 60 } = {}) {
  const t = seller.token;
  const members = await must(portal("GET", "/api/v1/seller/providers", { token: t }), 200, "providers");
  const list = members.body.items ?? members.body.providers ?? members.body;
  const memberId = (list.find((m) => m.role === "Owner") ?? list[0]).id ?? list[0].membershipId;
  await must(portal("PUT", `/api/v1/seller/providers/${memberId}`, { token: t, body: { isProvider: true, timeZone: "America/New_York" } }), 200, "make provider");
  await must(portal("PUT", `/api/v1/seller/providers/${memberId}/availability`, { token: t, body: { weekly: [1, 2, 3, 4, 5].map((day) => ({ day, intervals: [{ start: "09:00", end: "17:00" }] })), overrides: [] } }), 200, "provider hours");
  const cats = (await must(portal("GET", "/api/v1/categories", { token: t }), 200, "categories")).body;
  const category = cats.find((c) => c.slug === "home-services") ?? cats[0];
  const created = await must(portal("POST", "/api/v1/seller/products", { token: t, body: { title, brandId: seller.brandId, categoryId: category.id, type: "service" } }), 201, "create service");
  const id = created.body.id;
  const form = new FormData();
  form.append("file", new Blob([png(1200, 1200, [40, 150, 120])], { type: "application/octet-stream" }), "photo.png");
  check((await fetch(`${env.portal}/api/v1/seller/products/${id}/images?alt=${encodeURIComponent(title)}`, { method: "POST", headers: { authorization: `Bearer ${t}` }, body: form })).status === 201, "service photo upload");
  const cur = (await must(portal("GET", `/api/v1/seller/products/${id}`, { token: t }), 200, "read service")).body;
  const details = {
    version: cur.version, pricingModel: "hourly", price, minMinutes: 60, maxMinutes: 480, incrementMinutes: 30, defaultDurationMinutes: 120, bufferBeforeMinutes: 0, bufferAfterMinutes: 0,
    minNoticeHours: 1, maxAdvanceDays: 120, slotGranularityMinutes: 60, online: true, onlineInstructions: "https://meet.example.test/journey", atCustomer: true,
    serviceArea: { postalCodes: ["11201", "112*"] }, travelFee: 10, travelBufferMinutes: 30, atSeller: false, bookingMode: "instant", approvalHours: 24,
    intake: [{ id: "pets", label: "Any pets?", type: "yesno", required: false }], requirements: "Clear the area first.",
    addOns: [{ id: "deep", name: "Deep clean", price: 25, extraMinutes: 30 }], cancelFreeHours: 24, cancelFeePercent: 50, maxReschedules: 2, noShowFeePercent: 100, assignmentRule: "buyerChoice",
  };
  await must(portal("PUT", `/api/v1/seller/services/${id}/details`, { token: t, body: details }), 200, "service details");
  await must(portal("PUT", `/api/v1/seller/services/${id}/providers`, { token: t, body: { membershipIds: [memberId] } }), 200, "service providers");
  await must(portal("POST", `/api/v1/seller/products/${id}/status`, { token: t, body: { status: "active" } }), 200, "activate service");
  return id;
}

/** A verified buyer without the mail round trip (the product journey covers that): sign-up + the test-mode verify. Returns { email, token }. */
export async function newBuyer(tag = "buyer") {
  const email = `${tag}-${uniq()}@example.test`;
  await must(api("POST", "/api/v1/auth/sign-up", { body: { email, password: BUYER_PASSWORD, displayName: "Journey Buyer" } }), 204, "buyer sign-up");
  await must(api("POST", "/api/v1/demo/verify", { body: { email, password: BUYER_PASSWORD } }), 204, "buyer verify (test mode)");
  const s = await must(api("POST", "/api/v1/auth/sign-in", { body: { email, password: BUYER_PASSWORD } }), 200, "buyer sign-in");
  return { email, token: s.body.token };
}
