import { createHash } from "node:crypto";
import { readFileSync, existsSync } from "node:fs";
import { join } from "node:path";
import { z } from "zod";
import { validatePlan } from "@cutonce/project-model";
import { S, Strict, type BuildIdea, type IdeaDraft, type Surface, type Twin, type Vec3 } from "@cutonce/schemas";
import type { Config } from "../config.js";
import { normalise } from "../copilot/fastpath.js";
import { writeJsonAtomic } from "../store/fs.js";
import type { Payload, Rule, Vocab } from "./data.js";
import { newId } from "./files.js";
import type { ModelCall } from "./label.js";
import { toPlan } from "./plan.js";
import { matchRules } from "./rules.js";
import { describeShape, dimsCm, halfOf, volumeOf } from "./shape.js";
import { chooseSite, type Box2 } from "./site.js";
import { solve } from "./solver.js";
import { checkStability } from "./stability.js";

export interface IdeasDeps {
  cfg: Config; vocab: Vocab; rules: Rule[];
  /** The designing job's model call, or null when no provider has a key (then: the cache, then the rules). */
  call: ModelCall | null;
  model: string; cacheDir: string; timeoutMs: number;
  /** How long the live answer is waited for before the rehearsal cache is shown (BUILD_LIVE_MS). */
  liveMs: number;
  log: { warn: (o: object, m: string) => void };
  /** Work that finishes after the answer (a late live answer refreshing the cache); the session's idle() awaits it. */
  background?: (work: Promise<unknown>) => void;
  /** A progress line for the HUD, such as "Checked 4 designs: 3 stand up." */
  note?: (text: string) => void;
}
export interface IdeasInput {
  sessionId: string; twins: Twin[]; surfaces: Surface[]; camera: Vec3; photo: Buffer | null;
  /** What the builder asked for ("a birdhouse", or "a birdhouse, then something crazier"), or null. */
  request: string | null;
  /** Titles already offered in this session that must not be offered again (the session decides which count). */
  offered?: string[];
}
type Made = NonNullable<BuildIdea["made"]>;
type Candidate = { draft: IdeaDraft; source: "rule" | "ai"; made: Made; ruleId: string | null; payload: Payload | null };

const Out = z.object({ ideas: z.array(S.IdeaDraft) });
const OutStrict = z.object({ ideas: z.array(Strict.IdeaDraft) });

/** Part of every cache key: a change to SYSTEM below must retire the designs cached under the old words. */
export const PROMPT_VERSION = "kit-4";

const SYSTEM = [
  "You are Kit, the Kitbash co-pilot. You design small things a person can build right now from the real objects in front of them, like a Master Builder in the Lego Movie.",
  "You get an inventory of objects with measured sizes, and a photo. The objects can be anything. Return exactly 4 designs, each as bottom-up placement steps:",
  "- place: an object id from the inventory (each at most once).",
  "- orientation: upright (tallest side up), flat (thinnest side up) or on_side (middle side up). Cans, bottles, mugs and other cylinders can only be upright. An object marked as standing as found may be laid down (an orientation no taller than it is now) but never stood up on a smaller face.",
  "- on: [] for the table, or ids already placed that it rests on. Supports must be able to hold weight and be the SAME height: use identical objects as supports.",
  "- at_cm: {x, z} on the table (x to the right, z toward the viewer, origin the centre of the build), or null.",
  "- next_to, side (left/right/front/back), gap_cm: or put it beside an object already on the table.",
  "- taped_to: [] or ids already placed that this piece touches and is taped to. Only when TOOLS lists tape. Taped pieces act as one solid piece, so a box taped onto a single can stands. Everything must still rest on the table or on supports, and a tall, narrow taped stack still falls over.",
  "Something resting on supports needs at least 3 supports that are not in a line, or one support at least as wide as it. Keep weight over what holds it.",
  "If THE BUILDER ASKED for something, every design must be that thing or clearly serve it. If these objects cannot make it, give the closest designs you can and say so in why.",
  "Designs listed as already offered must not be repeated unless the builder asks for one again.",
  "At most 6 objects. No cutting. Title: 2–4 words saying what it is for. why: one fun sentence a judge would enjoy.",
].join("\n");

export function inventoryText(twins: Twin[], surfaces: Surface[]): string {
  const s = surfaces.map((x) => `${x.surface_id} ${x.kind} at ${Math.round(x.y * 100)} cm`).join("; ");
  // "as found": only this thing's size was measured, so its shape is a guess (a bowl, a kettle). It can be laid
  // down but not stood up on a smaller face. Saying so here is what keeps the first round of designs buildable.
  const lines = twins.map((t) => `${t.twin_id} ${t.label}: ${describeShape(t.shape)}; ${t.material}; ${t.load_bearing ? "can hold weight" : "cannot hold weight"}${t.sits_on ? `; on ${t.sits_on}` : ""}${t.name === "other" ? "; stands as found (may be laid down, never stood up)" : ""}`);
  return `Surfaces: ${s || "none"}.\nObjects:\n${lines.join("\n")}\nTOOLS: ${hasTape(twins) ? "tape (a roll is on the table)" : "none"}`;
}

/** Is there tape on the table (a roll, or anything Kit named as tape)? Then designs may tape pieces together. */
export const hasTape = (twins: Twin[]) => twins.some((t) => t.name === "tape_roll" || (/\btape\b/.test(normalise(t.label)) && !/\b(tape measure|measuring tape)\b/.test(normalise(t.label))));

/** An object's identity in a cache key: a vocabulary name, or the model's own name for anything else. */
const kindOf = (t: Twin) => (t.name === "other" ? `other:${normalise(t.label)}` : t.name);

/**
 * The cache key and the canonical ids (c1… in kind-then-size order) that map cached designs onto a new scan of the
 * same things. Sizes to the centimetre for objects with a standard size, to 5 cm for measured ones: two scans of one
 * box differ by a centimetre or two, and a cached design is checked again against today's sizes anyway. The wish and
 * the prompt's version are in the key too: a birdhouse is not the answer to "something crazier". Not the model: a
 * design that stands is checked again whoever made it, and a rehearsal on one provider must serve the other, or none.
 */
export function canonical(twins: Twin[], wish: string | null = null) {
  const sorted = [...twins].sort((a, b) => kindOf(a).localeCompare(kindOf(b)) || volumeOf(a.shape) - volumeOf(b.shape) || a.twin_id.localeCompare(b.twin_id));
  const size = (t: Twin) => dimsCm(t.shape).map((v) => (t.snapped ? Math.round(v) : 5 * Math.round(v / 5)));
  const key = createHash("sha1").update(JSON.stringify([PROMPT_VERSION, sorted.map((t) => [kindOf(t), size(t)]), wish ? normalise(wish) : ""])).digest("hex").slice(0, 16);
  return { key, toCanon: new Map(sorted.map((t, i) => [t.twin_id, `c${i + 1}`])), fromCanon: new Map(sorted.map((t, i) => [`c${i + 1}`, t.twin_id])) };
}

/** A twin's outer bound along the room's axes, whichever way it is turned. */
const roomBox = (t: Twin): Box2 => {
  const h = halfOf(t.shape), r = Math.max(h[0], h[2]);
  return { min: [t.position[0] - r, t.position[2] - r], max: [t.position[0] + r, t.position[2] + r] };
};

const remap = (d: IdeaDraft, m: Map<string, string>): IdeaDraft | null => {
  const id = (x: string) => m.get(x);
  const ids = [...d.uses, ...d.steps.flatMap((s) => [s.place, ...s.on, ...(s.next_to ? [s.next_to] : []), ...(s.taped_to ?? [])])];
  if (ids.some((x) => !id(x))) return null;
  return {
    ...d, uses: d.uses.map((x) => id(x)!),
    steps: d.steps.map((s) => ({ ...s, place: id(s.place)!, on: s.on.map((x) => id(x)!), next_to: s.next_to ? id(s.next_to)! : null, ...(s.taped_to ? { taped_to: s.taped_to.map((x) => id(x)!) } : {}) })),
  };
};

function buildSurface(twins: Twin[], surfaces: Surface[]): Surface | null {
  const votes = new Map<string, number>();
  for (const t of twins) if (t.sits_on) votes.set(t.sits_on, (votes.get(t.sits_on) ?? 0) + 1);
  const top = [...votes.entries()].sort((a, b) => b[1] - a[1])[0]?.[0];
  return surfaces.find((s) => s.surface_id === top) ?? surfaces[0] ?? null;
}

function check(c: Candidate, byId: Map<string, Twin>, surface: Surface, input: IdeasInput, deps: IdeasDeps): { idea: BuildIdea } | { reason: string } {
  const missing = c.draft.steps.find((s) => !byId.has(s.place));
  if (missing) return { reason: `${missing.place} is not in the inventory` };
  const solved = solve(c.draft, byId, { tape: hasTape(input.twins) });
  if (!solved.ok) return { reason: solved.reason };
  const stable = checkStability(solved.placed, byId, deps.vocab, c.payload);
  if (!stable.ok) return { reason: stable.reason };
  const ideaId = newId("idea");
  const { plan, twinOf, size } = toPlan({ ideaId, title: c.draft.title, why: c.draft.why, tools: c.draft.tools, source: c.source, ruleId: c.ruleId, model: deps.model, placed: solved.placed, twins: byId, projectId: deps.cfg.projectId });
  const error = validatePlan(plan).find((i) => i.severity === "error");
  if (error) return { reason: `plan check ${error.code}: ${error.message}` };
  const used = solved.placed.map((p) => byId.get(p.twin_id)!);
  const pile: Box2 = { min: [Math.min(...used.map((t) => roomBox(t).min[0])), Math.min(...used.map((t) => roomBox(t).min[1]))], max: [Math.max(...used.map((t) => roomBox(t).max[0])), Math.max(...used.map((t) => roomBox(t).max[1]))] };
  // Everything else standing on that surface, named or not, is in the way of a hologram.
  const inTheWay = input.twins.filter((t) => t.sits_on === surface.surface_id && !used.includes(t)).map(roomBox);
  const site = chooseSite(surface, pile, { w: size[0], d: size[1] }, input.camera, inTheWay);
  return { idea: {
    idea_id: ideaId, session_id: input.sessionId, source: c.source, rule_id: c.ruleId, title: c.draft.title, why: c.draft.why, tools: c.draft.tools,
    plan, origin: { position: site.position, rotation_quat: site.rotation_quat }, twin_of: twinOf, score: (c.source === "rule" ? 50 : 100) + used.length, made: c.made,
  } };
}

const top3 = (list: BuildIdea[]) => {
  const seen = new Set<string>();
  return [...list].sort((a, b) => b.score - a.score).filter((i) => { const k = i.title.toLowerCase(); if (seen.has(k)) return false; seen.add(k); return true; }).slice(0, 3);
};

const sleep = (ms: number) => new Promise<null>((resolve) => { const t = setTimeout(() => resolve(null), ms); t.unref?.(); });
const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/**
 * The designs for what is on the table: Kit's own, asked for live. If the live answer is not back within liveMs,
 * the designs saved earlier (at rehearsal) for the same objects and wish are shown instead, and the live answer only
 * refreshes the cache when it lands: a preview never changes under the judge's pointer. With nothing cached, Kit
 * keeps waiting for the live answer. The stored rules come last: with no model at all, or when nothing else stands.
 * Designs already offered (input.offered) are not offered again, unless nothing else stands. One final list.
 */
export async function computeIdeas(deps: IdeasDeps, input: IdeasInput, emit: (ideas: BuildIdea[], final: boolean) => void): Promise<BuildIdea[]> {
  const usable = input.twins.filter((t) => t.name !== "unknown" && t.confidence >= 0.5);
  const byId = new Map(usable.map((t) => [t.twin_id, t]));
  const surface = buildSurface(usable, input.surfaces);
  if (!surface || usable.length === 0) { emit([], true); return []; }
  const canon = canonical(usable, input.request);
  const offered = new Set((input.offered ?? []).map((t) => t.toLowerCase()));
  const fresh = (list: BuildIdea[]) => list.filter((i) => !offered.has(i.title.toLowerCase()));
  let cache: BuildIdea[] | null = null;                                // read and checked once, whoever asks first
  const fromCache = () => (cache ??= cachedIdeas(deps, input, byId, surface, canon));
  const fromRules = () => matchRules(deps.rules, usable)
    .map((m) => check({ draft: m.draft, source: "rule", made: "rule", ruleId: m.rule.rule_id, payload: m.payload }, byId, surface, input, deps))
    .flatMap((r) => ("idea" in r ? [r.idea] : []));

  // Each source in order: the live answer (or, when it is late, the cache), the cache, the stored rules. New designs
  // from any source beat repeats; repeats beat an empty list (a rethink whose only designs were shown already).
  let first: BuildIdea[] = [];
  if (deps.call) {
    const live = invent(deps, input, usable, byId, surface, canon);
    const settled = live.then((r) => ({ r }), (e: Error) => ({ e }));
    const early = await Promise.race([settled, sleep(deps.liveMs)]);
    // Only new designs from the cache end the wait: repeats of what was just shown are worth less than the live answer.
    const cached = early === null ? fresh(fromCache()) : [];
    if (early === null && cached.length) {
      first = cached;
      deps.background?.(settled.then((s) => { if ("e" in s) deps.log.warn({ err: s.e.message }, "a late live design answer failed"); }));
    } else {
      const done = early ?? (await settled);
      if ("e" in done) deps.log.warn({ err: done.e.message }, "live build ideas failed; using the cache or the rules");
      else {
        first = done.r.ideas;
        if (done.r.tried > 0) deps.note?.(`Checked ${plural(done.r.tried, "design", "designs")}: ${plural(done.r.ideas.length, "stands", "stand")} up.`);
      }
    }
  }
  let ideas: BuildIdea[] = [], repeats: BuildIdea[] = [];
  for (const source of [() => first, fromCache, fromRules]) {
    const all = source(), kept = fresh(all);
    if (kept.length) { ideas = kept; break; }
    if (!repeats.length) repeats = all;
  }
  if (!ideas.length) ideas = repeats;
  const shown = top3(ideas);
  emit(shown, true);
  return shown;
}

/** The designs cached for these objects and this wish, mapped onto today's twins and checked again. */
function cachedIdeas(deps: IdeasDeps, input: IdeasInput, byId: Map<string, Twin>, surface: Surface, canon: ReturnType<typeof canonical>): BuildIdea[] {
  const path = join(deps.cacheDir, `${canon.key}.json`);
  if (!existsSync(path)) return [];
  try {
    const drafts = (JSON.parse(readFileSync(path, "utf8")) as { drafts: IdeaDraft[] }).drafts;
    return drafts.map((d) => remap(d, canon.fromCanon)).filter((d): d is IdeaDraft => d !== null)
      .map((draft) => check({ draft, source: "ai", made: "cache", ruleId: null, payload: null }, byId, surface, input, deps))
      .flatMap((r) => ("idea" in r ? [r.idea] : []));
  } catch (err) {
    deps.log.warn({ err: (err as Error).message }, "the cached build ideas could not be read");
    return [];
  }
}

/** One live ask (and one repair round for the designs that fail a check); what stands is cached under the key. */
async function invent(deps: IdeasDeps, input: IdeasInput, usable: Twin[], byId: Map<string, Twin>, surface: Surface, canon: ReturnType<typeof canonical>): Promise<{ ideas: BuildIdea[]; tried: number }> {
  const call = deps.call!;
  const offered = input.offered ?? [];
  const text = inventoryText(usable, input.surfaces)
    + (offered.length ? `\nAlready offered, do not repeat: ${offered.join(", ")}.` : "")
    + (input.request ? `\nThe builder asked: "${input.request}".` : "");
  const ask = async (t: string, photo: Buffer | null) => Out.parse(await call(deps.cfg, {
    name: "build_ideas", model: deps.model, schema: Out, strictSchema: OutStrict, system: SYSTEM, text: t, timeoutMs: deps.timeoutMs,
    images: photo ? [{ data: photo, mime: "image/jpeg" as const }] : [],
  }));
  const drafts = (await ask(text, input.photo)).ideas;
  const ai = (draft: IdeaDraft) => check({ draft, source: "ai", made: "live", ruleId: null, payload: null }, byId, surface, input, deps);
  const ok: { draft: IdeaDraft; idea: BuildIdea }[] = [], failed: { draft: IdeaDraft; reason: string }[] = [];
  for (const draft of drafts) { const r = ai(draft); if ("idea" in r) ok.push({ draft, idea: r.idea }); else failed.push({ draft, reason: r.reason }); }
  let tried = drafts.length;
  if (failed.length) {
    const fix = `${text}\n\nThese designs failed a check. Fix each one and return only the fixed designs:\n${failed.map((f) => `- ${JSON.stringify(f.draft)}\n  failed because ${f.reason}`).join("\n")}`;
    // The repair is a bonus: if it fails, the designs that already passed still go out.
    try {
      const repaired = (await ask(fix, null)).ideas;
      tried += repaired.length;
      for (const draft of repaired) { const r = ai(draft); if ("idea" in r) ok.push({ draft, idea: r.idea }); }
    } catch (err) { deps.log.warn({ err: (err as Error).message }, "the repair round for AI build ideas failed; keeping the designs that passed"); }
  }
  if (ok.length) {
    try { writeJsonAtomic(join(deps.cacheDir, `${canon.key}.json`), { drafts: ok.map((o) => remap(o.draft, canon.toCanon)).filter(Boolean), model: deps.model, at: new Date().toISOString() }); }
    catch (err) { deps.log.warn({ err: (err as Error).message }, "could not cache the AI build ideas"); }
  }
  return { ideas: ok.map((o) => o.idea), tried };
}

const NUM = ["no", "a", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
const list = (w: string[]) => (w.length <= 1 ? w.join("") : `${w.slice(0, -1).join(", ")} and ${w.at(-1)}`);

/** "three tall cans, a pizza box and a tape roll": every named object, counted. Empty when none has a name. */
/** "a" or "an", by the word's first sound (vowel letters, which covers every label and title we have). */
const a = (words: string) => (/^[aeiou]/i.test(words) ? `an ${words}` : `a ${words}`);

export function describeFound(twins: Twin[]): string {
  const counts = new Map<string, number>();
  for (const t of twins) if (t.name !== "unknown") counts.set(t.label, (counts.get(t.label) ?? 0) + 1);
  return list([...counts.entries()].map(([label, n]) => (n === 1 ? a(label) : `${NUM[n] ?? n} ${label}s`)));
}

/** What the voice says when the final ideas arrive. */
export function summary(twins: Twin[], ideas: Pick<BuildIdea, "title">[]): string {
  const found = describeFound(twins);
  if (!found) return "I couldn't make out any objects. Try looking from a little closer.";
  if (!ideas.length) return `I found ${found}, but nothing I tried stands up. Add something flat to go on top, or three things the same height.`;
  return `I found ${found}. You could build ${list(ideas.map((i) => a(i.title.toLowerCase())))}.`;
}
