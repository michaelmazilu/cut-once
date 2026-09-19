import { mkdtempSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it, vi } from "vitest";
import type { BuildIdea, Surface, Twin } from "@cutonce/schemas";
import { REPO_ROOT, loadConfig } from "../src/config.js";
import { loadRules, loadVocab, standardShape } from "../src/build/data.js";
import { canonical, computeIdeas, describeFound, hasTape, summary } from "../src/build/ideas.js";
import { twin } from "./build-synth.js";

const vocab = loadVocab(REPO_ROOT), rules = loadRules(REPO_ROOT, vocab);
const std = (id: string, name: string, x: number): Twin => {
  const item = vocab.get(name)!;
  return twin({ twin_id: id, name, label: item.label, shape: standardShape(item)!, material: item.material, load_bearing: item.load_bearing, error_m: 0.003, snapped: true, position: [x, 0.8, 0.5] });
};
const pile = [std("o1", "tall_can", 0.1), std("o2", "tall_can", 0.2), std("o3", "tall_can", 0.3), std("o4", "pizza_box", -0.2)];
const table: Surface = { surface_id: "s1", kind: "table", y: 0.74, min: [-0.6, 0.2], max: [0.6, 1.0], points: 900 };
const aiDraft = { title: "Can tower", why: "One can on another.", tools: [], uses: ["o1"], steps: [{ place: "o1", orientation: "upright" as const, on: [], at_cm: null, next_to: null, side: null, gap_cm: null }] };
const phoneDraft = { ...aiDraft, title: "Phone stand" };
const deps = (over: object = {}) => ({
  cfg: loadConfig({}, { openaiKey: "k" }), vocab, rules, model: "m", cacheDir: mkdtempSync(join(tmpdir(), "ideas-")), timeoutMs: 1000, liveMs: 1000,
  log: { warn: vi.fn() }, call: vi.fn(async (_cfg: unknown, _req: unknown) => ({ ideas: [aiDraft] })), ...over,
});
const input = { sessionId: "bsess_t", twins: pile, surfaces: [table], camera: [0, 1.6, -1] as [number, number, number], photo: null, request: null };
/** A model that answers after `ms`. */
const slow = (ms: number, ideas: object[]) => vi.fn(() => new Promise((resolve) => setTimeout(() => resolve({ ideas }), ms)));

describe("computeIdeas: Kit's designs, live first", () => {
  it("asks the design model and offers its designs, made live, with no stored rule mixed in, in one final list", async () => {
    const emitted: { ideas: BuildIdea[]; final: boolean }[] = [];
    const out = await computeIdeas(deps(), input, (ideas, final) => emitted.push({ ideas, final }));
    expect(emitted.map((e) => e.final)).toEqual([true]);
    expect(out.map((i) => [i.title, i.source, i.made])).toEqual([["Can tower", "ai", "live"]]);
    expect(out[0]!.origin.position[1]).toBeCloseTo(0.74);
    expect(out[0]!.twin_of).toMatchObject({ part_o1: "o1" });
  });

  it("repairs a design that fails a check once, telling the model why", async () => {
    const bad = { ...aiDraft, title: "Rolling can", steps: [{ ...aiDraft.steps[0]!, orientation: "on_side" as const }] };
    const call = vi.fn().mockResolvedValueOnce({ ideas: [bad] }).mockResolvedValueOnce({ ideas: [aiDraft] });
    const out = await computeIdeas(deps({ call }), input, () => {});
    expect(call).toHaveBeenCalledTimes(2);
    expect((call.mock.calls[1]![1] as { text: string }).text).toMatch(/would roll on its side/);
    expect(out.map((i) => i.title)).toEqual(["Can tower"]);
  });

  it("says on the HUD how many designs it checked and how many stand up", async () => {
    const bad = { ...aiDraft, title: "Rolling can", steps: [{ ...aiDraft.steps[0]!, orientation: "on_side" as const }] };
    const note = vi.fn();
    await computeIdeas(deps({ call: vi.fn().mockResolvedValueOnce({ ideas: [aiDraft, bad] }).mockResolvedValueOnce({ ideas: [] }), note }), input, () => {});
    expect(note).toHaveBeenCalledWith("Checked 2 designs: 1 stands up.");
  });

  it("keeps the designs that passed when the repair call fails", async () => {
    const bad = { ...aiDraft, title: "Rolling can", steps: [{ ...aiDraft.steps[0]!, orientation: "on_side" as const }] };
    const d = deps({ call: vi.fn().mockResolvedValueOnce({ ideas: [aiDraft, bad] }).mockRejectedValueOnce(new Error("Request timed out.")) });
    const out = await computeIdeas(d, input, () => {});
    expect(out.map((i) => i.title)).toEqual(["Can tower"]);
    expect(d.log.warn.mock.calls[0]![1]).toMatch(/repair/);
  });

  it("still offers the live designs when they cannot be cached (a full disk)", async () => {
    const notAFolder = join(mkdtempSync(join(tmpdir(), "ideas-")), "file");
    writeFileSync(notAFolder, "x");
    expect((await computeIdeas(deps({ cacheDir: join(notAFolder, "cache") }), input, () => {})).map((i) => i.title)).toEqual(["Can tower"]);
  });
});

describe("computeIdeas: the rehearsal cache", () => {
  it("shows the designs saved for the same objects and wish when the live answer is late; the late answer only refreshes the cache", async () => {
    const d = deps();
    await computeIdeas(d, { ...input, request: "a birdhouse" }, () => {});                       // rehearsal: a live answer, cached
    const later: Promise<unknown>[] = [];
    // On stage: the same objects (scanned again, so new ids), the same wish, and a model that answers after the deadline.
    const latePhone = { ...phoneDraft, uses: ["o11"], steps: [{ ...aiDraft.steps[0]!, place: "o11" }] };        // the late answer speaks of today's ids
    const stage = { ...deps({ cacheDir: d.cacheDir, liveMs: 30, call: slow(200, [latePhone]) }), background: (w: Promise<unknown>) => later.push(w) };
    const shown = await computeIdeas(stage, { ...input, request: "a birdhouse", twins: pile.map((t, k) => ({ ...t, twin_id: `o${k + 11}` })) }, () => {});
    expect(shown.map((i) => [i.title, i.made])).toEqual([["Can tower", "cache"]]);
    expect(shown[0]!.twin_of).toMatchObject({ part_o11: "o11" });                               // mapped onto today's twins
    expect(later).toHaveLength(1);
    await Promise.all(later);
    const [file] = readdirSync(d.cacheDir);
    expect(JSON.parse(readFileSync(join(d.cacheDir, file!), "utf8")).drafts.map((x: { title: string }) => x.title)).toEqual(["Phone stand"]);
  });

  it("waits past the deadline for the live answer when nothing is cached", async () => {
    const out = await computeIdeas(deps({ liveMs: 20, call: slow(120, [aiDraft]) }), input, () => {});
    expect(out.map((i) => [i.title, i.made])).toEqual([["Can tower", "live"]]);
  });

  it("keeps waiting for the live answer when the cache holds only designs already offered", async () => {
    const d = deps();
    await computeIdeas(d, input, () => {});                                        // rehearsal: caches the can tower
    const late = deps({ cacheDir: d.cacheDir, liveMs: 20, call: slow(120, [phoneDraft]) });
    expect((await computeIdeas(late, { ...input, offered: ["Can tower"] }, () => {})).map((i) => [i.title, i.made])).toEqual([["Phone stand", "live"]]);
  });

  it("finds the designs cached at rehearsal whichever model asks now, or with no model at all", async () => {
    const d = deps({ model: "qwen3.5-omni-flash" });
    await computeIdeas(d, input, () => {});
    const down = vi.fn(async () => { throw new Error("connect ETIMEDOUT"); });
    expect((await computeIdeas({ ...d, model: "gpt-5.6-luna", call: down }, input, () => {})).map((i) => i.made)).toEqual(["cache"]);
    expect((await computeIdeas({ ...d, model: "none", call: null }, input, () => {})).map((i) => i.made)).toEqual(["cache"]);
  });

  it("uses the cache when the live call fails, and the stored rules when there is no cache either", async () => {
    const d = deps();
    await computeIdeas(d, input, () => {});
    const failing = vi.fn(async () => { throw new Error("connect ETIMEDOUT"); });
    expect((await computeIdeas({ ...d, call: failing }, input, () => {})).map((i) => i.made)).toEqual(["cache"]);
    expect((await computeIdeas(deps({ call: failing }), input, () => {})).map((i) => [i.title, i.made])).toEqual([["Laptop riser", "rule"]]);
  });

  it("with no model at all, uses the cache, then the stored rules", async () => {
    const d = deps();
    await computeIdeas(d, input, () => {});
    expect((await computeIdeas({ ...d, call: null }, input, () => {})).map((i) => i.made)).toEqual(["cache"]);
    expect((await computeIdeas(deps({ call: null }), input, () => {})).map((i) => [i.source, i.made])).toEqual([["rule", "rule"]]);
  });

  it("keeps each wish's designs apart: a rethink's never become the plain answer, nor the other way round", async () => {
    const d = deps({ call: vi.fn().mockResolvedValueOnce({ ideas: [phoneDraft] }).mockResolvedValue({ ideas: [aiDraft] }) });
    await computeIdeas(d, { ...input, request: "something for my phone" }, () => {});
    const failing = { ...d, call: vi.fn(async () => { throw new Error("down"); }) };
    expect((await computeIdeas(failing, input, () => {})).map((i) => i.made)).toEqual(["rule"]);          // nothing cached for the plain ask
    expect((await computeIdeas(failing, { ...input, request: "Something for my phone!" }, () => {})).map((i) => i.title)).toEqual(["Phone stand"]);
  });
});

describe("computeIdeas: no repeats", () => {
  // Which titles count as offered is the session's call (a change that names one brings it back): here it is only obeyed.
  it("never offers again what it is told was offered, whatever the request says", async () => {
    const d = deps({ call: vi.fn(async () => ({ ideas: [aiDraft, phoneDraft] })) });
    expect((await computeIdeas(d, { ...input, offered: ["Can tower"] }, () => {})).map((i) => i.title)).toEqual(["Phone stand"]);
    expect((await computeIdeas(d, { ...input, offered: ["Can tower"], request: "a can tower" }, () => {})).map((i) => i.title)).toEqual(["Phone stand"]);
  });
  it("offers a repeat rather than nothing, when every design it can find was offered already", async () => {
    const out = await computeIdeas(deps({ call: vi.fn(async () => ({ ideas: [aiDraft] })) }), { ...input, offered: ["Can tower", "Laptop riser"], request: "make it taller" }, () => {});
    expect(out.map((i) => [i.title, i.made])).toEqual([["Can tower", "live"]]);
  });
  it("tells the model what was already offered, and what the builder asked for", async () => {
    const d = deps();
    await computeIdeas(d, { ...input, offered: ["Birdhouse"], request: "something crazier" }, () => {});
    const { text } = d.call.mock.calls[0]![1] as { text: string };
    expect(text).toContain("Already offered, do not repeat: Birdhouse.");
    expect(text).toContain('The builder asked: "something crazier".');
  });
});

describe("computeIdeas: where the design goes", () => {
  it("puts the design where nothing else stands: an unnamed object beside the pile pushes it to the other side", async () => {
    const d = deps({ call: null });
    // A tight pile in the middle of the table, so the design fits on either side of it.
    const tight = [std("o1", "tall_can", -0.08), std("o2", "tall_can", 0), std("o3", "tall_can", 0.08), std("o4", "pizza_box", 0)].map((t) => ({ ...t, position: [t.position[0], t.position[1], 0.6] as [number, number, number] }));
    const [free] = await computeIdeas(d, { ...input, twins: tight }, () => {});
    const [fx, , fz] = free!.origin.position;
    expect(Math.abs(fx)).toBeGreaterThan(0.3);                      // beside the pile, not on it
    // Something the design does not use (not even named yet) stands exactly where the design would have gone.
    const blocker = twin({ twin_id: "o9", name: "unknown", confidence: 0, shape: { type: "box", size: [0.2, 0.1, 0.2] }, position: [fx, 0.79, fz] });
    const [moved] = await computeIdeas(d, { ...input, twins: [...tight, blocker] }, () => {});
    expect(Math.sign(moved!.origin.position[0])).toBe(-Math.sign(fx));
  });

  it("says so, with no ideas, when nothing in view has a name yet", async () => {
    const emitted: { ideas: BuildIdea[]; final: boolean }[] = [];
    const out = await computeIdeas(deps(), { ...input, twins: pile.map((t) => ({ ...t, name: "unknown", confidence: 0 })) }, (ideas, final) => emitted.push({ ideas, final }));
    expect(out).toEqual([]);
    expect(emitted).toEqual([{ ideas: [], final: true }]);
  });
});

describe("tape in the design prompt", () => {
  it("knows tape by name, and a tape measure is not tape", () => {
    const named = (label: string) => twin({ twin_id: "o9", name: "other", label });
    expect([named("duct tape"), named("masking tape roll"), named("tape measure"), named("measuring tape")].map((t) => hasTape([t]))).toEqual([true, true, false, false]);
  });

  it("tells the model about tape only when a roll is on the table, and designs may use it", async () => {
    const roll = twin({ twin_id: "o9", name: "tape_roll", label: "tape roll", material: "plastic", confidence: 0.9, snapped: true, shape: { type: "cylinder", axis: "y", diameter: 0.11, length: 0.048 }, position: [0.4, 0.764, 0.5] });
    const d = deps();
    await computeIdeas(d, { ...input, twins: [...pile, roll] }, () => {});
    await computeIdeas(d, input, () => {});
    const [withTape, without] = d.call.mock.calls.map((c) => (c[1] as { text: string; system: string }));
    expect(withTape!.text).toContain("TOOLS: tape (a roll is on the table)");
    expect(without!.text).toContain("TOOLS: none");
    expect(withTape!.system).toContain("taped_to");
  });
});

describe("canonical: the cache key", () => {
  const box = (id: string, size: [number, number, number]) => twin({ twin_id: id, name: "cardboard_box", label: "cardboard box", snapped: false, shape: { type: "box", size } });
  it("is the same for the same things measured a little differently, and for new ids", () =>
    expect(canonical([box("o1", [0.204, 0.1, 0.3])], "a birdhouse").key).toBe(canonical([box("o7", [0.212, 0.098, 0.305])], "A birdhouse!").key));
  it("changes with the wish and what the things are", () => {
    const base = canonical([box("o1", [0.2, 0.1, 0.3])], "a birdhouse").key;
    expect(canonical([box("o1", [0.2, 0.1, 0.3])], "a robot").key).not.toBe(base);
    expect(canonical([box("o1", [0.2, 0.1, 0.45])], "a birdhouse").key).not.toBe(base);
    const thermos = twin({ twin_id: "o1", name: "other", label: "thermos" }), vase = twin({ twin_id: "o1", name: "other", label: "vase" });
    expect(canonical([thermos]).key).not.toBe(canonical([vase]).key);
  });
});

describe("summary", () => {
  it("says what it found and what you could build", () =>
    expect(summary(pile, [{ title: "Laptop riser" } as BuildIdea])).toBe("I found three tall cans and a pizza box. You could build a laptop riser."));
  it("lists what it sees for the HUD", () => expect(describeFound(pile)).toBe("three tall cans and a pizza box"));
});
