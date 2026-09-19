import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { S, type Plan } from "@cutonce/schemas";
import type { FastifyBaseLogger } from "fastify";
import type { Config } from "./config.js";
import type { DocumentStore, KnownHash } from "./store/documents.js";
import type { Store } from "./store/store.js";

const json = (p: string) => JSON.parse(readFileSync(p, "utf8"));

/** The plan a fresh app starts on: nothing to draw until Kit builds something. It gives the copilot a run to talk in. */
export const blankPlan = (projectId: string): Plan => ({
  plan_id: "plan_blank", project_id: projectId, name: "Nothing built yet", revision: 1, status: "approved",
  frame: { handedness: "right", up: "+Y", units: "m", pose: "none", origin: "none" },
  layers: [], parts: [], materials: [], steps: [], markers: [], touch_points: [],
  provenance: { source_document_ids: [], extracted_by: "boot", approved_by: "boot", assumptions: [], validation: [] },
});

/**
 * Prepares DATA_DIR. The app gets one blank plan and starts on it: no hologram until Kit builds one. The desk and the
 * fixture plans are test data, loaded only with TEST_FIXTURES=on (the tests and simulations), never in the app.
 */
export async function boot(store: Store, docs: DocumentStore, cfg: Config, log: FastifyBaseLogger) {
  store.importApproved(blankPlan(cfg.projectId));
  store.putSeed({ seed: "blank", plan_id: "plan_blank", built: [] });

  const planFiles: string[] = [];
  const demo = join(cfg.repoRoot, "data", "demo");
  if (cfg.testFixtures) {
    if (existsSync(demo)) planFiles.push(...readdirSync(demo).filter((f) => f.endsWith(".plan.json")).map((f) => join(demo, f)));
    for (const f of ["plan_desk_archetype.json", "plan_asymmetric.json"]) {
      const p = join(cfg.repoRoot, "data", "fixtures", f);
      if (existsSync(p)) planFiles.push(p);
    }
  }
  for (const file of planFiles) {
    const parsed = S.Plan.safeParse(json(file));
    if (!parsed.success) { log.warn({ file }, "skipped a plan file that does not match the schema"); continue; }
    if (store.importApproved(parsed.data as Plan)) log.info({ plan_id: parsed.data.plan_id, revision: parsed.data.revision }, "imported plan");
    const meshFiles = new Set<string>();
    for (const part of parsed.data.parts) {
      const shape = part.shape as { type: string; uri?: string };
      if (shape.type === "mesh" && shape.uri) meshFiles.add(shape.uri);
    }
    for (const uri of meshFiles) {
      const source = join(dirname(file), uri);
      // A clone made without Git LFS has a small text pointer here instead of the model: say so, or the
      // preview and headset quietly fall back to boxes.
      if (existsSync(source) && readFileSync(source).subarray(0, 64).toString().startsWith("version https://git-lfs")) {
        log.warn({ file: source }, `${uri} is a Git LFS pointer, not the model: install git-lfs and run \`git lfs pull\``);
      }
      store.syncAsset(parsed.data.plan_id, uri, source);
    }
  }

  const seeds = join(demo, "seeds");
  if (cfg.testFixtures && existsSync(seeds)) for (const f of readdirSync(seeds).filter((f) => f.endsWith(".json"))) {
    const seed = S.Seed.safeParse(json(join(seeds, f)));
    if (seed.success) store.putSeed(seed.data);
  }

  const known = join(demo, "known_hashes.json");
  if (cfg.testFixtures && existsSync(known)) docs.mergeKnown(json(known) as Record<string, KnownHash>);

  // A fresh data folder starts with one run: the configured seed (blank unless DEFAULT_SEED says otherwise), else blank.
  // An existing current run is never replaced here.
  const seedNames = store.listSeeds();
  const first = [cfg.defaultSeed, "blank"].find((s) => seedNames.includes(s));
  if (!store.currentAssembly() && first) {
    try { const a = await store.createAssembly({ seed: first }); log.info({ assembly_id: a.assembly_id, seed: first }, "created the first run"); }
    catch (err) { log.warn({ err, seed: first }, "could not create the first run"); }
  }
}
