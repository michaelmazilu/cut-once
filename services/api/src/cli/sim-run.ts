import "../env.js";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { buildApp } from "../app.js";
import { loadConfig } from "../config.js";
import { plugins } from "../plugins.js";
import { runScenario } from "../sim/scenario.js";

/**
 * pnpm -F @cutonce/api exec tsx src/cli/sim-run.ts --out <file> [--base <url> --token <token> --allow-live]
 * Without --base it starts its own server (temp data, fake copilot). With --base it tests a real one,
 * such as your laptop's. Exits 1 if any step failed.
 */
const arg = (name: string) => { const i = process.argv.indexOf(name); return i > 0 ? process.argv[i + 1] : undefined; };
const out = arg("--out");
let base = arg("--base");
if (base && !process.argv.includes("--allow-live")) {
  // The scenario starts a new run and forces a part wrong: on a live server every headset would jump to it.
  console.error(`Refusing to run against ${base}: the scenario starts a new run and marks the crossbar wrong,\n`
    + "which every connected headset follows. Never do this during a demo. Add --allow-live if you mean it.");
  process.exit(2);
}
let token = arg("--token") ?? "sim-token";
let stop = async () => {};

if (!base) {
  const dataDir = mkdtempSync(join(tmpdir(), "cutonce-scenario-"));
  const cfg = loadConfig({}, {
    dataDir, apiToken: token, host: "127.0.0.1", port: 0, logLevel: "silent",
    esUrl: "", esApiKey: "", kibanaUrl: "", mcpUrl: "", openaiKey: "", elevenKey: "", reconstruction: false,
    copilotMode: "fake", fakeCopilotDelayMs: 0,
  defaultSeed: "demo_start", testFixtures: true,   // the simulations and push reports use the desk test plan; the app starts blank
  });
  const app = await buildApp(cfg, plugins);
  await app.listen({ port: 0, host: "127.0.0.1" });
  base = `http://127.0.0.1:${(app.server.address() as AddressInfo).port}`;
  stop = async () => { await app.close(); rmSync(dataDir, { recursive: true, force: true }); };
} else {
  token = arg("--token") ?? process.env.API_TOKEN ?? token;
}

const result = await runScenario(base.replace(/\/$/, ""), token);
await stop();
for (const s of result.steps) console.log(`${s.ok ? "PASS" : "FAIL"}  ${s.name.padEnd(18)} ${String(s.ms).padStart(5)} ms  ${s.detail ?? ""}`);
console.log(result.ok ? "all steps pass" : `${result.steps.filter((s) => !s.ok).length} step(s) failed`);
if (out) {
  const path = resolve(process.cwd(), out);
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, JSON.stringify(result, null, 2));
}
process.exit(result.ok ? 0 : 1);
