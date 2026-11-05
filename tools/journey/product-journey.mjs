// The buyer journey for a product, against the REAL portal, API and web (brief M8):
//   seller (portal API) creates + activates a product -> the catalog syncs -> a buyer browses and searches -> adds to the cart as a GUEST -> signs up (mail from the sandbox) and
//   signs in (the guest cart must survive) -> checks out -> pays -> the order appears in the seller portal -> the seller accepts and ships with tracking -> the buyer sees the
//   tracking -> the order is delivered -> the buyer sees it delivered -> the buyer reviews the product (a verified purchase) and the review shows on the product page.
import { readFileSync } from "node:fs";
import * as L from "./lib.mjs";
import { launch, watch, hydrated, go } from "./browser.mjs";
import { exposePay } from "./pay.mjs";

const { step, ok, check, until, portal, api, env } = L;
const run = L.uniq();
const MUG = `Journeymug ${run}`;
const problems = [];
let livePage = null;

/** What the buyer was looking at when it failed, so a failure in CI can be read without a rerun. */
async function dumpPage() {
  if (!livePage) return;
  if (problems.console?.length) console.error(`browser console errors: ${problems.console.join(" | ")}`);
  try { console.error(`page ${livePage.url()} says: ${(await livePage.locator("main").innerText({ timeout: 3000 })).replace(/\s+/g, " ").slice(0, 700)}`); await livePage.screenshot({ path: `${env.runDir}/failure.png` }); } catch { /* the page is gone */ }
}

async function main() {
  step("a seller creates and activates products in the real portal");
  const seller = await L.newApprovedSeller(`Journey Shop ${run}`);
  const productId = await L.sellerProduct(seller, { title: MUG, price: 24.5, quantity: 5 });
  await L.sellerProduct(seller, { title: `Journeyplate ${run}`, price: 12, quantity: 5, color: [60, 120, 200] });
  ok(`seller ${seller.sellerId} has product ${productId}`);

  step("the catalog syncs into the marketplace");
  const listing = await until("the listing in the marketplace catalog", async () => {
    await L.pumpPortal();
    const r = await api("GET", `/api/v1/catalog/listings?q=${encodeURIComponent(MUG.split(" ")[0] + " " + run)}`);
    return r.body?.items?.[0] ?? null;
  }, { timeout: 90_000, every: 2000 });
  check(listing.title === MUG, `the synced listing has the title ${MUG}`);
  check(listing.priceFromCents === 2450, "the synced price is 2450 cents");
  ok(`listing ${listing.slug} synced at ${listing.priceFromCents} cents`);

  const browser = await launch();
  try {
    const context = await browser.newContext({ baseURL: env.web });
    const page = await context.newPage();
    watch(page, problems); livePage = page;
    // the stand-in Stripe.js asks the runner to "pay": deliver Stripe's signed webhook for the order named in the return URL (never trusting the page for the amount)
    let buyerToken = null;
    await exposePay(page, () => buyerToken);

    step("the buyer browses and searches");
    await go(page, "/");
    await go(page, `/search?q=${encodeURIComponent(`${MUG.split(" ")[0]} ${run}`)}`);
    await hydrated(page);
    const card = page.getByRole("link", { name: new RegExp(MUG) }).first();
    await card.waitFor({ timeout: 15_000 });
    await card.click();
    await page.waitForURL(/\/p\//);
    await page.getByRole("heading", { name: MUG }).waitFor();
    ok("search finds the product and its page opens");

    step("a guest adds it to the cart (the cart token header must match web and API)");
    await hydrated(page);
    await page.getByRole("button", { name: new RegExp(`^Add ${MUG}, .*, to cart$`) }).click();
    await page.waitForURL(/\/cart\?notice=added$/);
    await go(page, "/cart");
    await page.getByText(MUG).first().waitFor({ timeout: 10_000 });
    check((await page.getByTestId("cart-count").innerText()).trim() === "1", "the header cart count is 1 for the guest after a reload");
    ok("the guest cart survives a reload");

    step("the buyer signs up, verifies by the mailed link and signs in (the guest cart is merged)");
    const email = `buyer-${run}@example.test`;
    await go(page, "/sign-up");
    await page.getByLabel("Name").fill("Journey Buyer");
    await page.getByLabel("Email").fill(email);
    await page.getByLabel("Password").fill(L.BUYER_PASSWORD);
    await page.getByRole("button", { name: "Create account" }).click();
    await page.waitForURL(/\/verify-email\?sent=1$/);
    const mail = await until("the verification mail in the sandbox", () => L.mailTo(email, /verify|confirm/i));
    const link = /https?:\/\/[^\s"<>]*\/verify-email\?token=[^\s"<>]+/.exec(L.decodeMail(mail.raw))?.[0];
    check(link, "the verification mail contains a /verify-email link");
    await go(page, new URL(link).pathname + new URL(link).search);
    await page.getByLabel("Password").fill(L.BUYER_PASSWORD);
    await page.getByRole("button", { name: "Verify email" }).click();
    await page.getByRole("heading", { name: "Email verified" }).waitFor();
    await go(page, "/sign-in");
    await page.getByLabel("Email").fill(email); await page.getByLabel("Password").fill(L.BUYER_PASSWORD);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await page.waitForURL("**/account", { timeout: 15_000 });
    ok("signed in after the first visit (no error after sign-in)");
    await go(page, "/cart");
    await page.getByText(MUG).first().waitFor({ timeout: 10_000 });
    ok("the guest cart was merged into the account");
    buyerToken = (await L.must(api("POST", "/api/v1/auth/sign-in", { body: { email, password: L.BUYER_PASSWORD } }), 200, "sign in through the API")).body.token;

    step("checkout and payment");
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
    await page.getByRole("button", { name: /^Pay / }).click();
    await page.waitForURL(/\/checkout\/confirmation\/MP-/, { timeout: 20_000 });
    const number = decodeURIComponent(new URL(page.url()).pathname.split("/").pop());
    ok(`order ${number} paid`);
    await until("the order to be placed with the seller", async () => (await page.getByTestId("order-status").getAttribute("data-status")) === "placed" || (await page.reload({ waitUntil: "domcontentloaded" }), false), { timeout: 60_000, every: 2000 });
    ok("the confirmation page says placed");

    step("the order is in the seller portal");
    const sellerOrder = await until("the order in the seller portal", async () => { await L.pumpPortal(); return L.portalOrderFor(seller, number); }, { timeout: 60_000, every: 2000 });
    ok(`seller order ${sellerOrder.number ?? sellerOrder.id}`);

    step("the seller accepts and ships with tracking; the buyer sees it");
    const detail = async () => (await L.must(portal("GET", `/api/v1/seller/orders/${sellerOrder.id}`, { token: seller.token }), 200, "seller order detail")).body;
    let d = await detail();
    await L.must(portal("POST", `/api/v1/seller/orders/${sellerOrder.id}/accept`, { token: seller.token, body: { version: d.version } }), 200, "accept");
    d = await detail();
    const tracking = `1Z999AA1${String(Math.floor(Math.random() * 1e10)).padStart(10, "0")}`;   // a valid-looking UPS number (the portal checks the format)
    await L.must(portal("POST", `/api/v1/seller/orders/${sellerOrder.id}/ship`, { token: seller.token, body: { version: d.version, carrier: "UPS", trackingNumber: tracking } }), 200, "ship");
    await L.pumpPortal();
    await go(page, `/account/orders/${number}`);
    await until("the tracking number on the buyer's order page", async () => { await L.pumpPortal(); await page.reload({ waitUntil: "domcontentloaded" }); return (await page.locator("main").innerText()).includes(tracking); }, { timeout: 60_000, every: 3000 });
    ok(`the buyer sees tracking ${tracking}`);

    step("delivered");
    d = await detail();
    await L.must(portal("POST", `/api/v1/seller/orders/${sellerOrder.id}/deliver`, { token: seller.token, body: { version: d.version } }), 200, "deliver");
    await until("the delivered status on the buyer's order page", async () => { await L.pumpPortal(); await page.reload({ waitUntil: "domcontentloaded" }); return /delivered/i.test(await page.locator("main").innerText()); }, { timeout: 60_000, every: 3000 });
    ok("the buyer sees the order delivered");

    step("the buyer reviews the product they received (a verified purchase)");
    await go(page, `/p/${listing.slug}`);
    const write = page.getByRole("link", { name: /Write a review:/ });
    await until("the write-a-review link for the delivered order", async () => { await L.pumpPortal(); if (await write.count()) return true; await page.reload({ waitUntil: "domcontentloaded" }); return false; }, { timeout: 60_000, every: 2000 });
    await write.first().click();
    await hydrated(page);
    await page.getByLabel("5 stars").check();
    const reviewText = `Sturdy, nicely finished and it arrived on time ${run}.`;
    await page.getByLabel("Your review").fill(reviewText);
    await page.getByRole("button", { name: "Publish review" }).click();
    await page.getByText("Your review is published").waitFor({ timeout: 20_000 });
    await go(page, `/p/${listing.slug}`);
    const mine = page.getByTestId("review").filter({ hasText: reviewText });
    await mine.waitFor({ timeout: 15_000 });
    check(/Verified purchase/.test(await mine.innerText()), "the review is marked as a verified purchase");
    check((await page.getByTestId("rating-summary").innerText()).includes("5"), "the rating summary on the product page shows the 5-star review");
    ok("the review is published on the product page as a verified purchase");
  } catch (e) { await dumpPage(); throw e; } finally { await browser.close(); }

  step("no server errors on the way");
  check(problems.length === 0, `errors seen: ${problems.join("; ")}`);
  const health = (await api("GET", "/health")).body;
  check(health.forwarding?.invalid === 0, `the web server's forwarded client address was refused ${health.forwarding?.invalid} times (key or clock mismatch)`);
  check(health.outbox?.deadLettered === 0, `${health.outbox?.deadLettered} outbox messages were dead-lettered`);
  // a database made by an older build ("error after sign-in" on a test site): the API says so once, critically, at start-up and then answers 500 where a column is missing
  const log = readFileSync(`${env.runDir}/api.log`, "utf8");
  check(!/does not match the model/i.test(log), "the API logged that its database schema does not match the model (a stale database)");
  check(!/^crit:/m.test(log), `the API logged a critical line: ${/^crit:.*\n.*$/m.exec(log)?.[0]}`);
  ok("no 5xx, no script errors, no refused forwarding headers, nothing dead-lettered, no schema or critical line in the API log");
}

try { await main(); console.log("JOURNEY PASSED: product"); }
catch (e) {
  console.error(`JOURNEY FAILED: ${e instanceof Error ? e.message : e}`);
  if (problems.length) console.error(`errors seen: ${problems.join("; ")}`);
  process.exit(1);
}
