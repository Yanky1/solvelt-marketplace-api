// The service-booking journey against the REAL portal, API and web (brief M8): a seller (portal API) publishes a service with a provider and working hours -> the catalog syncs ->
// a buyer picks a day and time on the service page -> holds it -> pays -> the booking is confirmed (the portal says so, through its signed webhook) -> the seller sees it.
import { readFileSync } from "node:fs";
import * as L from "./lib.mjs";
import { launch, watch, go } from "./browser.mjs";
import { exposePay } from "./pay.mjs";

const { step, ok, check, until, portal, api, env } = L;
const run = L.uniq();
const TITLE = `Journeyclean ${run}`;
const problems = [];
let livePage = null;

async function dumpPage() {
  if (!livePage) return;
  if (problems.console?.length) console.error(`browser console errors: ${problems.console.join(" | ")}`);
  try { console.error(`page ${livePage.url()} says: ${(await livePage.locator("main").innerText({ timeout: 3000 })).replace(/\s+/g, " ").slice(0, 700)}`); await livePage.screenshot({ path: `${env.runDir}/failure-booking.png` }); } catch { /* the page is gone */ }
}

async function main() {
  step("a seller publishes a service with a provider in the real portal");
  const seller = await L.newApprovedSeller(`Journey Services ${run}`);
  const serviceId = await L.sellerService(seller, { title: TITLE });
  ok(`service ${serviceId}`);

  step("the catalog syncs the service");
  const listing = await until("the service in the marketplace catalog", async () => {
    await L.pumpPortal();
    return (await api("GET", `/api/v1/catalog/listings?q=${encodeURIComponent(TITLE)}&type=service`)).body?.items?.[0] ?? null;
  }, { timeout: 90_000, every: 2000 });
  ok(`listing ${listing.slug}`);

  const buyer = await L.newBuyer("booker");
  const browser = await launch();
  try {
    const context = await browser.newContext({ baseURL: env.web });
    const page = await context.newPage();
    watch(page, problems); livePage = page;
    await exposePay(page, () => buyer.token);

    step("the buyer signs in and opens the service page");
    await go(page, "/sign-in");
    await page.getByLabel("Email").fill(buyer.email); await page.getByLabel("Password").fill(L.BUYER_PASSWORD);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
    await page.waitForURL("**/account", { timeout: 15_000 });
    await go(page, `/p/${listing.slug}`);

    step("availability: the buyer sees times, in the provider's zone");
    const slot = page.getByTestId("booking-slot");
    // the default place is "Online": the times are listed at once (an at-home booking would ask for a postal code first)
    await slot.getByRole("group", { name: "Choose a day" }).getByRole("button").first().waitFor({ timeout: 30_000 });
    await slot.getByRole("group", { name: "Choose a day" }).getByRole("button").first().click();
    await slot.getByRole("group", { name: /^Times on / }).getByRole("button").first().click();
    ok("days and times are shown by the real availability engine");

    step("hold the time");
    const form = page.getByRole("form", { name: /^Book / });
    if (await form.getByLabel("Street address").count()) {   // only a visit at the buyer's home needs an address
      await form.getByLabel("Name").fill("Dana Buyer");
      await form.getByLabel("Street address").fill("1 Main St");
      await form.getByLabel("City").fill("Brooklyn");
      await form.getByLabel("State or region").fill("NY");
    }
    await page.getByRole("button", { name: new RegExp(`^Hold ${TITLE}`) }).click();
    await page.waitForURL(/\/bookings\/[0-9a-f-]+$/, { timeout: 30_000 });
    const bookingId = page.url().split("/").pop();
    check((await page.getByTestId("booking-status").getAttribute("data-status")) === "Hold", "the booking starts as a Hold");
    ok(`hold ${bookingId}`);

    step("pay");
    await page.getByRole("button", { name: /^Continue to payment/ }).click();
    await page.getByTestId("payment-step").waitFor({ timeout: 20_000 });
    await page.getByRole("button", { name: /^Pay / }).click();
    await page.waitForURL(new RegExp(`/bookings/${bookingId}`), { timeout: 20_000 });

    step("the portal confirms the booking and the buyer sees it");
    await until("the booking to be Confirmed", async () => {
      await L.pumpPortal();
      await page.reload({ waitUntil: "domcontentloaded" });
      return (await page.getByTestId("booking-status").getAttribute("data-status").catch(() => null)) === "Confirmed";
    }, { timeout: 90_000, every: 3000 });
    ok("the buyer's booking page says Confirmed");

    step("the booking is in the seller portal, paid");
    const sellerView = await until("the booking in the seller portal", async () => {
      const r = await portal("GET", "/api/v1/seller/bookings", { token: seller.token });
      const items = r.body?.items ?? r.body?.bookings ?? [];
      return items.find((b) => JSON.stringify(b).includes("Confirmed")) ?? null;
    }, { timeout: 30_000, every: 2000 });
    ok(`seller booking ${sellerView.number ?? sellerView.id} is Confirmed`);
  } catch (e) { await dumpPage(); throw e; } finally { await browser.close(); }

  step("no server errors on the way");
  check(problems.length === 0, `errors seen: ${problems.join("; ")}`);
  const health = (await api("GET", "/health")).body;
  check(health.forwarding?.invalid === 0, "no refused forwarding headers");
  check(health.outbox?.deadLettered === 0, `${health.outbox?.deadLettered} outbox messages were dead-lettered`);
  check(!/^crit:/m.test(readFileSync(`${env.runDir}/api.log`, "utf8")), "the API log holds no critical line");
  ok("clean");
}

try { await main(); console.log("JOURNEY PASSED: booking"); }
catch (e) { console.error(`JOURNEY FAILED: ${e instanceof Error ? e.message : e}`); if (problems.length) console.error(`errors seen: ${problems.join("; ")}`); process.exit(1); }
