import "../env.js";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { loadConfig, REPO_ROOT } from "../config.js";
import { loadRules, loadVocab } from "../build/data.js";
import { BuildFiles } from "../build/files.js";
import { computeIdeas } from "../build/ideas.js";
import { nameTwins } from "../build/label.js";
import { Truth, carryNames, scoreRecording } from "../build/score.js";
import { fixSizes } from "../build/sizes.js";
import { buildTwins, decodeScan } from "../build/twins.js";
import { models } from "../copilot/models.js";
import { KIT_SYSTEM, KitTurn, kitContextText } from "../copilot/kit.js";
import { routeOutcome, routeTurn } from "../copilot/router.js";
import { aiFor, ready } from "../ai.js";
import type { KitBuildContext } from "../build/session.js";

/**
 * pnpm build:eval [--live] [--router] [--check]
 *   (no flag)  every recording in data/build/recordings that has a truth.json: its saved names on THIS build's measurements
 *              (so a twin builder that got worse shows up). No key, no network.
 *   --live     name with the vision model and ask the design model too, on the providers KIT_AI picks (ai.ts).
 *   --router   the spoken-turn test set: the router outside build mode (on OpenAI, as the app routes), and Kit's turn
 *              inside it on every provider with a key; how often each is right, and right within its live budget.
 *   --check    exit 1 when a bar is missed: found 90%, labels 90%, size p90 2 cm, router 97%.
 */
const args = new Set(process.argv.slice(2));
const cfg = loadConfig();
const vocab = loadVocab(REPO_ROOT), rules = loadRules(REPO_ROOT, vocab), files = new BuildFiles(cfg.dataDir, REPO_ROOT);
const log = { warn: (o: object, m: string) => console.warn(`  ! ${m}`, o) };
const pct = (a: number, b: number) => (b ? `${Math.round((100 * a) / b)}%` : "–");
const q = (v: number[], p: number) => (v.length ? [...v].sort((a, b) => a - b)[Math.min(v.length - 1, Math.floor(v.length * p))]! : NaN);
let bad = false;

if (args.has("--router")) {
  type Case = { said: string; mode: string; ideas: string[]; expect: string };
  const set = JSON.parse(readFileSync(join(REPO_ROOT, "data", "build", "router-eval.json"), "utf8")) as Case[];
  const real = models(cfg);
  /** Judgement first, with patient budgets; each answer's time is then held to the live budget: a late answer is `late`. */
  const score = async (label: string, cases: Case[], budget: number, late: string, run: (c: Case) => Promise<string>) => {
    const ms: number[] = [];
    let right = 0, rightInTime = 0;
    for (const c of cases) {
      const t0 = Date.now();
      const got = await run(c);
      const took = Date.now() - t0;
      ms.push(took);
      if (got === c.expect) right++; else console.log(`  ✗ [${label}] "${c.said}" (${c.mode}) → ${got}, expected ${c.expect}`);
      if ((took <= budget ? got : late) === c.expect) rightInTime++;
      else if (got === c.expect) console.log(`  ⏱ [${label}] "${c.said}" was right, but took ${took} ms (budget ${budget} ms)`);
    }
    console.log(`${label}: ${right}/${cases.length} = ${pct(right, cases.length)} right; within budget: ${rightInTime}/${cases.length} = ${pct(rightInTime, cases.length)} (bar 97%); median ${q(ms, 0.5)} ms, p90 ${q(ms, 0.9)} ms`);
    if (cases.length && rightInTime / cases.length < 0.97) bad = true;
  };

  // Outside build mode: the router, on OpenAI as the app runs it, scored with the pipeline's own rule (only a sure
  // "build ideas" scans; a late answer makes the turn a question).
  const router = aiFor(cfg, "turn", "openai");
  const patientM = { ...real, budgets: { ...real.budgets, route: 5000 } };
  if (router?.provider === "openai") {
    await score(`router on openai (${real.router})`, set.filter((c) => c.mode !== "build"), real.budgets.route, "question", async (c) =>
      routeOutcome(await routeTurn(cfg, patientM, { transcript: c.said, mode: c.mode }, router)) === "scan" ? "build_ideas" : "question");
  } else console.log("router: skipped (OPENAI_API_KEY is not set; outside build mode the app needs it anyway)");

  // Build mode: Kit's turn from the words (OpenAI's path), with the set's designs on show and no objects, on every
  // provider with a key. A late answer is "That took too long. Ask me again."
  const providers = (["omni", "openai"] as const).filter((p) => ready(cfg, p));
  if (!providers.length) console.log("Kit's turn: skipped (neither OMNI_API_KEY + OMNI_BASE_URL nor OPENAI_API_KEY is set)");
  for (const provider of providers) {
    const ai = aiFor(cfg, "turn", provider)!;
    await score(`Kit's turn on ${provider} (${ai.model})`, set.filter((c) => c.mode === "build"), cfg.kitTurnMs, "too slow", async (c) => {
      const context: KitBuildContext = {
        status: c.ideas.length ? `showing ${c.ideas.length} designs` : "nothing scanned yet", twins: [], surfaces: [], camera: null, wish: null, started: null, tape: false,
        ideas: c.ideas.map((title, k) => ({ idea_id: `idea_eval${k + 1}`, title, why: "", uses: [], steps: 3 })),
      };
      try {
        const kit = KitTurn.parse(await ai.call(cfg, { name: "kit_turn", model: ai.model, schema: KitTurn, system: KIT_SYSTEM, text: kitContextText(context, null, c.said, []), timeoutMs: 20_000 }));
        return kit.intent === "ideas" ? "build_ideas" : kit.intent === "change" ? "modify_design" : kit.intent;
      } catch (err) { return `error: ${(err as Error).message.slice(0, 60)}`; }
    });
  }
}

const dir = files.recordingsDir;
const recs = existsSync(dir) ? readdirSync(dir).filter((d) => /^[a-z0-9_]+$/.test(d) && existsSync(join(dir, d, "truth.json"))) : [];
const all = { found: 0, truth: 0, labelsRight: 0, labelled: 0, sizeErrCm: [] as number[], rawErrCm: [] as number[] };
if (args.has("--live") && !aiFor(cfg, "label")) console.log("--live needs OMNI_API_KEY + OMNI_BASE_URL or OPENAI_API_KEY: naming by size and offering rule designs only");
for (const name of recs) {
  const truth = Truth.parse(JSON.parse(readFileSync(join(dir, name, "truth.json"), "utf8")));
  const { scan, photo } = files.readScan(`scan_rec_${name}`);
  const t0 = Date.now(), cloud = decodeScan(scan), built = buildTwins(cloud, scan.scan_id), msTwins = Date.now() - t0;
  const saved = args.has("--live") ? null : files.readLabels(scan.scan_id);
  const named = saved ? { twins: carryNames(built.twins, saved), by: "saved names" }
    : await nameTwins({ cfg, ai: aiFor(cfg, "label"), vocab, timeoutMs: 20_000, log }, photo, built.twins, built.surfaces, cloud);
  const twins = fixSizes(named.twins, vocab);
  const ideas = await computeIdeas(
    { cfg, vocab, rules, call: args.has("--live") ? aiFor(cfg, "ideas")?.call ?? null : null, model: aiFor(cfg, "ideas")?.model ?? "none",
      cacheDir: join(files.root, "idea-cache"), timeoutMs: 20_000, liveMs: cfg.buildLiveMs, log },
    { sessionId: "bsess_eval", twins, surfaces: built.surfaces, camera: scan.camera.position, photo, request: null }, () => {});
  // Two size errors: as measured (the twin builder's own work), and after known objects took their standard size (what
  // the designs use; zero for every object the vocabulary has a size for, so only the first shows a twin builder slipping).
  const s = scoreRecording(twins, truth), raw = scoreRecording(named.twins, truth);
  console.log(`${name} (${named.by}): found ${s.found}/${s.truth} (${pct(s.found, s.truth)}), labels ${pct(s.labelsRight, s.labelled)}, size err as measured median ${q(raw.sizeErrCm, 0.5)} cm p90 ${q(raw.sizeErrCm, 0.9)} cm, after standard sizes p90 ${q(s.sizeErrCm, 0.9)} cm, ideas ${ideas.length} [${ideas.map((i) => i.title).join(", ")}], twins ${msTwins} ms`);
  all.rawErrCm.push(...raw.sizeErrCm);
  all.found += s.found; all.truth += s.truth; all.labelsRight += s.labelsRight; all.labelled += s.labelled; all.sizeErrCm.push(...s.sizeErrCm);
}
if (recs.length) {
  console.log(`ALL: found ${pct(all.found, all.truth)} (bar 90%), labels ${pct(all.labelsRight, all.labelled)} (bar 90%), size p90 ${q(all.sizeErrCm, 0.9)} cm (bar 2 cm; as measured ${q(all.rawErrCm, 0.9)} cm)`);
  if (all.found / Math.max(1, all.truth) < 0.9 || all.labelsRight / Math.max(1, all.labelled) < 0.9 || q(all.sizeErrCm, 0.9) > 2) bad = true;
} else console.log(`no recordings with a truth.json in ${dir} yet: record one with pnpm build:record`);
if (args.has("--check") && bad) process.exit(1);
