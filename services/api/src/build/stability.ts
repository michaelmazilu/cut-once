import type { Twin } from "@cutonce/schemas";
import type { Payload, Vocab } from "./data.js";
import { centroid, circlePoly, clip, hull, margin, rectPoly, type P2 } from "./poly.js";
import { halfOf, volumeOf } from "./shape.js";
import type { Placed } from "./solver.js";

const DENSITY: Record<Twin["material"], number> = { cardboard: 60, metal: 1000, plastic: 900, glass: 1200, wood: 600, paper: 700, fabric: 200, ceramic: 1500, other: 300 };
const cm = (m: number) => (m * 100).toFixed(1);

const footprint = (p: Placed): P2[] => p.shape.type === "cylinder" && p.shape.axis === "y"
  ? circlePoly(p.position[0], p.position[2], p.shape.diameter / 2)
  : rectPoly(p.position[0], p.position[2], halfOf(p.shape)[0], halfOf(p.shape)[2]);

/**
 * Static tipping check. Every part rests flat, so sliding is impossible and the only failure is tipping. For each
 * object, the downward forces on it are its own weight at its centre, plus the load of each thing resting on it,
 * applied where they touch. Their combined point must fall inside what holds it up (its contact patches with its
 * supports, or its own footprint on the table) by at least max(1 cm, the size error), plus 1 cm for every level
 * stacked above it: a person sets each piece down about a centimetre off, and those errors add up, so four cans
 * stacked on end pass a perfect-placement check and fall over on a real table. Deterministic and exact.
 *
 * Balance alone is not enough: a pizza box centred on one can balances on paper and falls when a finger touches its
 * edge. So what holds a piece up must also span at least SUPPORT_SPAN of it, along both of its sides. Three cans in a
 * triangle do, and so does one support as wide as the piece: the rule the ideas model is given, kept here in code.
 */
export const PLACEMENT_ERROR = 0.01;
export const SUPPORT_SPAN = 0.5;
/**
 * Taped pieces: tape makes them one rigid body, so the span rule does not apply inside it, but the body as a whole
 * must still stand. Its weight must land inside what holds it up by the usual margin, and it must lean at least
 * TIP_MIN_DEG before its weight passes that edge: a tall, narrow taped stack falls at a nudge like an untaped one.
 * Tape holds pieces in place, it does not carry them: at most TAPE_MAX_KG may hang on it (the pieces resting on other
 * taped pieces, and whatever rests on those). A joint that is not taped is a loose joint, whatever is taped above it.
 */
export const TIP_MIN_DEG = 7;
export const TAPE_MAX_KG = 1.5;

const plural = (labels: string[]) => {
  const unique = [...new Set(labels)];
  return unique.length === 1 ? `${unique[0]}s` : `${unique.slice(0, -1).join(", ")} and ${unique.at(-1)}`;
};

export function checkStability(placed: Placed[], twins: Map<string, Twin>, vocab: Vocab, payload: Payload | null): { ok: true } | { ok: false; reason: string } {
  const byId = new Map(placed.map((p) => [p.twin_id, p]));
  const top = placed.at(-1)!;
  const mass = (p: Placed) => { const t = twins.get(p.twin_id)!; return volumeOf(p.shape) * (vocab.get(t.name)?.density_kg_m3 ?? DENSITY[t.material]); };
  const above = new Map<string, Placed[]>();
  for (const p of placed) for (const s of p.rests_on) above.set(s, [...(above.get(s) ?? []), p]);
  const carried = new Map<string, number>();
  const carriedBy = (p: Placed): number => {
    const hit = carried.get(p.twin_id);
    if (hit !== undefined) return hit;
    let m = mass(p) + (payload && p === top ? payload.kg : 0);
    for (const q of above.get(p.twin_id) ?? []) m += carriedBy(q) / q.rests_on.length;
    carried.set(p.twin_id, m);
    return m;
  };
  const levels = new Map<string, number>();
  const levelsAbove = (p: Placed): number => {
    const hit = levels.get(p.twin_id);
    if (hit !== undefined) return hit;
    const n = Math.max(0, ...(above.get(p.twin_id) ?? []).map((q) => 1 + levelsAbove(q)));
    levels.set(p.twin_id, n);
    return n;
  };
  // Rigid bodies: pieces joined by tape (union-find over taped_to). A body of one piece is checked on its own below.
  const root = new Map(placed.map((p) => [p.twin_id, p.twin_id]));
  const find = (id: string): string => { const r = root.get(id)!; if (r === id) return id; const head = find(r); root.set(id, head); return head; };
  for (const p of placed) for (const q of p.taped_to) root.set(find(p.twin_id), find(q));
  const bodies = new Map<string, Placed[]>();
  for (const p of placed) bodies.set(find(p.twin_id), [...(bodies.get(find(p.twin_id)) ?? []), p]);
  for (const body of bodies.values()) {
    if (body.length < 2) continue;
    const verdict = checkTaped(body);
    if (!verdict.ok) return verdict;
  }

  function checkTaped(body: Placed[]): { ok: true } | { ok: false; reason: string } {
    const ids = new Set(body.map((p) => p.twin_id)), names = plural(body.map((p) => p.label));
    let m = 0, sx = 0, sy = 0, sz = 0;
    for (const p of body) { const w = mass(p) + (payload && p === top ? payload.kg : 0); m += w; sx += w * p.position[0]; sy += w * p.position[1]; sz += w * p.position[2]; }
    // What rests on the body from outside it presses where it touches.
    for (const p of body) for (const q of above.get(p.twin_id) ?? []) {
      if (ids.has(q.twin_id)) continue;
      const patch = clip(footprint(q), footprint(p));
      if (patch.length === 0) continue;
      const share = carriedBy(q) / q.rests_on.length, c = centroid(patch);
      sx += share * c[0]; sy += share * (p.position[1] + halfOf(p.shape)[1]); sz += share * c[1]; m += share;
    }
    // What holds it up: the footprints of its pieces on the table, and its contact patches with supports outside it.
    const contact: P2[] = [];
    let base = Infinity;
    const outside: string[] = [];
    for (const p of body) {
      if (p.rests_on.length === 0) { contact.push(...footprint(p)); base = Math.min(base, p.position[1] - halfOf(p.shape)[1]); }
      for (const r of p.rests_on) if (!ids.has(r)) {
        const support = byId.get(r)!;
        outside.push(r);
        contact.push(...clip(footprint(p), footprint(support)));
        base = Math.min(base, support.position[1] + halfOf(support.shape)[1]);
      }
    }
    const region = hull(contact), load: P2 = [sx / m, sz / m];
    const levels = Math.max(0, ...body.flatMap((p) => (above.get(p.twin_id) ?? []).filter((q) => !ids.has(q.twin_id)).map((q) => 1 + levelsAbove(q))));
    const need = Math.max(0.01, ...body.map((p) => twins.get(p.twin_id)!.error_m), ...outside.map((id) => twins.get(id)!.error_m)) + PLACEMENT_ERROR * levels;
    const got = margin(load, region);
    if (got < need) {
      return { ok: false, reason: got < 0 || !Number.isFinite(got)
        ? `the taped ${names} would tip: their weight lands ${Number.isFinite(got) ? cm(-got) : "well"} cm outside what holds them up`
        : `the taped ${names} are only ${cm(got)} cm from tipping; they need ${cm(need)} cm` };
    }
    const lean = (Math.atan2(got, Math.max(1e-6, sy / m - base)) * 180) / Math.PI;
    if (lean < TIP_MIN_DEG) return { ok: false, reason: `the taped ${names} would tip over at a ${lean.toFixed(0)}° lean; a taped stack needs ${TIP_MIN_DEG}°` };
    // The tape holds the pieces resting on other taped pieces, and everything resting on those from outside the body.
    const held = body.filter((p) => p.rests_on.some((r) => ids.has(r)));
    let resting = 0;
    for (const p of held) {
      if (payload && p === top) resting += payload.kg;
      for (const q of above.get(p.twin_id) ?? []) if (!ids.has(q.twin_id)) resting += carriedBy(q) / q.rests_on.length;
    }
    const heldKg = held.reduce((sum, p) => sum + mass(p), 0) + resting;
    if (heldKg > TAPE_MAX_KG) {
      const what = held.length === 1 ? held[0]!.label : plural(held.map((p) => p.label));
      return { ok: false, reason: `tape cannot hold the ${what} in place: ${held.length === 1 ? "it weighs" : "they weigh"} ${heldKg.toFixed(1)} kg`
        + `${resting > 0 ? ` with what rests on ${held.length === 1 ? "it" : "them"}` : ""}, over ${TAPE_MAX_KG} kg` };
    }
    return { ok: true };
  }

  const taped = (p: Placed) => (bodies.get(find(p.twin_id))?.length ?? 1) > 1;
  for (const p of placed) {
    // A taped piece on the table or on a piece it is taped to is checked with its rigid body above. One resting only on
    // pieces outside its body sits on a loose joint: tape above it does not stop it sliding off, so it is checked here.
    if (taped(p) && (p.rests_on.length === 0 || p.rests_on.some((r) => find(r) === find(p.twin_id)))) continue;
    const t = twins.get(p.twin_id)!;
    let m = mass(p) + (payload && p === top ? payload.kg : 0);
    let sx = m * p.position[0], sz = m * p.position[2];
    for (const q of above.get(p.twin_id) ?? []) {
      const patch = clip(footprint(q), footprint(p));
      if (patch.length === 0) continue;
      const share = carriedBy(q) / q.rests_on.length, c = centroid(patch);
      sx += share * c[0]; sz += share * c[1]; m += share;
    }
    const load: P2 = [sx / m, sz / m];
    const region = p.rests_on.length === 0 ? footprint(p) : hull(p.rests_on.flatMap((id) => clip(footprint(p), footprint(byId.get(id)!))));
    const need = Math.max(0.01, t.error_m, ...p.rests_on.map((id) => twins.get(id)!.error_m)) + PLACEMENT_ERROR * levelsAbove(p);
    const got = margin(load, region);
    if (got < need) {
      return { ok: false, reason: got < 0 || !Number.isFinite(got)
        ? `the ${t.label} would tip: its weight lands ${Number.isFinite(got) ? cm(-got) : "well"} cm outside what holds it up`
        : `the ${t.label} is only ${cm(got)} cm from tipping; it needs ${cm(need)} cm` };
    }
    if (p.rests_on.length > 0) {
      const own = footprint(p);
      for (const k of [0, 1] as const) {
        const span = (poly: P2[]) => (poly.length ? Math.max(...poly.map((q) => q[k])) - Math.min(...poly.map((q) => q[k])) : 0);
        if (span(region) < SUPPORT_SPAN * span(own)) {
          return { ok: false, reason: `the ${t.label} overhangs what holds it up: its supports span ${cm(span(region))} cm of its ${cm(span(own))} cm. Use three supports that are not in a line, or one at least half as wide` };
        }
      }
    }
  }
  return { ok: true };
}
