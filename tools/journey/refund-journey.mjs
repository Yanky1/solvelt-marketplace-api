// The refund journey against the REAL portal, API and web (brief M8): a paid, shipped order -> the seller refunds it in the portal -> the portal's refund.issued reaches the API ->
// the API moves the money (Stripe refund, the in-process fake in CI) -> the API tells the portal refund.processed -> the portal's ledger records it -> the buyer sees the refund.
import { readFileSync } from "node:fs";
import * as L from "./lib.mjs";
import { launch, watch, go } from "./browser.mjs";

const { step, ok, check, until, portal, api, env } = L;
const run = L.uniq();
const problems = [];
let livePage = null;

async function main() {
  step("a seller, a product and a paid order");
  const seller = await L.newApprovedSeller(`Journey Refunds ${run}`);
  const title = `Journeyvase ${run}`;
  await L.sellerProduct(seller, { title, price: 40, quantity: 5 });
  const listing = await until("the listing in the catalog", async () => { await L.pumpPortal(); return (await api("GET", `/api/v1/catalog/listings?q=${encodeURIComponent(title)}`)).body?.items?.[0] ?? null; }, { timeout: 90_000, every: 2000 });
  const buyer = await L.newBuyer("refunded");
  const order = await L.apiCheckoutAndPay(buyer, listing.slug);
  ok(`order ${order.number}, ${order.totalCents} cents`);
  const sellerOrder = await until("the order in the seller portal", async () => { await L.pumpPortal(); return L.portalOrderFor(seller, order.number); }, { timeout: 60_000, every: 2000 });

  step("the seller accepts and ships (a refund covers shipped units only)");
  const detail = async () => (await L.must(portal("GET", `/api/v1/seller/orders/${sellerOrder.id}`, { token: seller.token }), 200, "seller order")).body;
  let d = await detail();
  await L.must(portal("POST", `/api/v1/seller/orders/${sellerOrder.id}/accept`, { token: seller.token, body: { version: d.version } }), 200, "accept");
  d = await detail();
  await L.must(portal("POST", `/api/v1/seller/orders/${sellerOrder.id}/ship`, { token: seller.token, body: { version: d.version, carrier: "UPS", trackingNumber: `1Z999AA1${String(Math.floor(Math.random() * 1e10)).padStart(10, "0")}` } }), 200, "ship");
  await L.pumpPortal();

  step("the seller refunds the whole order in the portal");
  d = await detail();
  const refund = await L.must(portal("POST", `/api/v1/seller/orders/${sellerOrder.id}/refunds`, { token: seller.token, body: { version: d.version, full: true, reason: "goodwill", note: "refund journey" } }), [200, 201], "refund");
  ok(`refund recorded in the portal: ${JSON.stringify(refund.body).slice(0, 160)}`);

  step("refund.issued reaches the API and the money moves (Stripe refund)");
  const refunded = await until("the order to show a refund", async () => {
    await L.pumpPortal();
    const o = (await api("GET", `/api/v1/orders/${order.number}`, { token: buyer.token })).body;
    return o?.refundedCents > 0 ? o : null;
  }, { timeout: 90_000, every: 3000 });
  check(refunded.refundedCents > 0 && refunded.refundedCents <= order.totalCents, `refunded ${refunded.refundedCents} of ${order.totalCents} cents`);
  ok(`the buyer's order shows ${refunded.refundedCents} cents refunded`);

  step("refund.processed reaches the portal's ledger");
  const entry = await until("a refund entry in the seller's ledger", async () => {
    await L.pumpPortal();
    const r = await portal("GET", "/api/v1/seller/payments/ledger", { token: seller.token });
    const rows = r.body?.items ?? r.body?.entries ?? r.body ?? [];
    return Array.isArray(rows) ? rows.find((e) => /refund/i.test(JSON.stringify(e.type ?? e.kind ?? e))) ?? null : null;
  }, { timeout: 90_000, every: 3000 });
  check(entry.amountCents < 0 && -entry.amountCents <= order.totalCents, `the ledger refund (${entry.amountCents}) is negative and within what was paid (${order.totalCents}; tax is never in the seller ledger)`);
  ok(`ledger: ${entry.type} ${entry.amountCents} cents (${entry.memo})`);

  step("the buyer sees the refund on the order page");
  const browser = await launch();
  try {
    const page = await (await browser.newContext({ baseURL: env.web })).newPage();
    watch(page, problems); livePage = page;
    await go(page, "/sign-in");
    await page.getByLabel("Email").fill(buyer.email); await page.getByLabel("Password").fill(L.BUYER_PASSWORD);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await page.waitForURL("**/account", { timeout: 15_000 });
    await go(page, `/account/orders/${order.number}`);
    const text = await page.locator("main").innerText();
    check(/refund/i.test(text), "the order page mentions the refund");
    ok("the order page shows the refund");
  } catch (e) {
    try { console.error(`page ${livePage?.url()} says: ${(await livePage.locator("main").innerText({ timeout: 3000 })).replace(/\s+/g, " ").slice(0, 600)}`); } catch { /* gone */ }
    throw e;
  } finally { await browser.close(); }

  step("no server errors on the way");
  check(problems.length === 0, `errors seen: ${problems.join("; ")}`);
  const health = (await api("GET", "/health")).body;
  check(health.outbox?.deadLettered === 0, `${health.outbox?.deadLettered} outbox messages were dead-lettered`);
  check(!/^crit:/m.test(readFileSync(`${env.runDir}/api.log`, "utf8")), "the API log holds no critical line");
  ok("clean");
}

try { await main(); console.log("JOURNEY PASSED: refund"); }
catch (e) { console.error(`JOURNEY FAILED: ${e instanceof Error ? e.message : e}`); process.exit(1); }
