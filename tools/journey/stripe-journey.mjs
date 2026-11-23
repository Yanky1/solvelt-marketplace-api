// The opt-in journey with REAL Stripe test-mode keys (brief M8, step 7; W3 item 5). Run only with JOURNEY_REAL_STRIPE=1 and STRIPE_TEST_SECRET_KEY (a sk_test_ key) in the environment, and a stack started
// the same way. Two parts:
//  1. An order paid through Stripe's API with the test card pm_card_visa. The webhook is Stripe's OWN event, read back from Stripe's API (/v1/events) and delivered to the API byte for byte, signed
//     with the webhook secret (a signed replay: Stripe cannot reach a CI runner). It is delivered twice: the second delivery changes nothing. Then the seller refunds, the API refunds through real
//     Stripe and the refund is read back from Stripe and the portal ledger.
//  2. With STRIPE_PUBLISHABLE_KEY (a pk_test_ key) too: the 3DS test card in a real browser, through the REAL Stripe.js on the web's checkout page. The challenge is completed, Stripe.js returns to the
//     confirmation page, Stripe's own event is replayed signed, and the order is placed with the seller. Without the publishable key this part is skipped with a notice.
// The secret key is read from the environment only, never written to a file or a log (decisions M8-09, W3-05).
import * as L from "./lib.mjs";

const { step, ok, check, until, portal, api } = L;
const KEY = process.env.STRIPE_TEST_SECRET_KEY ?? "";
const run = L.uniq();
/** Stripe's test card that always asks for 3D Secure authentication (W3-05). */
const CARD_3DS = "4000002500003155";

async function stripe(method, path, form) {
  const r = await fetch(`https://api.stripe.com${path}`, { method, headers: { authorization: `Bearer ${KEY}`, ...(form ? { "content-type": "application/x-www-form-urlencoded" } : {}) }, body: form ? new URLSearchParams(form).toString() : undefined });
  const body = await r.json();
  check(r.ok, `Stripe ${method} ${path} answered ${r.status}: ${body?.error?.message ?? ""}`);
  return body;
}

/** Stripe's own payment_intent.succeeded event for this intent, as Stripe's API lists it. */
async function realEvent(intentId, since) {
  return until(`Stripe's payment_intent.succeeded event for ${intentId.slice(0, 12)}...`, async () => {
    const list = await stripe("GET", `/v1/events?type=payment_intent.succeeded&created[gte]=${since}&limit=20`);
    return list.data.find((e) => e?.data?.object?.id === intentId) ?? null;
  }, { timeout: 60_000, every: 2000 });
}

/** Delivers the event exactly as Stripe sent it, twice (the second delivery must be a no-op), and checks both answers. */
async function deliver(event) {
  const body = JSON.stringify(event);
  const first = await L.signedReplay(body);
  check(first.status === 200, `Stripe's event, signed, answered ${first.status}`);
  const again = await L.signedReplay(body);
  check(again.status === 200, `the same event delivered again answered ${again.status}`);
  const rows = L.sql(`select count(*) from stripe_events where "Id" = '${String(event.id).replace(/[^A-Za-z0-9_]/g, "")}'`);
  check(String(rows).trim() === "1", `the event is recorded once (got ${rows})`);
}

async function paidByApi() {
  step("an order paid with a real Stripe test-mode PaymentIntent; Stripe's own event is delivered, signed, twice");
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
  const since = Math.floor(Date.now() / 1000) - 120;
  const confirmed = await stripe("POST", `/v1/payment_intents/${intentId}/confirm`, { payment_method: "pm_card_visa", return_url: "https://example.com/return" });
  check(confirmed.status === "succeeded", `the test card payment ended as ${confirmed.status}`);
  await deliver(await realEvent(intentId, since));
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
  return { seller, listing };
}

/** The first Stripe frame on the page that has a field with this name (the Payment Element keeps its fields in its own iframes). */
async function stripeField(page, name) {
  return until(`Stripe's "${name}" field`, async () => {
    for (const f of page.frames()) {
      if (!/stripe\.com/.test(f.url())) continue;
      const input = f.locator(`input[name="${name}"]`);
      if (await input.count().catch(() => 0)) return input.first();
    }
    return null;
  }, { timeout: 30_000, every: 500 });
}

async function threeDSecure({ seller, listing }) {
  step("the 3DS test card in a real browser, through the real Stripe.js on the checkout page");
  const { launch, watch, go } = await import("./browser.mjs");
  const buyer = await L.newBuyer("stripe3ds");
  const detail = (await L.must(api("GET", `/api/v1/catalog/listings/${listing.slug}`), 200, "listing")).body;
  await L.must(api("POST", "/api/v1/cart/lines", { token: buyer.token, body: { variantId: detail.variants[0].id ?? detail.variants[0].variantId, quantity: 1 } }), 200, "cart");
  const browser = await launch();
  const problems = [];
  try {
    const page = await (await browser.newContext({ baseURL: L.env.web })).newPage();
    watch(page, problems);
    await go(page, "/sign-in");
    await page.getByLabel("Email").fill(buyer.email);
    await page.getByLabel("Password").fill(L.BUYER_PASSWORD);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await page.waitForURL("**/account", { timeout: 15_000 });
    await go(page, "/checkout");
    await page.getByLabel("Full name").fill("Dana Buyer");
    await page.getByLabel("Street address").fill("1 Main St");
    await page.getByLabel("City").fill("Brooklyn");
    await page.getByLabel("State / region").fill("NY");
    await page.getByLabel("Postal code").fill("11201");
    await page.getByRole("button", { name: "Continue to delivery" }).click();
    await page.getByTestId("checkout-review").waitFor({ timeout: 20_000 });
    await page.getByRole("button", { name: /^Place order and pay/ }).click();
    await page.getByTestId("payment-step").waitFor({ timeout: 20_000 });
    const since = Math.floor(Date.now() / 1000) - 120;

    await (await stripeField(page, "number")).fill(CARD_3DS);
    await (await stripeField(page, "expiry")).fill("12 / 34");
    await (await stripeField(page, "cvc")).fill("123");
    const zip = await stripeField(page, "postalCode").catch(() => null);
    if (zip) await zip.fill("11201");
    await page.getByRole("button", { name: /^Pay / }).click();

    // Stripe's test-mode challenge: a button in a nested Stripe frame
    const complete = await until("Stripe's 3DS test challenge", async () => {
      for (const f of page.frames()) {
        if (!/stripe\.com/.test(f.url())) continue;
        const b = f.getByRole("button", { name: /^Complete/i });
        if (await b.count().catch(() => 0)) return b.first();
      }
      return null;
    }, { timeout: 45_000, every: 1000 });
    await complete.click();
    await page.waitForURL(/\/checkout\/confirmation\/MP-/, { timeout: 45_000 });
    const number = decodeURIComponent(new URL(page.url()).pathname.split("/").pop());
    const intentId = String(L.sql(`select "PaymentIntentId" from marketplace_orders where "Number" = '${number.replace(/[^A-Za-z0-9-]/g, "")}'`)).trim();
    check(intentId.startsWith("pi_"), `the order has its Stripe intent (got ${intentId.slice(0, 8)})`);
    const intent = await stripe("GET", `/v1/payment_intents/${intentId}`);
    check(intent.status === "succeeded", `the 3DS payment ended as ${intent.status}`);
    await deliver(await realEvent(intentId, since));
    await until("the 3DS order placed with the seller", async () => { await L.pumpPortal(); return L.portalOrderFor(seller, number); }, { timeout: 60_000, every: 2000 });
    check(problems.length === 0, `no server error or page error on the way: ${problems.join("; ")}`);
    ok(`3DS order ${number} paid with ${intentId} and placed with the seller`);
  } finally {
    await browser.close();
  }
}

async function main() {
  check(/^(sk|rk)_test_/.test(KEY), "STRIPE_TEST_SECRET_KEY is not set to a Stripe test key");
  const first = await paidByApi();
  if (L.env.realStripeJs) await threeDSecure(first);
  else console.log("::notice title=3DS step skipped::STRIPE_TEST_PUBLISHABLE_KEY is not set, so the 3DS test card was not run (the rest of the real-Stripe journey was).");
}

try { await main(); console.log("JOURNEY PASSED: stripe"); }
catch (e) { console.error(`JOURNEY FAILED: ${e instanceof Error ? e.message : e}`); process.exit(1); }
