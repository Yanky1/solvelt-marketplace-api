// The opt-in journey with REAL Stripe test-mode keys (brief M8, step 7). Run only with JOURNEY_REAL_STRIPE=1 and STRIPE_TEST_SECRET_KEY (a sk_test_ key) in the environment, and a stack started the same way.
// The checkout creates a real PaymentIntent; this script confirms it through Stripe's API with the test card pm_card_visa, then delivers the signed payment_intent.succeeded built from what Stripe
// returned (Stripe cannot reach a CI runner, so the webhook TRANSPORT is not tested here: the fake-provider journeys cover the receiver). The seller then refunds, the API refunds through real Stripe,
// and the refund is read back from Stripe. Not covered: the 3DS test card (it needs Stripe.js in a browser and a publishable key: a follow-up, decision M8-09).
import * as L from "./lib.mjs";

const { step, ok, check, until, portal, api } = L;
const KEY = process.env.STRIPE_TEST_SECRET_KEY ?? "";
const run = L.uniq();

async function stripe(method, path, form) {
  const r = await fetch(`https://api.stripe.com${path}`, { method, headers: { authorization: `Bearer ${KEY}`, ...(form ? { "content-type": "application/x-www-form-urlencoded" } : {}) }, body: form ? new URLSearchParams(form).toString() : undefined });
  const body = await r.json();
  check(r.ok, `Stripe ${method} ${path} answered ${r.status}: ${body?.error?.message ?? ""}`);
  return body;
}

async function main() {
  check(/^(sk|rk)_test_/.test(KEY), "STRIPE_TEST_SECRET_KEY is not set to a Stripe test key");
  step("a paid order with a real Stripe test-mode PaymentIntent");
  const seller = await L.newApprovedSeller(`Journey Stripe ${run}`);
  const title = `Journeystripe ${run}`;
  await L.sellerProduct(seller, { title, price: 30, quantity: 5 });
  const listing = await until("the listing in the catalog", async () => { await L.pumpPortal(); return (await api("GET", `/api/v1/catalog/listings?q=${encodeURIComponent(title)}`)).body?.items?.[0] ?? null; }, { timeout: 90_000, every: 2000 });
  const buyer = await L.newBuyer("stripe");
  const detail = (await L.must(api("GET", `/api/v1/catalog/listings/${listing.slug}`), 200, "listing")).body;
  await L.must(api("POST", "/api/v1/cart/lines", { token: buyer.token, body: { variantId: detail.variants[0].id ?? detail.variants[0].variantId, quantity: 1 } }), 200, "cart");
  const quote = (await L.must(api("POST", "/api/v1/checkout/quote", { token: buyer.token, body: { address: { country: "US", region: "NY", postalCode: "11201" } } }), 200, "quote")).body;
  const placed = (await L.must(api("POST", "/api/v1/checkout", { token: buyer.token, body: { address: L.ADDRESS, quoteHash: quote.quoteHash ?? quote.quote?.quoteHash } }), 200, "checkout")).body;
  const intentId = placed.payment.clientSecret.split("_secret_")[0];
  check(intentId.startsWith("pi_") && !intentId.startsWith("pi_fake"), `the API created a real Stripe intent (got ${intentId.slice(0, 8)}...)`);
  const confirmed = await stripe("POST", `/v1/payment_intents/${intentId}/confirm`, { payment_method: "pm_card_visa", return_url: "https://example.com/return" });
  check(confirmed.status === "succeeded", `the test card payment ended as ${confirmed.status}`);
  const r = await L.stripeEvent("payment_intent.succeeded", { id: intentId, amount: confirmed.amount, amount_received: confirmed.amount_received, currency: confirmed.currency, metadata: confirmed.metadata });
  check(r.status === 200, `the signed event answered ${r.status}`);
  ok(`order ${placed.orderNumber} paid with ${intentId}`);
  const so = await until("the order in the seller portal", async () => { await L.pumpPortal(); return L.portalOrderFor(seller, placed.orderNumber); }, { timeout: 60_000, every: 2000 });

  step("the seller ships and refunds; the refund is made at Stripe");
  const sd = async () => (await L.must(portal("GET", `/api/v1/seller/orders/${so.id}`, { token: seller.token }), 200, "seller order")).body;
  let d = await sd();
  await L.must(portal("POST", `/api/v1/seller/orders/${so.id}/accept`, { token: seller.token, body: { version: d.version } }), 200, "accept");
  d = await sd();
  await L.must(portal("POST", `/api/v1/seller/orders/${so.id}/ship`, { token: seller.token, body: { version: d.version, carrier: "UPS", trackingNumber: `1Z999AA1${String(Math.floor(Math.random() * 1e10)).padStart(10, "0")}` } }), 200, "ship");
  d = await sd();
  await L.must(portal("POST", `/api/v1/seller/orders/${so.id}/refunds`, { token: seller.token, body: { version: d.version, full: true, reason: "goodwill" } }), [200, 201], "refund");
  const refund = await until("a succeeded refund at Stripe", async () => {
    await L.pumpPortal();
    const list = await stripe("GET", `/v1/refunds?payment_intent=${intentId}`);
    return list.data.find((x) => x.status === "succeeded") ?? null;
  }, { timeout: 90_000, every: 3000 });
  check(refund.amount > 0 && refund.amount <= confirmed.amount, `the Stripe refund (${refund.amount}) is within what was paid (${confirmed.amount})`);
  ok(`Stripe refund ${refund.id}: ${refund.amount} cents`);
  const entry = await until("the refund in the portal's ledger", async () => {
    await L.pumpPortal();
    const rows = (await portal("GET", "/api/v1/seller/payments/ledger", { token: seller.token })).body?.items ?? [];
    return rows.find((e) => e.type === "refund") ?? null;
  }, { timeout: 90_000, every: 3000 });
  ok(`ledger: ${entry.type} ${entry.amountCents} cents`);
}

try { await main(); console.log("JOURNEY PASSED: stripe"); }
catch (e) { console.error(`JOURNEY FAILED: ${e instanceof Error ? e.message : e}`); process.exit(1); }
