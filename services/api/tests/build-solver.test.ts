import { describe, expect, it } from "vitest";
import type { IdeaDraft, PlaceStep, Twin } from "@cutonce/schemas";
import { REPO_ROOT } from "../src/config.js";
import { loadVocab, standardShape } from "../src/build/data.js";
import { solve } from "../src/build/solver.js";
import { checkStability } from "../src/build/stability.js";
import { twin } from "./build-synth.js";

const vocab = loadVocab(REPO_ROOT);
const std = (name: string, id: string): Twin => {
  const item = vocab.get(name)!;
  return twin({ twin_id: id, name, label: item.label, shape: standardShape(item) ?? { type: "box" as const, size: [0.3, 0.2, 0.12] as [number, number, number] }, material: item.material, load_bearing: item.load_bearing, error_m: 0.003, snapped: true });
};
const step = (s: Partial<PlaceStep> & { place: string }): PlaceStep => ({ orientation: "upright", on: [], at_cm: null, next_to: null, side: null, gap_cm: null, ...s });
const draft = (steps: PlaceStep[]): IdeaDraft => ({ title: "t", why: "w", tools: [], uses: steps.map((s) => s.place), steps });
const kit = new Map([std("tall_can", "o1"), std("tall_can", "o2"), std("tall_can", "o3"), std("pizza_box", "o4"), std("drink_can", "o5")].map((t) => [t.twin_id, t]));
const riser = draft([
  step({ place: "o1", at_cm: { x: -12, z: -12 } }), step({ place: "o2", at_cm: { x: 12, z: -12 } }), step({ place: "o3", at_cm: { x: 0, z: 12 } }),
  step({ place: "o4", orientation: "flat", on: ["o1", "o2", "o3"] }),
]);

describe("solve", () => {
  it("stands cans on the table and lays the board flat on their tops, centred", () => {
    const r = solve(riser, kit);
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    const board = r.placed[3]!;
    expect(board.shape).toEqual({ type: "box", size: [0.35, 0.04, 0.35] });
    expect(board.position[1]).toBeCloseTo(0.157 + 0.02, 6);
    expect(board.position[0]).toBeCloseTo(0, 6);
    expect(board.position[2]).toBeCloseTo(-0.04, 6);
    expect(board.rests_on).toEqual(["o1", "o2", "o3"]);
  });
  it("refuses supports of different heights, with the reason", () => {
    const r = solve(draft([step({ place: "o1", at_cm: { x: -12, z: 0 } }), step({ place: "o5", at_cm: { x: 12, z: 0 } }), step({ place: "o4", orientation: "flat", on: ["o1", "o5"] })]), kit);
    expect(r).toMatchObject({ ok: false });
    expect(!r.ok && r.reason).toMatch(/differ by 35 mm/);
  });
  it("keeps something outside the vocabulary the way it stands: a bowl measured as a box is never stood on its rim", () => {
    const bowl = twin({ twin_id: "o9", name: "other", label: "fruit bowl", shape: { type: "box", size: [0.22, 0.08, 0.14] } });
    const withBowl = new Map([...kit, ["o9", bowl]]);
    const r = solve(draft([step({ place: "o9", orientation: "upright" })]), withBowl);
    expect(!r.ok && r.reason).toBe("the fruit bowl only rests the way it stands now; keep it flat");
    expect(solve(draft([step({ place: "o9", orientation: "flat" })]), withBowl)).toMatchObject({ ok: true });
  });
  it("refuses a can on its side", () => expect(solve(draft([step({ place: "o1", orientation: "on_side" })]), kit)).toMatchObject({ ok: false }));
  it("refuses overlaps", () => expect(solve(draft([step({ place: "o1", at_cm: { x: 0, z: 0 } }), step({ place: "o2", at_cm: { x: 3, z: 0 } })]), kit)).toMatchObject({ ok: false }));
  it("puts next_to things beside each other with the gap", () => {
    const r = solve(draft([step({ place: "o1" }), step({ place: "o2", next_to: "o1", side: "right", gap_cm: 4 })]), kit);
    expect(r.ok && r.placed[1]!.position[0]).toBeCloseTo(0.066 + 0.04, 6);
  });
});

describe("checkStability", () => {
  const solved = (d: IdeaDraft) => { const r = solve(d, kit); if (!r.ok) throw new Error(r.reason); return r.placed; };
  it("passes the three-can riser, even with a laptop on it", () =>
    expect(checkStability(solved(riser), kit, vocab, { label: "laptop", size_cm: [31, 1.6, 22], kg: 1.6 })).toEqual({ ok: true }));
  it("fails a board resting on one can at its edge, and says by how much", () => {
    const r = checkStability(solved(draft([step({ place: "o1" }), step({ place: "o4", orientation: "flat", on: ["o1"], at_cm: { x: 15, z: 0 } })])), kit, vocab, null);
    expect(r).toMatchObject({ ok: false });
    expect(!r.ok && r.reason).toMatch(/pizza box would tip/);
  });

  it("fails a piece that claims to rest on a can it is nowhere near", () => {
    const r = checkStability(solved(draft([step({ place: "o1" }), step({ place: "o4", orientation: "flat", on: ["o1"], at_cm: { x: 60, z: 0 } })])), kit, vocab, null);
    expect(!r.ok && r.reason).toMatch(/pizza box would tip: its weight lands well/);
  });

  // People set each piece down about a centimetre off, and the errors add up with height. A perfect-placement check
  // passes any tower of cans; this one allows three and refuses four.
  const tower = (n: number) => {
    const cans = new Map(Array.from({ length: n }, (_, k) => std("tall_can", `o${k + 1}`)).map((t) => [t.twin_id, t]));
    const d = draft(Array.from({ length: n }, (_, k) => step({ place: `o${k + 1}`, on: k === 0 ? [] : [`o${k}`] })));
    const r = solve(d, cans);
    if (!r.ok) throw new Error(r.reason);
    return checkStability(r.placed, cans, vocab, null);
  };
  it("allows a tower of three cans", () => expect(tower(3)).toEqual({ ok: true }));
  it("refuses a tower of four: each level adds a centimetre of placement error to what the bottom can must absorb", () => {
    const r = tower(4);
    expect(r).toMatchObject({ ok: false });
    expect(!r.ok && r.reason).toMatch(/tall can is only 3\.\d cm from tipping; it needs 4\.0 cm/);
  });

  // The ideas model is told: "at least 3 supports that are not in a line, or one support at least as wide". A pizza box
  // centred on one can balances on paper, and falls the moment a judge touches its edge.
  it("refuses a wide board balanced on one can, and on two cans in a line, and says why", () => {
    const onOne = checkStability(solved(draft([step({ place: "o1" }), step({ place: "o4", orientation: "flat", on: ["o1"] })])), kit, vocab, null);
    expect(!onOne.ok && onOne.reason).toMatch(/pizza box overhangs what holds it up: its supports span 6\.6 cm of its 35\.0 cm/);
    const inLine = checkStability(solved(draft([
      step({ place: "o1", at_cm: { x: -12, z: 0 } }), step({ place: "o2", at_cm: { x: 12, z: 0 } }), step({ place: "o4", orientation: "flat", on: ["o1", "o2"] }),
    ])), kit, vocab, null);
    expect(inLine).toMatchObject({ ok: false });
  });
  it("lets a can stand on a can: a support as wide as what rests on it", () => {
    const two = new Map([std("tall_can", "o1"), std("tall_can", "o2")].map((t) => [t.twin_id, t]));
    const r = solve(draft([step({ place: "o1" }), step({ place: "o2", on: ["o1"] })]), two);
    expect(r.ok && checkStability(r.placed, two, vocab, null)).toEqual({ ok: true });
  });

  it("asks for more margin under an object whose size is only roughly known", () => {
    // The same stand twice: a board on one cardboard box 20 cm wide. Measured to 3 mm it stands; cut off by the photo's edge (±11 cm) it may not.
    const stand = (error_m: number) => {
      const base = { ...std("cardboard_box", "o1"), shape: { type: "box" as const, size: [0.2, 0.2, 0.2] as [number, number, number] }, error_m };
      const things = new Map([base, std("pizza_box", "o2")].map((t) => [t.twin_id, t]));
      const r = solve(draft([step({ place: "o1" }), step({ place: "o2", orientation: "flat", on: ["o1"] })]), things);
      if (!r.ok) throw new Error(r.reason);
      return checkStability(r.placed, things, vocab, null);
    };
    expect(stand(0.003)).toEqual({ ok: true });
    expect(stand(0.11)).toMatchObject({ ok: false });
  });
});

describe("tape", () => {
  const box20 = { ...std("cardboard_box", "o5"), shape: { type: "box" as const, size: [0.2, 0.12, 0.2] as [number, number, number] } };
  const kitWithBox = new Map([std("tall_can", "o1"), std("tall_can", "o2"), std("tall_can", "o3"), std("tall_can", "o4"), box20].map((t) => [t.twin_id, t]));
  const post = (tape: string[]) => draft([step({ place: "o1" }), step({ place: "o5", orientation: "flat", on: ["o1"], taped_to: tape })]);
  const stands = (d: IdeaDraft, things = kitWithBox) => {
    const r = solve(d, things, { tape: true });
    if (!r.ok) throw new Error(r.reason);
    return checkStability(r.placed, things, vocab, null);
  };

  it("holds a box on one can (a birdhouse on a post); untaped, the same box only balances, and is refused", () => {
    expect(stands(post(["o1"]))).toEqual({ ok: true });
    expect(stands(post([]))).toMatchObject({ ok: false, reason: expect.stringMatching(/cardboard box overhangs what holds it up/) });
  });

  it("does not stop a tall, narrow taped stack from tipping as a whole", () => {
    const tower = draft(["o1", "o2", "o3", "o4"].map((id, k) => step({ place: id, on: k ? [`o${k}`] : [], taped_to: k ? [`o${k}`] : [] })));
    const r = stands(tower);
    expect(r).toMatchObject({ ok: false });
    expect(!r.ok && r.reason).toMatch(/^the taped tall cans would tip over at a 6° lean; a taped stack needs 7°$/);
  });

  it("holds a box, not a brick: too much weight on a taped joint is refused", () => {
    const base = { ...std("cardboard_box", "o6"), shape: { type: "box" as const, size: [0.3, 0.1, 0.3] as [number, number, number] } };
    const brick = twin({ twin_id: "o7", name: "other", label: "ceramic block", material: "ceramic", load_bearing: true, error_m: 0.003, shape: { type: "box", size: [0.15, 0.15, 0.15] } });
    const things = new Map([base, brick].map((t) => [t.twin_id, t]));
    const r = stands(draft([step({ place: "o6", orientation: "flat" }), step({ place: "o7", on: ["o6"], taped_to: ["o6"] })]), things);
    expect(!r.ok && r.reason).toMatch(/^tape cannot hold the ceramic block in place: it weighs 5\.1 kg, over 1\.5 kg$/);
  });

  it("needs a roll on the table, a piece placed before, and pieces that touch", () => {
    expect(solve(post(["o1"]), kitWithBox)).toEqual({ ok: false, reason: "there is no tape on the table to tape the cardboard box with" });
    expect(solve(draft([step({ place: "o1", taped_to: ["o5"] }), step({ place: "o5", orientation: "flat", on: ["o1"] })]), kitWithBox, { tape: true }))
      .toEqual({ ok: false, reason: "the tall can is taped to o5, which is not placed before it" });
    const apart = draft([step({ place: "o1", at_cm: { x: -10, z: 0 } }), step({ place: "o2", at_cm: { x: 10, z: 0 }, taped_to: ["o1"] })]);
    expect(solve(apart, kitWithBox, { tape: true })).toEqual({ ok: false, reason: "the tall can does not touch the tall can, so tape cannot join them" });
  });

  it("joins only pieces that really touch: two cans corner to corner, 3.3 cm apart, do not", () => {
    const diagonal = draft([step({ place: "o1", at_cm: { x: 0, z: 0 } }), step({ place: "o2", at_cm: { x: 7, z: 7 }, taped_to: ["o1"] })]);
    expect(solve(diagonal, kitWithBox, { tape: true })).toEqual({ ok: false, reason: "the tall can does not touch the tall can, so tape cannot join them" });
    const side = draft([step({ place: "o1", at_cm: { x: 0, z: 0 } }), step({ place: "o2", at_cm: { x: 6.8, z: 0 }, taped_to: ["o1"] })]);
    expect(solve(side, kitWithBox, { tape: true })).toMatchObject({ ok: true });
  });

  it("does not make a loose joint hold: a box loose on one can, or on two in a line, falls whatever is taped on top", () => {
    const pile = new Map([std("tall_can", "o1"), std("tall_can", "o2"), std("pizza_box", "o4"), std("drink_can", "o6")].map((t) => [t.twin_id, t]));
    const onOne = draft([step({ place: "o1" }), step({ place: "o4", orientation: "flat", on: ["o1"] }), step({ place: "o6", on: ["o4"], taped_to: ["o4"] })]);
    expect(stands(onOne, pile)).toMatchObject({ ok: false, reason: expect.stringMatching(/^the pizza box overhangs what holds it up/) });
    const inLine = draft([step({ place: "o1", at_cm: { x: -10, z: 0 } }), step({ place: "o2", at_cm: { x: 10, z: 0 } }),
      step({ place: "o4", orientation: "flat", on: ["o1", "o2"] }), step({ place: "o6", on: ["o4"], taped_to: ["o4"] })]);
    expect(stands(inLine, pile)).toMatchObject({ ok: false, reason: expect.stringMatching(/^the pizza box overhangs what holds it up/) });
  });

  it("counts what rests on a taped piece as held by the tape: a laptop loose on a box taped to one can is too much", () => {
    const pile = new Map([std("tall_can", "o1"), std("pizza_box", "o4"), std("laptop", "o7")].map((t) => [t.twin_id, t]));
    const r = stands(draft([step({ place: "o1" }), step({ place: "o4", orientation: "flat", on: ["o1"], taped_to: ["o1"] }), step({ place: "o7", orientation: "flat", on: ["o4"] })]), pile);
    expect(!r.ok && r.reason).toBe("tape cannot hold the pizza box in place: it weighs 1.8 kg with what rests on it, over 1.5 kg");
  });
});
