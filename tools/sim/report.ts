import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { diffPlans, type PartChange, type PlanDiff } from "@cutonce/project-model";
import type { Plan } from "@cutonce/schemas";
import { compareScreens } from "./compare.js";
import { BASELINE, CURRENT, OUT, ROOT } from "./paths.js";
import type { PlanReport, ReportInput, RunMeta, ScenarioResult } from "./types.js";

/** Plans whose geometry is compared push to push. */
export const TRACKED_PLANS = ["data/demo/desk.plan.json"];
/** A blueprint-read part counts as right when it is within this many mm of the known-good part. */
export const EXTRACTION_OK_MM = 5;

const sha7 = (m: RunMeta) => m.sha.slice(0, 7);
const mm = (v: number) => `${Number.isInteger(v) ? v : v.toFixed(1)} mm`;
const secs = (ms: number) => (ms >= 1000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.round(ms)} ms`);
const esc = (s: string) => s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!);

function describeChange(c: PartChange): string {
  if (c.change === "added") return `${c.name} added`;
  if (c.change === "removed") return `${c.name} removed`;
  const bits: string[] = [];
  if ((c.moved_mm ?? 0) > 0) bits.push(`moved ${mm(c.moved_mm!)}`);
  if ((c.resized_mm ?? 0) > 0) bits.push(`resized ${mm(c.resized_mm!)}`);
  if (c.fields.length) bits.push(`${c.fields.join(", ")} changed`);
  return `${c.name} ${bits.join(" and ") || "changed"}`;
}

function describePlan(p: PlanReport): string {
  if (!p.diff) return `- \`${p.file}\`: ${p.note ?? "not compared"}`;
  const n = p.diff.changes.length;
  if (n === 0) return `- \`${p.file}\`: no change`;
  const shown = p.diff.changes.slice(0, 6).map(describeChange).join(", ");
  return `- \`${p.file}\`: ${n} changed: ${shown}${n > 6 ? `, and ${n - 6} more` : ""}`;
}

/** Error of a blueprint-read part against its known-good match: the larger of its move and its resize. */
const partError = (c: PartChange) => Math.max(c.moved_mm ?? 0, c.resized_mm ?? 0);

/** Known-good parts the reading got within EXTRACTION_OK_MM, out of all known-good parts. */
export function extractionScore(diff: PlanDiff): { within: number; total: number } {
  const nearlyRight = diff.changes.filter((c) => c.change === "changed" && partError(c) <= EXTRACTION_OK_MM).length;
  return { within: diff.unchanged + nearlyRight, total: diff.unchanged + diff.changes.filter((c) => c.change !== "added").length };
}

export function describeExtraction(diff: PlanDiff, before: PlanDiff | null = null): string {
  const off = diff.changes.filter((c) => c.change === "changed" && partError(c) > EXTRACTION_OK_MM);
  const missing = diff.changes.filter((c) => c.change === "removed");
  const extra = diff.changes.filter((c) => c.change === "added");
  const { within, total } = extractionScore(diff);
  const worst = [...off].sort((a, b) => partError(b) - partError(a)).slice(0, 3).map((c) => `${c.name} ${mm(partError(c))} off`);
  let line = `Blueprint reading: ${within}/${total} parts within ${EXTRACTION_OK_MM} mm`;
  if (before) { const was = extractionScore(before); line += ` (was ${was.within}/${was.total})`; }
  if (worst.length) line += `; worst: ${worst.join(", ")}`;
  if (missing.length) line += `; missing: ${missing.map((c) => c.name).join(", ")}`;
  if (extra.length) line += `; extra: ${extra.map((c) => c.name).join(", ")}`;
  return line;
}

export function buildMarkdown(r: ReportInput): string {
  const out: string[] = [];
  out.push(r.baseline
    ? `## Simulation report · ${sha7(r.current)} vs ${sha7(r.baseline)}${r.current.dirty ? " (with uncommitted changes)" : ""}`
    : `## Simulation report · ${sha7(r.current)}\n\nNo earlier run to compare with, so everything below is new.`);

  const cur = r.scenario.current, old = r.scenario.baseline;
  const failing = cur?.steps.filter((s) => !s.ok) ?? [];
  if (failing.length) {
    out.push(failing.map((s) => {
      const was = old?.steps.find((b) => b.name === s.name);
      return `**FAILING: ${s.name}**${s.detail ? `: ${s.detail}` : ""}${was?.ok ? " (passed in the baseline)" : ""}`;
    }).join("  \n"));
  }

  if (!cur) out.push("Behaviour: not run");
  else {
    const pass = cur.steps.filter((s) => s.ok).length;
    const was = old ? ` (was ${old.steps.filter((s) => s.ok).length}/${old.steps.length})` : "";
    const slowest = [...cur.steps].sort((a, b) => b.ms - a.ms)[0];
    const slowWas = slowest && old?.steps.find((s) => s.name === slowest.name);
    out.push(`Behaviour: ${pass}/${cur.steps.length} steps pass${was}.` +
      (slowest ? ` Slowest: ${slowest.name} ${secs(slowest.ms)}${slowWas ? ` (was ${secs(slowWas.ms)})` : ""}.` : ""));
  }

  const shown = r.screens.filter((s) => s.status !== "removed");
  const changed = r.screens.filter((s) => s.status === "changed" || s.status === "size_changed");
  const added = r.screens.filter((s) => s.status === "new");
  const removed = r.screens.filter((s) => s.status === "removed");
  let pictures = `Pictures: ${changed.length} of ${shown.length} scenes changed`;
  if (r.baseline && added.length) pictures += `, ${added.length} new`;
  if (removed.length) pictures += `, ${removed.length} removed`;
  const rows = r.screens.filter((s) => s.status !== "same" && (r.baseline || s.status !== "new")).map((s) => {
    const what = s.status === "changed" ? `${s.diffPct}% of pixels` : s.status === "size_changed" ? "size changed" : s.status === "new" ? "new scene" : "removed";
    return `| ${s.scene} | ${what} |`;
  });
  out.push(rows.length ? `${pictures}\n\n| Scene | Change |\n|---|---|\n${rows.join("\n")}` : pictures);

  out.push(`Plans:\n${r.plans.map(describePlan).join("\n")}`);
  out.push(r.extraction.diff ? describeExtraction(r.extraction.diff, r.extraction.baseline ?? null) : `Blueprint reading: skipped (${r.extraction.skipped ?? "not run"})`);
  out.push("Full report with images: artifact `sim-out` → report.html");
  return out.join("\n\n") + "\n";
}

export function buildHtml(r: ReportInput): string {
  const img = (src: string, label: string) => `<figure><img src="${esc(src)}" alt="${esc(label)}"><figcaption>${esc(label)}</figcaption></figure>`;
  const scenes = r.screens.map((s) => {
    const pics = [
      s.status !== "new" && r.baseline ? img(`baseline/screens/${s.scene}.png`, "Before") : "",
      s.status !== "removed" ? img(`current/screens/${s.scene}.png`, "After") : "",
      s.status === "changed" ? img(`diff/${s.scene}.png`, "Difference") : "",
    ].join("");
    const badge = s.status === "changed" ? `${s.diffPct}% changed` : s.status.replace("_", " ");
    return `<section class="scene ${s.status}"><h3>${esc(s.scene)} <small>${esc(badge)}</small></h3><p>${esc(s.note)}</p><div class="pics">${pics}</div></section>`;
  }).join("\n");
  const steps = (r.scenario.current?.steps ?? []).map((s) => {
    const was = r.scenario.baseline?.steps.find((b) => b.name === s.name);
    return `<tr class="${s.ok ? "ok" : "bad"}"><td>${esc(s.name)}</td><td>${s.ok ? "pass" : "FAIL"}</td><td>${secs(s.ms)}</td><td>${was ? secs(was.ms) : ""}</td><td>${esc(s.detail ?? "")}</td></tr>`;
  }).join("");
  const md = buildMarkdown(r);
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>Cut Once simulation report</title>
<style>
body{font:14px/1.5 system-ui,sans-serif;margin:24px;background:#0b1017;color:#e6edf5}
h1,h2,h3{font-weight:600}small{color:#9aa7b5;font-weight:400;margin-left:8px}
pre{white-space:pre-wrap;background:#121a24;padding:12px;border-radius:8px}
.scene{border-top:1px solid #243140;padding:12px 0}.scene.same{opacity:.6}
.pics{display:flex;gap:12px;flex-wrap:wrap}figure{margin:0}img{width:400px;border:1px solid #243140;border-radius:6px}
figcaption{color:#9aa7b5;font-size:12px}table{border-collapse:collapse}td{padding:4px 10px;border-bottom:1px solid #243140}
tr.bad td{color:#ff8a8a}
</style></head><body>
<h1>Cut Once simulation report</h1>
<pre>${esc(md)}</pre>
<h2>Behaviour</h2>
${steps ? `<table><tr><td>Step</td><td>Result</td><td>Now</td><td>Before</td><td>Detail</td></tr>${steps}</table>` : "<p>Not run.</p>"}
<h2>Pictures</h2>
${scenes || "<p>No scenes.</p>"}
</body></html>
`;
}

// ── CLI: tsx tools/sim/report.ts ────────────────────────────────────────────
const readJson = <T>(path: string): T | null => (existsSync(path) ? (JSON.parse(readFileSync(path, "utf8")) as T) : null);
const git = (...args: string[]) => execFileSync("git", args, { cwd: ROOT, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] });

function planAt(sha: string, file: string): Plan | null {
  try { return JSON.parse(git("show", `${sha}:${file}`)) as Plan; } catch { return null; }
}

function main(): number {
  const current = readJson<RunMeta>(join(CURRENT, "meta.json"))
    ?? { sha: git("rev-parse", "HEAD").trim(), dirty: git("status", "--porcelain").trim() !== "", created_at: new Date().toISOString(), ci: !!process.env.CI };
  const baseline = readJson<RunMeta>(join(BASELINE, "meta.json"));
  const screens = compareScreens(CURRENT, baseline ? BASELINE : null, join(OUT, "diff"));

  const plans: PlanReport[] = TRACKED_PLANS.map((file) => {
    const now = readJson<Plan>(join(ROOT, file));
    if (!now) return { file, diff: null, note: "missing in this run" };
    if (!baseline) return { file, diff: null, note: "no baseline" };
    const before = planAt(baseline.sha, file);
    return before ? { file, diff: diffPlans(before, now) } : { file, diff: null, note: `not in ${baseline.sha.slice(0, 7)}` };
  });

  const extracted = readJson<Plan>(join(CURRENT, "extracted.plan.json"));
  const known = readJson<Plan>(join(ROOT, "data/demo/desk.plan.json"));
  const skippedFile = join(CURRENT, "extraction-skipped.txt");
  const extractedBefore = readJson<Plan>(join(BASELINE, "extracted.plan.json"));
  const extraction = extracted && known
    ? { diff: diffPlans(known, extracted), skipped: null, baseline: extractedBefore ? diffPlans(known, extractedBefore) : null }
    : { diff: null, skipped: existsSync(skippedFile) ? readFileSync(skippedFile, "utf8").trim() : "not run" };

  const input: ReportInput = {
    current, baseline, screens, plans,
    scenario: { current: readJson<ScenarioResult>(join(CURRENT, "scenario.json")), baseline: readJson<ScenarioResult>(join(BASELINE, "scenario.json")) },
    extraction,
  };
  mkdirSync(OUT, { recursive: true });
  writeFileSync(join(OUT, "report.md"), buildMarkdown(input));
  writeFileSync(join(OUT, "report.json"), JSON.stringify(input, null, 2));
  writeFileSync(join(OUT, "report.html"), buildHtml(input));
  console.log(buildMarkdown(input));
  return input.scenario.current && !input.scenario.current.ok ? 1 : 0;
}

// report.html links sim-out/baseline/…, sim-out/current/… and sim-out/diff/…, so the artifact works on its own.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) process.exit(main());
