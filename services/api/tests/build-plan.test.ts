import { describe, expect, it } from "vitest";
import { validatePlan } from "@cutonce/project-model";
import type { Surface, Twin } from "@cutonce/schemas";
import { REPO_ROOT } from "../src/config.js";
import { loadRules, loadVocab, standardShape } from "../src/build/data.js";
import { toPlan } from "../src/build/plan.js";
import { matchRules } from "../src/build/rules.js";
import { chooseSite } from "../src/build/site.js";
import { solve } from "../src/build/solver.js";
import { checkStability } from "../src/build/stability.js";
import { twin } from "./build-synth.js";

const vocab = loadVocab(REPO_ROOT), rules = loadRules(REPO_ROOT, vocab);
let n = 0;
const std = (name: string, x = 0, z = 0.5): Twin => {
  const item = vocab.get(name)!;
  const shape = standardShape(item) ?? { type: "box" as const, size: [0.3, 0.2, 0.12] as [number, number, number] };
  return twin({ twin_id: `o${++n}`, name, label: item.label, shape, material: item.material, load_bearing: item.load_bearing, error_m: 0.003, snapped: true, position: [x, 0.8, z] });
};
const table: Surface = { surface_id: "s1", kind: "table", y: 0.74, min: [-0.6, 0.2], max: [0.6, 1.0], points: 900 };

describe("every rule in data/build/rules.json", () => {
  // Four identical cans, so the four-can riser is exercised as well as the three-can one.
  const pile = [std("tall_can"), std("tall_can"), std("tall_can"), std("tall_can"), std("pizza_box"), std("cardboard_box"), std("drink_can")];
  const matched = matchRules(rules, pile);

  it("matches the demo pile", () => expect(matched.map((m) => m.rule.rule_id).sort()).toEqual(rules.map((r) => r.rule_id).sort()));

  for (const m of matched) {
    it(`${m.rule.rule_id} solves, stands with its payload, and passes the plan checker`, () => {
      const byId = new Map(pile.map((t) => [t.twin_id, t]));
      const solved = solve(m.draft, byId);
      if (!solved.ok) throw new Error(solved.reason);
      expect(checkStability(solved.placed, byId, vocab, m.payload)).toEqual({ ok: true });
      const { plan } = toPlan({ ideaId: "idea_test", title: m.draft.title, why: m.draft.why, tools: m.draft.tools, source: "rule", ruleId: m.rule.rule_id, model: "rules", placed: solved.placed, twins: byId, projectId: "proj_cutonce_demo" });
      expect(validatePlan(plan).filter((i) => i.severity === "error")).toEqual([]);
      expect(plan.parts.filter((p) => p.rests_on.length === 0).map((p) => p.part_id)).toEqual(["part_surface"]);
      expect(plan.parts.some((p) => p.rotation_quat)).toBe(false);
    });
  }
});

describe("a taped design as a plan", () => {
  it("says what to tape in the step, joins the parts through a tape material, lists tape as a tool, and passes the plan checker", () => {
    const can = std("tall_can", 0, 0.5), box = { ...std("cardboard_box", 0, 0.5), shape: { type: "box" as const, size: [0.2, 0.12, 0.2] as [number, number, number] } };
    const things = new Map([can, box].map((t) => [t.twin_id, t]));
    const solved = solve({ title: "Birdhouse", why: "A home for birds.", tools: [], uses: [can.twin_id, box.twin_id], steps: [
      { place: can.twin_id, orientation: "upright", on: [], at_cm: null, next_to: null, side: null, gap_cm: null },
      { place: box.twin_id, orientation: "flat", on: [can.twin_id], at_cm: null, next_to: null, side: null, gap_cm: null, taped_to: [can.twin_id] },
    ] }, things, { tape: true });
    if (!solved.ok) throw new Error(solved.reason);
    const { plan } = toPlan({ ideaId: "idea_tape", title: "Birdhouse", why: "A home for birds.", tools: [], source: "ai", ruleId: null, model: "m", placed: solved.placed, twins: things, projectId: "proj_cutonce_demo" });
    expect(plan.steps.at(-1)!.instruction).toBe("Lay the cardboard box flat on top of the tall can. Tape it to the tall can.");
    expect(plan.parts.find((q) => q.part_id === `part_${box.twin_id}`)!.attaches_to).toEqual([{ part_id: `part_${can.twin_id}`, relation: "on", via_material_id: "mat_tape" }]);
    expect(plan.materials.find((m) => m.material_id === "mat_tape")).toMatchObject({ name: "Tape", unit: "strip", quantity: 1 });
    expect(plan.steps.at(-1)!.materials).toContainEqual({ material_id: "mat_tape", qty: 1 });
    expect(plan.provenance.assumptions).toContain("tools: tape");
    expect(validatePlan(plan).filter((i) => i.severity === "error")).toEqual([]);
  });
});

describe("matchRules", () => {
  it("needs identical cans for a rule that says same_name", () => {
    expect(matchRules(rules, [std("tall_can"), std("drink_can"), std("energy_can"), std("pizza_box")]).map((m) => m.rule.rule_id)).not.toContain("rule_laptop_riser");
  });
});

describe("chooseSite", () => {
  it("turns the design's front toward the viewer and puts it beside the pile, on the table", () => {
    const site = chooseSite(table, { min: [-0.1, 0.5], max: [0.1, 0.6] }, { w: 0.35, d: 0.35 }, [0, 1.6, -1]);
    expect(site.yaw_deg).toBeCloseTo(180, 0);
    expect(site.position[1]).toBeCloseTo(0.74);
    const [x, , z] = site.position;
    expect(x >= -0.6 + 0.175 && x <= 0.6 - 0.175 && z >= 0.2 && z <= 1.0).toBe(true);
    expect(Math.abs(x)).toBeGreaterThan(0.2);                     // beside the pile, not on it
  });

  it("goes to the clear side when something the design does not use stands on the other", () => {
    const pile = { min: [-0.1, 0.5] as [number, number], max: [0.1, 0.6] as [number, number] };
    const design = { w: 0.35, d: 0.35 }, viewer: [number, number, number] = [0, 1.6, -1];
    const free = chooseSite(table, pile, design, viewer);
    // Put a sponsor's box exactly where the design would have gone: it must now go to the other side of the pile.
    const inTheWay = { min: [free.position[0] - 0.1, free.position[2] - 0.1] as [number, number], max: [free.position[0] + 0.1, free.position[2] + 0.1] as [number, number] };
    const moved = chooseSite(table, pile, design, viewer, [inTheWay]);
    expect(Math.sign(moved.position[0])).toBe(-Math.sign(free.position[0]));
    expect(Math.abs(moved.position[0] - free.position[0])).toBeGreaterThan(0.4);
  });

  it("keeps the whole design on a table that is turned in the room, wherever the pile is", () => {
    // A 1.5 × 0.75 m table turned 30°. Its box along the room's axes has four empty corners a design must not land in.
    const yaw = 30, t = (yaw * Math.PI) / 180, c = Math.cos(t), sn = Math.sin(t);
    const corner = (u: number, w: number): [number, number] => [u * c + w * sn, 1 - u * sn + w * c];       // centre (0, 1)
    const xs = [corner(0.75, 0.375), corner(-0.75, 0.375), corner(0.75, -0.375), corner(-0.75, -0.375)];
    const turnedTable: Surface = {
      surface_id: "s1", kind: "table", y: 0.74, points: 2000, rect: { centre: [0, 1], len: 1.5, wid: 0.75, yaw_deg: yaw },
      min: [Math.min(...xs.map((q) => q[0])), Math.min(...xs.map((q) => q[1]))], max: [Math.max(...xs.map((q) => q[0])), Math.max(...xs.map((q) => q[1]))],
    };
    const onTable = (x: number, z: number) => { const u = (x - 0) * c - (z - 1) * sn, w = (x - 0) * sn + (z - 1) * c; return Math.abs(u) <= 0.75 + 1e-9 && Math.abs(w) <= 0.375 + 1e-9; };
    let beside = 0;
    for (let i = -3; i <= 3; i++) for (let k = -1; k <= 1; k++) {
      const [px, pz] = corner(i * 0.18, k * 0.12);
      const pile = { min: [px - 0.1, pz - 0.08] as [number, number], max: [px + 0.1, pz + 0.08] as [number, number] };
      const site = chooseSite(turnedTable, pile, { w: 0.35, d: 0.35 }, [0, 1.6, -1]);
      if (Math.hypot(site.position[0] - px, site.position[2] - pz) < 1e-6) continue;              // nothing fitted: built where the pile is
      beside++;
      const th = (site.yaw_deg * Math.PI) / 180, r = [Math.cos(th), -Math.sin(th)], f = [Math.sin(th), Math.cos(th)];
      for (const [a, b] of [[-1, -1], [1, -1], [1, 1], [-1, 1]]) {
        const x = site.position[0] + r[0]! * a! * 0.175 + f[0]! * b! * 0.175, z = site.position[2] + r[1]! * a! * 0.175 + f[1]! * b! * 0.175;
        expect([i, k, onTable(x, z)]).toEqual([i, k, true]);
      }
    }
    expect(beside).toBeGreaterThan(5);
  });

  it("on a crowded kitchen counter, finds the clear spot nearest the pile instead of drawing over what stands there", () => {
    // A 3 × 0.64 m counter against a wall. The design's pieces are spread along it, so beside the pile is off the
    // counter, in front is off the edge, and the pile's middle is where four cans (not in the design) stand.
    const counter: Surface = { surface_id: "s1", kind: "table", y: 0.92, points: 2000, min: [-1.5, -0.62], max: [1.5, 0.02], rect: { centre: [0, -0.3], len: 3, wid: 0.64, yaw_deg: 0 } };
    const pile = { min: [-0.6, -0.5] as [number, number], max: [1.0, -0.1] as [number, number] };
    const cans = { min: [0.04, -0.39] as [number, number], max: [0.31, -0.16] as [number, number] };
    const box = { min: [-0.95, -0.4] as [number, number], max: [-0.65, -0.2] as [number, number] };
    const design = { w: 0.6, d: 0.35 };
    const site = chooseSite(counter, pile, design, [0, 1.62, 1.0], [cans, box]);
    const th = (site.yaw_deg * Math.PI) / 180, r = [Math.cos(th), -Math.sin(th)], f = [Math.sin(th), Math.cos(th)];
    const pts = [[-1, -1], [1, -1], [1, 1], [-1, 1]].map(([a, b]) => [site.position[0] + r[0]! * a! * 0.3 + f[0]! * b! * 0.175, site.position[2] + r[1]! * a! * 0.3 + f[1]! * b! * 0.175]);
    for (const [x, z] of pts) expect([x! >= -1.5 && x! <= 1.5, z! >= -0.62 && z! <= 0.02]).toEqual([true, true]);
    const [minX, maxX, minZ, maxZ] = [Math.min(...pts.map((q) => q[0]!)), Math.max(...pts.map((q) => q[0]!)), Math.min(...pts.map((q) => q[1]!)), Math.max(...pts.map((q) => q[1]!))];
    for (const o of [cans, box]) expect(maxX <= o.min[0] || minX >= o.max[0] || maxZ <= o.min[1] || minZ >= o.max[1]).toBe(true);
    expect(Math.abs(site.position[0] - 0.2)).toBeLessThan(0.7);                 // near the pile, not at the far end of the counter
  });

  it("still gives a spot when every clear one is taken", () => {
    const everywhere = [{ min: [-5, -5] as [number, number], max: [5, 5] as [number, number] }];
    const site = chooseSite(table, { min: [-0.1, 0.5], max: [0.1, 0.6] }, { w: 0.35, d: 0.35 }, [0, 1.6, -1], everywhere);
    expect(Number.isFinite(site.position[0]) && Number.isFinite(site.position[2])).toBe(true);
  });
});
