// Playwright comes from the web checkout (its dependency), so this repo needs no browser tooling of its own.
import { createRequire } from "node:module";
import { join } from "node:path";
import { env } from "./lib.mjs";

const require = createRequire(join(env.webDir, "package.json"));
export const { chromium, devices } = require("@playwright/test");

export async function launch() {
  return chromium.launch({ ...(process.env.PW_CHROMIUM_PATH ? { executablePath: process.env.PW_CHROMIUM_PATH, args: ["--no-sandbox"] } : {}) });
}

/** A page that remembers every HTTP error (>= 400 except images) and script error, so a journey fails on a 5xx or a contract mismatch even when the page looked fine. */
export function watch(page, problems) {
  page.on("pageerror", (e) => problems.push(`page error: ${String(e).slice(0, 200)}`));
  page.on("console", (m) => { if (m.type() === "error") problems.console = [...(problems.console ?? []), m.text().slice(0, 300)].slice(-8); });   // shown when a step fails (a CSP refusal, a failed script)
  page.on("response", (r) => { const u = r.url(); if (r.status() >= 500 && !u.includes("/img/")) problems.push(`${r.status()} ${r.request().method()} ${u}`); });
}
export const hydrated = (page) => page.waitForSelector("html[data-hydrated=true]");

/** Opens a page and waits until it is hydrated (not "network idle": a slow image must not hide what the page did). On a timeout it names the requests still open. */
export async function go(page, path) {
  const open = new Map();
  const on = (r) => open.set(r, r.url());
  const off = (r) => open.delete(r);
  page.on("request", on); page.on("requestfinished", off); page.on("requestfailed", off);
  try {
    await page.goto(path, { waitUntil: "domcontentloaded" });
    await hydrated(page);
  } catch (e) {
    throw new Error(`${e.message.split("\n")[0]} (opening ${path}; still loading: ${[...open.values()].map((u) => u.slice(0, 120)).join(", ") || "nothing"})`);
  } finally { page.off("request", on); page.off("requestfinished", off); page.off("requestfailed", off); }
}
