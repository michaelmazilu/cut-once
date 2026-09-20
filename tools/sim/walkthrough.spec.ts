import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { expect, test } from "@playwright/test";
import { OUT, SIM_TOKEN } from "./paths.js";

/**
 * The Kit workflow end to end, as three prompts, photographed at every beat. This is the demo's
 * golden path run on the pretend headset (/sim), so it needs no Quest, no keys and no mic:
 *
 *   1. "Hey Kit, what can I build?"  → the table is scanned (the laptop replays the recorded scan,
 *      as /sim always does), the objects get names, and Kit's designs appear over the table.
 *   2. "Build the first one."        → the design is picked, the run starts, and Kit walks step 1.
 *   3. "Where does this go?"         → a scripted question mid-build: answer card + highlight.
 *      Then the step is finished and Kit reads out the next one — the progression beat.
 */
const SHOTS = join(OUT, "walkthrough");
mkdirSync(SHOTS, { recursive: true });

test.beforeEach(async ({ context }) => {
  await context.addInitScript((t) => localStorage.setItem("cutonce.api_token", t), SIM_TOKEN);
});

/** Same-origin API call from inside the page, with the page's own token. */
async function api(page: import("@playwright/test").Page, method: string, path: string, body?: unknown) {
  return page.evaluate(async ({ method, path, body }) => {
    const r = await fetch(path, {
      method,
      headers: { Authorization: `Bearer ${localStorage.getItem("cutonce.api_token")}`, "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    return { status: r.status, json: await r.json().catch(() => null) as any };
  }, { method, path, body });
}

test("kit walkthrough", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));

  // ── Beat 0: the pretend headset at rest ─────────────────────────────────────
  await page.goto("/sim?mode=build");
  await page.waitForFunction(() => (window as any).__previewReady === true, null, { timeout: 30_000 });
  await page.screenshot({ path: join(SHOTS, "01-idle.png") });

  // ── Prompt 1: "What can I build?" → scan replay → objects named, designs shown ──
  const replay = await api(page, "POST", "/v1/build/scans/scan_rec_synthetic_kit/replay", { labels: "saved" });
  expect(replay.status, `scan replay: ${JSON.stringify(replay.json)}`).toBe(200);
  await expect(page.locator(".sim-hint")).toBeVisible({ timeout: 30_000 });
  await page.waitForTimeout(1200);   // let the labels and previews settle for the photo
  await page.screenshot({ path: join(SHOTS, "02-what-can-i-build.png") });

  // ── Prompt 2: "Build the first one." ─────────────────────────────────────────
  const current = await api(page, "GET", "/v1/build/sessions/current");
  const idea = current.json?.ideas?.[0];
  expect(idea, `no ideas in ${JSON.stringify(current.json)}`).toBeTruthy();
  const started = await api(page, "POST", `/v1/build/ideas/${idea.idea_id}/start`, {});
  expect(started.status, `start idea: ${JSON.stringify(started.json)}`).toBe(200);
  // The session marks pre-placed objects built (the pizza box is already on the table), so the
  // first spoken step may be step 2. Assert the walkthrough line, whatever number it starts at.
  const stepLine = page.locator(".sim-answer", { hasText: /Step \d+ of \d+/ });
  await expect(stepLine).toBeVisible({ timeout: 20_000 });
  await page.waitForTimeout(800);
  await page.screenshot({ path: join(SHOTS, "03-build-walkthrough.png") });

  // ── Prompt 3: "Where does this go?" — pointing at the tall can, as a builder would ─────────
  // The ray comes from the mouse; park it on the can's ghost before asking, or Kit rightly
  // answers "point at a part and ask again".
  await page.mouse.move(690, 420);
  await page.waitForTimeout(400);
  await page.keyboard.press("1");
  await expect(page.getByText(/Answered in/)).toBeVisible({ timeout: 20_000 });
  await expect(page.locator(".sim-answer")).toContainText(/step/i, { timeout: 5_000 });
  await page.screenshot({ path: join(SHOTS, "04-kit-answers.png") });

  // ── The progression: finish the current step, Kit reads the next one ────────
  const aid = started.json.assembly_id as string;
  const state = await api(page, "GET", `/v1/assemblies/${aid}/state`);
  const plan = await api(page, "GET", `/v1/plans/${started.json.plan_id}?revision=${started.json.revision}`);
  const step = plan.json.steps.find((s: any) => s.step_id === state.json.current_step_id);
  for (const partId of step.part_ids) {
    const now = new Date().toISOString();
    await api(page, "POST", `/v1/assemblies/${aid}/events`, {
      event_id: `evt_${crypto.randomUUID().replace(/-/g, "").slice(0, 26).toUpperCase().replace(/[ILOU]/g, "A")}`,
      assembly_id: aid, version: null, timestamp: now, client_timestamp: now, kind: "part_state",
      part_id: partId, previous_state: "missing", new_state: "built", source: "manual", confidence: 1, actor: "walkthrough",
    });
  }
  await expect(page.locator(".sim-answer", { hasText: `Step ${step.index + 1} of` })).toBeVisible({ timeout: 20_000 });
  await page.waitForTimeout(500);
  await page.screenshot({ path: join(SHOTS, "05-next-step.png") });

  expect(errors, errors.join("; ")).toHaveLength(0);
});
