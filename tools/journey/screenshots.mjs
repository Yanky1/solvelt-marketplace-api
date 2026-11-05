// Screenshots of every buyer page against the REAL stack (brief M8): desktop + mobile, light + dark, written to <web repo>/docs/screenshots/journey/<page>-<desktop|mobile>-<light|dark>.png,
// plus an axe (WCAG 2.2 AA) check of every page in every combination. Regenerate: tools/journey/stack.sh up && node tools/journey/screenshots.mjs   (or tools/journey/run.sh screenshots).
// The data is made through the real portal and API first: a seller with a product and a service, a delivered order, a refund, a confirmed booking, a cart with an item.
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { createRequire } from "node:module";
import * as L from "./lib.mjs";
import { launch, devices, go } from "./browser.mjs";

const require = createRequire(join(L.env.webDir, "package.json"));
const AxeBuilder = require("@axe-core/playwright").default ?? require("@axe-core/playwright");
const { step, ok, until, portal, api, env } = L;
const OUT = join(env.webDir, "docs", "screenshots", "journey");
const run = L.uniq();

async function build() {
  step("data: a seller with a product and a service, a delivered and refunded order, a booking");
  const seller = await L.newApprovedSeller(`Shots Shop ${run}`);
  const productTitle = `Shotsvase ${run}`, serviceTitle = `Shotsclean ${run}`;
  await L.sellerProduct(seller, { title: productTitle, price: 40, quantity: 20 });
  await L.sellerProduct(seller, { title: `Shotsplate ${run}`, price: 18, quantity: 20, color: [60, 120, 200] });
  await L.sellerService(seller, { title: serviceTitle });
  const find = (q, extra = "") => until(`${q} in the catalog`, async () => { await L.pumpPortal(); return (await api("GET", `/api/v1/catalog/listings?q=${encodeURIComponent(q)}${extra}`)).body?.items?.[0] ?? null; }, { timeout: 90_000, every: 2000 });
  const product = await find(productTitle), service = await find(serviceTitle, "&type=service");
  const buyer = await L.newBuyer("shots");

  const order = await L.apiCheckoutAndPay(buyer, product.slug);
  const so = await until("the order in the seller portal", async () => { await L.pumpPortal(); return L.portalOrderFor(seller, order.number); }, { timeout: 60_000, every: 2000 });
  const detail = async () => (await L.must(portal("GET", `/api/v1/seller/orders/${so.id}`, { token: seller.token }), 200, "seller order")).body;
  let d = await detail();
  await L.must(portal("POST", `/api/v1/seller/orders/${so.id}/accept`, { token: seller.token, body: { version: d.version } }), 200, "accept");
  d = await detail();
  await L.must(portal("POST", `/api/v1/seller/orders/${so.id}/ship`, { token: seller.token, body: { version: d.version, carrier: "UPS", trackingNumber: `1Z999AA1${String(Math.floor(Math.random() * 1e10)).padStart(10, "0")}` } }), 200, "ship");
  d = await detail();
  await L.must(portal("POST", `/api/v1/seller/orders/${so.id}/deliver`, { token: seller.token, body: { version: d.version } }), 200, "deliver");
  d = await detail();
  await L.must(portal("POST", `/api/v1/seller/orders/${so.id}/refunds`, { token: seller.token, body: { version: d.version, lines: [{ lineId: d.lines[0].id, quantity: 1 }], reason: "goodwill" } }), [200, 201], "partial refund").catch(() => null);
  await until("the order to be delivered for the buyer", async () => { await L.pumpPortal(); return JSON.stringify((await api("GET", `/api/v1/orders/${order.number}`, { token: buyer.token })).body).includes("delivered"); }, { timeout: 90_000, every: 3000 });
  const booking = await L.apiBookAndPay(buyer, service);

  // a message to the seller about the order, an address, a wishlist, and a cart with an item: the buyer pages have something to show
  await api("POST", "/api/v1/addresses", { token: buyer.token, body: { ...L.ADDRESS, label: "Home", isDefault: true } }).catch(() => null);
  const plate = (await api("GET", `/api/v1/catalog/listings?q=Shotsplate%20${run}`)).body.items[0];
  const detailPlate = (await api("GET", `/api/v1/catalog/listings/${plate.slug}`)).body;
  await api("POST", "/api/v1/cart/lines", { token: buyer.token, body: { variantId: detailPlate.variants[0].id ?? detailPlate.variants[0].variantId, quantity: 2 } });
  await api("POST", "/api/v1/messages", { token: buyer.token, body: { orderNumber: order.number, sellerOrderNumber: so.number, text: "Hello, could you tell me when the vase ships?" } }).catch(() => null);
  await L.pumpPortal();
  return { seller, product, service, buyer, order, booking, brandSlug: product.brand.slug, sellerId: product.seller.id, categorySlug: product.category.slug };
}

async function main() {
  const f = await build();
  const pages = {
    home: "/", "search-results": `/search?q=Shots`, "search-empty": "/search?q=zzzzqqqq", product: `/p/${f.product.slug}`, service: `/p/${f.service.slug}`, categories: "/categories", services: "/services",
    brands: "/brands", category: `/c/${f.categorySlug}`, brand: `/b/${f.brandSlug}`, seller: `/s/${f.sellerId}`, "sign-in": "/sign-in", "sign-up": "/sign-up", "forgot-password": "/forgot-password",
    "verify-email": "/verify-email?sent=1", "not-found": "/does-not-exist", styleguide: "/styleguide", cart: "/cart", checkout: "/checkout",
    "confirmation": `/checkout/confirmation/${f.order.number}`, account: "/account", "account-orders": "/account/orders", "account-order": `/account/orders/${f.order.number}`,
    "account-bookings": "/account/bookings", booking: `/bookings/${f.booking.id}`, "account-addresses": "/account/addresses", "account-address-new": "/account/addresses/new",
    "account-notifications": "/account/notifications", "account-notification-prefs": "/account/notifications/preferences", "account-messages": "/account/messages", "account-wishlists": "/account/wishlists",
    "account-quotes": "/account/quotes", "account-profile": "/account/profile", "account-password": "/account/password", "account-passkeys": "/account/passkeys", "account-privacy": "/account/privacy",
    "account-demo": "/account/demo",
  };
  const signedOut = new Set(["home", "search-results", "search-empty", "product", "service", "categories", "services", "brands", "category", "brand", "seller", "sign-in", "sign-up", "forgot-password", "verify-email", "not-found", "styleguide"]);
  mkdirSync(OUT, { recursive: true });
  const browser = await launch();
  const axeProblems = [];
  let shots = 0;
  try {
    for (const [form, device] of [["desktop", devices["Desktop Chrome"]], ["mobile", devices["Pixel 7"]]]) {
      for (const theme of ["light", "dark"]) {
        step(`screenshots: ${form}, ${theme}`);
        const context = await browser.newContext({ ...device, baseURL: env.web, colorScheme: theme });
        await context.addCookies([{ name: "solvelt_theme", value: theme, url: env.web }]);
        const page = await context.newPage();
        // signed out pages first, then sign in and do the rest
        const visit = async (name, path) => {
          await go(page, path);
          await page.waitForLoadState("networkidle", { timeout: 8000 }).catch(() => {});   // images settle when they can; a slow one must not fail a screenshot run
          await page.screenshot({ path: join(OUT, `${name}-${form}-${theme}.png`), fullPage: true });
          shots++;
          const axe = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21aa", "wcag22aa"]).analyze();
          for (const v of axe.violations) axeProblems.push({ page: name, form, theme, rule: v.id, impact: v.impact, nodes: v.nodes.slice(0, 3).map((n) => n.target.join(" ")) });
        };
        for (const [name, path] of Object.entries(pages)) if (signedOut.has(name)) await visit(name, path);
        await go(page, "/sign-in");
        await page.getByLabel("Email").fill(f.buyer.email); await page.getByLabel("Password").fill(L.BUYER_PASSWORD);
        await page.getByRole("button", { name: "Sign in", exact: true }).click();
        await page.waitForURL("**/account", { timeout: 15_000 });
        for (const [name, path] of Object.entries(pages)) if (!signedOut.has(name)) await visit(name, path);
        await context.close();
      }
    }
  } finally { await browser.close(); }
  writeFileSync(join(OUT, "axe-report.json"), JSON.stringify(axeProblems, null, 1));
  ok(`${shots} screenshots in ${OUT}`);
  if (axeProblems.length) {
    console.error(`axe found ${axeProblems.length} violation(s): ${[...new Set(axeProblems.map((p) => `${p.page}/${p.form}/${p.theme}:${p.rule}`))].slice(0, 20).join(", ")} (docs/screenshots/journey/axe-report.json)`);
    if (process.env.AXE_STRICT === "1") process.exit(1);
  } else ok("axe: no violations in any page, form or theme");
}

try { await main(); console.log("SCREENSHOTS DONE"); }
catch (e) { console.error(`SCREENSHOTS FAILED: ${e instanceof Error ? e.message : e}`); process.exit(1); }
