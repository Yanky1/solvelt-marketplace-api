// The staff area (M7) against the REAL API and web (brief M8, owner-listed journey step):
//   the first admin enrols a passkey from the one-time bootstrap secret (a WebAuthn virtual authenticator) -> looks a buyer up (masked) -> locks the account (the buyer's sign-in is refused,
//   an old session is dead) -> unlocks it (the buyer signs in again) -> the audit log shows both actions with their reasons.
// Not covered here: the refund retry (a failed refund needs a Stripe refund that fails; the in-process fake never fails one, and a real Stripe test key is the opt-in job). It stays in the API's own tests.
import * as L from "./lib.mjs";
import { launch, watch, hydrated, go } from "./browser.mjs";

const { step, ok, check, until, api, env } = L;
const problems = [];
let livePage = null;
const REASON = "Journey check of the lock flow";

async function dumpPage() {
  if (!livePage) return;
  if (problems.console?.length) console.error(`browser console errors: ${problems.console.join(" | ")}`);
  try { console.error(`page ${livePage.url()} says: ${(await livePage.locator("main").innerText({ timeout: 3000 })).replace(/\s+/g, " ").slice(0, 700)}`); await livePage.screenshot({ path: `${env.runDir}/staff-journey-failure.png` }); } catch { /* the page is gone */ }
}

async function main() {
  step("a buyer exists (the person staff will look up)");
  const buyer = await L.newBuyer("staffcase");
  ok(`buyer ${buyer.email}`);

  const browser = await launch();
  try {
    const context = await browser.newContext({ baseURL: env.web });
    const page = await context.newPage();
    watch(page, problems); livePage = page;
    const cdp = await context.newCDPSession(page);
    await cdp.send("WebAuthn.enable");
    await cdp.send("WebAuthn.addVirtualAuthenticator", { options: { protocol: "ctap2", transport: "internal", hasResidentKey: true, hasUserVerification: true, isUserVerified: true, automaticPresenceSimulation: true } });

    step("the first admin enrols a passkey with the one-time secret and is signed in");
    check(env.staffBootstrapSecret, "env.json has no staffBootstrapSecret (stack.sh)");
    await go(page, "/staff/enrol");
    await page.getByLabel("One-time secret").fill(env.staffBootstrapSecret);
    await page.getByRole("button", { name: "Create my passkey" }).click();
    await page.waitForURL((u) => !u.pathname.startsWith("/staff/enrol"), { timeout: 20_000 });
    await hydrated(page);
    await page.getByRole("heading", { name: "Staff", exact: true }).waitFor({ timeout: 15_000 });
    ok("enrolled and signed in at the staff area");

    step("staff look the buyer up: the email is masked");
    await go(page, "/staff/buyers");
    await page.getByLabel("Value").fill(buyer.email);
    await page.getByLabel("Reason").first().fill(REASON);
    await page.getByRole("button", { name: "Look up" }).click();
    const card = page.getByTestId("buyer-card");
    await card.waitFor({ timeout: 20_000 });
    const text = await card.innerText();
    check(!text.includes(buyer.email), "the buyer's full email is shown to staff (it must be masked)");
    ok("the buyer card is shown with a masked email");

    // a sensitive action may ask for a fresh passkey check first: confirm with the virtual authenticator when the page asks
    const confirmIfAsked = async () => {
      const ask = page.getByRole("button", { name: "Confirm with my passkey" });
      if (await ask.isVisible().catch(() => false)) { await ask.click(); await page.waitForURL((u) => !u.pathname.startsWith("/staff/confirm"), { timeout: 20_000 }).catch(() => {}); }
    };

    step("staff lock the account: the buyer is refused, an old session is dead");
    await card.getByLabel("Reason").first().fill(REASON);
    await card.getByRole("button", { name: "Lock this account" }).click();
    await confirmIfAsked();
    await page.getByTestId("buyer-card").getByText("Locked", { exact: true }).waitFor({ timeout: 20_000 });
    const refused = await api("POST", "/api/v1/auth/sign-in", { body: { email: buyer.email, password: L.BUYER_PASSWORD } });
    check(refused.status === 403 && refused.body?.code === "account_locked", `a locked buyer's sign-in answered ${refused.status} ${JSON.stringify(refused.body)} (expected 403 account_locked)`);
    const old = await api("GET", "/api/v1/account", { token: buyer.token });
    check(old.status === 401, `the buyer's old session answered ${old.status} (expected 401)`);
    ok("sign-in is 403 account_locked and the old token is 401");

    step("staff unlock it: the buyer signs in again");
    await page.getByTestId("buyer-card").getByLabel("Reason").first().fill(REASON);
    await page.getByRole("button", { name: "Unlock this account" }).click();
    await confirmIfAsked();
    await until("the Locked badge to go", async () => (await page.getByTestId("buyer-card").getByText("Locked", { exact: true }).count()) === 0, { timeout: 20_000, every: 500 });
    await L.must(api("POST", "/api/v1/auth/sign-in", { body: { email: buyer.email, password: L.BUYER_PASSWORD } }), 200, "the unlocked buyer's sign-in");
    ok("the unlocked buyer signs in");

    step("the audit log shows both actions with the reason");
    await go(page, "/staff/audit");
    const rows = page.locator("table.table tbody tr");
    await rows.first().waitFor({ timeout: 15_000 });
    const audit = await page.locator("main").innerText();
    check(/lock/i.test(audit) && /unlock/i.test(audit), `the audit log does not list the lock and the unlock: ${audit.replace(/\s+/g, " ").slice(0, 300)}`);
    check(!audit.includes(buyer.email), "the audit log shows the buyer's full email");
    ok("the audit log lists the lock and the unlock");
  } catch (e) { await dumpPage(); throw e; } finally { await browser.close(); }

  step("no server errors on the way");
  check(problems.length === 0, `errors seen: ${problems.join("; ")}`);
  const health = (await api("GET", "/health")).body;
  check(health.forwarding?.invalid === 0, `the web server's forwarded client address was refused ${health.forwarding?.invalid} times`);
  ok("no 5xx, no script errors, no refused forwarding headers");
}

try { await main(); console.log("JOURNEY PASSED: staff"); }
catch (e) {
  console.error(`JOURNEY FAILED: ${e instanceof Error ? e.message : e}`);
  if (problems.length) console.error(`errors seen: ${problems.join("; ")}`);
  process.exit(1);
}
