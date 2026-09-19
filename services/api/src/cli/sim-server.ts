import "../env.js";
import { existsSync, mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { S } from "@cutonce/schemas";
import { buildApp } from "../app.js";
import { loadConfig } from "../config.js";
import { plugins } from "../plugins.js";

/**
 * A server for simulations: fresh temp data, no keys, no Elasticsearch, token `sim-token`.
 * Never point a headset at it. If a blueprint-reading run left sim-out/current/extracted.plan.json,
 * that plan is imported as plan_desk_extracted so the report can photograph it.
 */
const dataDir = mkdtempSync(join(tmpdir(), "cutonce-sim-"));
const cfg = loadConfig({}, {
  dataDir, apiToken: "sim-token", host: "127.0.0.1", port: Number(process.env.SIM_PORT ?? 8787), logLevel: "warn",
  esUrl: "", esApiKey: "", kibanaUrl: "", mcpUrl: "", openaiKey: "", elevenKey: "", reconstruction: false,
  copilotMode: "fake", fakeCopilotDelayMs: 0,
  defaultSeed: "demo_start", testFixtures: true,   // the simulations and push reports use the desk test plan; the app starts blank
});
const app = await buildApp(cfg, plugins);
const extracted = join(cfg.repoRoot, "sim-out", "current", "extracted.plan.json");
if (existsSync(extracted)) {
  const plan = S.Plan.parse(JSON.parse(readFileSync(extracted, "utf8")));
  app.ctx.store.importApproved({ ...plan, plan_id: "plan_desk_extracted", revision: 1 });
}
await app.listen({ port: cfg.port, host: cfg.host });
console.log(`sim server on http://${cfg.host}:${cfg.port} (data ${dataDir})`);
for (const signal of ["SIGINT", "SIGTERM"] as const) process.on(signal, () => void app.close().then(() => process.exit(0)));
