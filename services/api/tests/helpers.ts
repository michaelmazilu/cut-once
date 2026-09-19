import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { ulid } from "ulid";
import { buildApp } from "../src/app.js";
import { loadConfig, type Config } from "../src/config.js";
import { plugins } from "../src/plugins.js";

export const TOKEN = "test-token";
export const auth = { authorization: `Bearer ${TOKEN}` };

export async function makeApp(overrides: Partial<Config> = {}) {
  const dataDir = overrides.dataDir ?? mkdtempSync(join(tmpdir(), "cutonce-"));
  // The tests were written around the half-built desk (test data only), so they load it and pin it as the first run; the app starts blank.
  const cfg = loadConfig({}, { dataDir, apiToken: TOKEN, logLevel: "silent", esUrl: "", esApiKey: "", openaiKey: "", defaultSeed: "demo_start", testFixtures: true, ...overrides });
  const app = await buildApp(cfg, plugins);
  return { app, dataDir, cleanup: async () => { await app.close(); rmSync(dataDir, { recursive: true, force: true }); } };
}

export const builtEvent = (aid: string, partId: string, from = "missing", to = "built", extra: Record<string, unknown> = {}) => {
  const now = new Date().toISOString();
  return { event_id: `evt_${ulid()}`, assembly_id: aid, version: null, timestamp: now, client_timestamp: now, kind: "part_state",
    part_id: partId, previous_state: from, new_state: to, source: "manual", confidence: 1, actor: "operator", ...extra };
};
