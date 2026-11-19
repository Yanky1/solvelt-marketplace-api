// The quantity-offer journey against the REAL portal and API (portal C18, decision Q-01..Q-06): a seller creates two products, a free-gift product and an offer ("10+ at 95c, 20+ at 90c, a free gift from 10"),
// the offer feed syncs into the marketplace, a buyer puts 25 units in the cart (20 + 5: the cart limits a line to 20), the cart and the quote price them at 90c = 2250 with the gift at $0, the buyer pays,
// and the REAL portal ACCEPTS the order (it recomputes every discount and gift from the exact offer version and would answer 422 offer_mismatch otherwise); the gift line is on the seller's order.
import { readFileSync } from "node:fs";
import * as L from "./lib.mjs";

const { step, ok, check, until, portal, api, env } = L;
const run = L.uniq();

async function main() {
  step("a seller creates two products, a free-gift product and a quantity offer in the real portal");
  // free shipping from 23.00: the discounted subtotal (2250) is UNDER it and the list price (2500) is OVER it, so the order only passes if the buyer side compares the discounted subtotal (Q-04)
  const seller = await L.newApprovedSeller(`Journey Tiers ${run}`, { freeOverCents: 2300 });
  const titleA = `Tierpen ${run} alpha`, titleB = `Tierpen ${run} beta`, titleG = `Tiergift ${run} wallet`;
  const a = await L.sellerProduct(seller, { title: titleA, price: 1, quantity: 100 });
  const b = await L.sellerProduct(seller, { title: titleB, price: 1, quantity: 100, color: [200, 60, 60] });
  const g = await L.sellerProduct(seller, { title: titleG, price: 5, quantity: 10, color: [60, 200, 60] });
  const offer = await L.sellerOffer(seller, { name: `Journey bundle ${run}`, productIds: [a, b], tiers: [{ minQuantity: 10, unitPriceCents: 95 }, { minQuantity: 20, unitPriceCents: 90 }], gifts: [{ minQuantity: 10, productId: g, quantity: 1 }] });
  ok(`offer ${offer.id} version ${offer.version}`);

  step("the listings and the offer sync into the marketplace");
  const find = async (title) => until(`the listing ${title}`, async () => { await L.pumpPortal(); return (await api("GET", `/api/v1/catalog/listings?q=${encodeURIComponent(title)}`)).body?.items?.[0] ?? null; }, { timeout: 90_000, every: 2000 });
  const la = await find(titleA), lb = await find(titleB);
  await find(titleG);
  const detail = async (l) => (await L.must(api("GET", `/api/v1/catalog/listings/${l.slug}`), 200, "listing detail")).body;
  const da = await detail(la), db = await detail(lb);
  const buyer = await L.newBuyer("tiers");
  const add = (d, quantity) => L.must(api("POST", "/api/v1/cart/lines", { token: buyer.token, body: { variantId: d.variants[0].id ?? d.variants[0].variantId, quantity } }), 200, "add to cart");

  step("the cart prices 25 units at 90 cents once the offer has synced (every unit gets the reached tier)");
  await add(da, 20); await add(db, 5);
  const cart = await until("the cart to show the offer", async () => {
    await L.pumpPortal();
    const c = (await api("GET", "/api/v1/cart", { token: buyer.token })).body;
    return c?.subtotalCents === 2250 ? c : null;
  }, { timeout: 90_000, every: 3000 });
  check(cart.itemCount === 25, `25 units in the cart (${cart.itemCount})`);
  const group = cart.groups[0];
  check(group.discountCents === 250, `the discount is 250 cents (${group.discountCents})`);
  check(group.offers?.[0]?.tier?.unitPriceCents === 90, "the reached tier is 90 cents");
  ok("cart: 25 units = 2250 cents, discount 250");

  step("the quote carries the discount and the free gift at $0, the buyer pays");
  const quote = (await L.must(api("POST", "/api/v1/checkout/quote", { token: buyer.token, body: { address: { country: "US", region: "NY", postalCode: "11201" } } }), 200, "quote")).body;
  check(quote.itemsCents === 2250, `quote items ${quote.itemsCents}`);
  const qg = quote.groups[0];
  check(qg.shippingCents === 500, `shipping is charged (500) because the DISCOUNTED subtotal 2250 is under the free-shipping threshold 2300 (${qg.shippingCents})`);
  check(qg.gifts?.length === 1 && qg.gifts[0].quantity === 1, "the quote holds one free gift");
  check(qg.offers?.[0]?.offerId === offer.id && qg.offers[0].version === offer.version, "the quote names the offer and its version");
  const placed = (await L.must(api("POST", "/api/v1/checkout", { token: buyer.token, body: { address: L.ADDRESS, quoteHash: quote.quoteHash } }), 200, "checkout")).body;
  const intentId = placed.payment.clientSecret.split("_secret_")[0];
  const total = (await L.must(api("GET", `/api/v1/checkout/orders/${placed.orderNumber}`, { token: buyer.token }), 200, "read the order")).body.totalCents;
  const r = await L.stripeEvent("payment_intent.succeeded", { id: intentId, amount: total, amount_received: total, currency: "usd", metadata: { marketplace_order_id: placed.orderNumber } });
  check(r.status === 200, `the Stripe webhook answered ${r.status}`);

  step("the REAL portal accepts the order (no offer_mismatch) and the gift line is on the seller's order");
  const sellerOrder = await until("the order in the seller portal", async () => { await L.pumpPortal(); return L.portalOrderFor(seller, placed.orderNumber); }, { timeout: 90_000, every: 2000 });
  const so = (await L.must(portal("GET", `/api/v1/seller/orders/${sellerOrder.id}`, { token: seller.token }), 200, "seller order")).body;
  check(JSON.stringify(so).includes("Tiergift"), "the seller's order holds the free gift line");
  const mine = (await api("GET", `/api/v1/orders/${placed.orderNumber}`, { token: buyer.token })).body;
  check(mine.status !== "failed", `the buyer's order is ${mine.status}`);
  const lines = mine.groups[0].lines;
  check(lines.filter((l) => !l.isGift).reduce((n, l) => n + l.discountCents, 0) === 250, "the order lines carry the 250 cents discount");
  check(lines.some((l) => l.isGift && l.unitPriceCents === 0), "the order has the free gift line at 0");
  ok(`order ${placed.orderNumber} accepted by the portal`);

  step("no server errors on the way");
  const health = (await api("GET", "/health")).body;
  check(health.outbox?.deadLettered === 0, `${health.outbox?.deadLettered} outbox messages were dead-lettered`);
  check(!/^crit:/m.test(readFileSync(`${env.runDir}/api.log`, "utf8")), "the API log holds no critical line");
  ok("clean");
}

try { await main(); console.log("JOURNEY PASSED: tiered"); }
catch (e) { console.error(`JOURNEY FAILED: ${e instanceof Error ? e.message : e}`); process.exit(1); }
