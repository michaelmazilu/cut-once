import { describe, expect, it } from "vitest";
import type { Vec3 } from "@cutonce/schemas";
import type { KitBuildContext } from "../src/build/session.js";
import { decideKit, kitContextText, pickByPosition, whereFrom, type KitTurn } from "../src/copilot/kit.js";
import { twin } from "./build-synth.js";

const turn = (over: Partial<KitTurn> = {}): KitTurn => ({ heard: "something", intent: "question", wish: null, pick: null, answer: "An answer.", objects: [], confidence: 0.9, ...over });
const ideas = [{ idea_id: "idea_a", title: "Birdhouse" }, { idea_id: "idea_b", title: "Robot" }, { idea_id: "idea_c", title: "Can tower" }];
const at = (over: object = {}) => ({
  canRethink: true, building: false, ideas,
  byName: (said: string) => ideas.find((i) => said.toLowerCase().includes(i.title.toLowerCase())) ?? null, ...over,
});

describe("decideKit", () => {
  it("speaks the answer to a question, and flags it when Kit was unsure what was asked", () => {
    expect(decideKit(turn(), at())).toEqual({ kind: "say", text: "An answer.", clarify: false });
    expect(decideKit(turn({ confidence: 0.3 }), at())).toEqual({ kind: "say", text: "An answer.", clarify: true });
    expect(decideKit(turn({ answer: " " }), at())).toMatchObject({ kind: "say", text: "I don't have an answer for that. Try asking another way." });
  });

  it("rethinks the objects already on the table for a wish, and scans for a plain ask or when nothing is known yet", () => {
    expect(decideKit(turn({ intent: "ideas", wish: "a birdhouse", answer: "" }), at())).toEqual({ kind: "rethink", wish: "a birdhouse", text: "Let me see how to make a birdhouse from what's here." });
    expect(decideKit(turn({ intent: "ideas", wish: null, answer: "" }), at())).toEqual({ kind: "scan", wish: null, text: "Let me see what you've got." });
    expect(decideKit(turn({ intent: "ideas", wish: "a birdhouse", answer: "On it!" }), at({ canRethink: false }))).toEqual({ kind: "scan", wish: "a birdhouse", text: "On it!" });
  });

  it("mid-build, a change looks again with the change as the wish (the objects have moved)", () => {
    expect(decideKit(turn({ intent: "change", wish: "something crazier", answer: "" }), at({ canRethink: false, building: true })))
      .toEqual({ kind: "scan", wish: "something crazier", text: "Let me look again with that in mind." });
    expect(decideKit(turn({ intent: "change", wish: null, heard: "make it taller" }), at())).toMatchObject({ kind: "rethink", wish: "make it taller" });
  });

  it("picks a design by the model's id, by its name, or by where it stands", () => {
    expect(decideKit(turn({ intent: "pick", pick: "idea_b" }), at())).toEqual({ kind: "start", ideaId: "idea_b", title: "Robot" });
    expect(decideKit(turn({ intent: "pick", pick: "idea_zzz", heard: "the can tower please" }), at())).toMatchObject({ kind: "start", ideaId: "idea_c" });
    expect(decideKit(turn({ intent: "pick", pick: null, heard: "the one on the left" }), at())).toMatchObject({ kind: "start", ideaId: "idea_a" });
    expect(decideKit(turn({ intent: "pick", pick: null, heard: "that one" }), at())).toMatchObject({ kind: "say", clarify: true });
  });

  it("never picks while a build is under way", () =>
    expect(decideKit(turn({ intent: "pick", pick: "idea_a" }), at({ building: true }))).toMatchObject({ kind: "say", clarify: true }));

  it("marks a step done, or undoes, only when sure and never on a question", () => {
    expect(decideKit(turn({ intent: "done", heard: "I've put the can in" }), at())).toEqual({ kind: "command", phrase: "done" });
    expect(decideKit(turn({ intent: "done", heard: "is it done?" }), at())).toMatchObject({ kind: "say", clarify: true });
    expect(decideKit(turn({ intent: "undo", confidence: 0.7 }), at())).toMatchObject({ kind: "say", clarify: true });
  });

  it("turns steps as the voice commands do", () => {
    expect(decideKit(turn({ intent: "next", confidence: 0.65 }), at())).toEqual({ kind: "command", phrase: "next" });
    expect(decideKit(turn({ intent: "back" }), at())).toEqual({ kind: "command", phrase: "back" });
  });

  it("asks back when it cannot tell, or is unsure of an action", () => {
    expect(decideKit(turn({ intent: "unclear", answer: "Do you want a design, or to know about this step?" }), at())).toEqual({ kind: "say", text: "Do you want a design, or to know about this step?", clarify: true });
    expect(decideKit(turn({ intent: "ideas", confidence: 0.4, answer: "" }), at())).toEqual({ kind: "say", text: "Sorry, what would you like to do?", clarify: true });
  });
});

describe("pickByPosition", () => {
  it("reads left, middle, right and first to third, as the headset lays the designs out", () => {
    expect(["the left one", "the middle one", "the right one", "the second one", "the third", "the last one"].map((s) => pickByPosition(s, ideas)?.idea_id))
      .toEqual(["idea_a", "idea_b", "idea_c", "idea_b", "idea_c", "idea_c"]);
  });
  it("has no middle of two, and nothing to pick from none", () => {
    expect(pickByPosition("the middle one", ideas.slice(0, 2))).toBeNull();
    expect(pickByPosition("the left one", [])).toBeNull();
  });
});

describe("whereFrom", () => {
  // Right-handed, +Y up, the camera facing +z: its right is -x (the headset's mirrored X).
  const cam = { position: [0, 1.6, -1] as Vec3, forward: [0, -0.5, 1] as Vec3 };
  it("says left and right from where the camera faced, and how far away", () => {
    expect(whereFrom(cam, [-0.3, 0.8, 0.5])).toBe("30 cm to your right, 1.7 m away");
    expect(whereFrom(cam, [0.25, 0.8, 0.5])).toBe("25 cm to your left, 1.7 m away");
    expect(whereFrom(cam, [0.05, 0.8, 0.5])).toBe("in front of you, 1.7 m away");
  });
});

describe("kitContextText", () => {
  const context = (over: Partial<KitBuildContext> = {}): KitBuildContext => ({
    status: "showing 3 designs", surfaces: [{ surface_id: "s1", kind: "table", y: 0.74, min: [-1, 0], max: [1, 1], points: 900 }],
    twins: [
      twin({ twin_id: "o1", name: "tall_can", label: "tall can", position: [-0.3, 0.82, 0.5], shape: { type: "cylinder", axis: "y", diameter: 0.066, length: 0.157 } }),
      twin({ twin_id: "o2", name: "unknown" }),
    ],
    camera: { position: [0, 1.6, -1], forward: [0, -0.5, 1] }, wish: "a birdhouse", started: null, tape: true,
    ideas: [{ idea_id: "idea_a", title: "Birdhouse", why: "Birds need homes.", uses: ["o1"], steps: 2 }], ...over,
  });

  it("gives the objects with where they are, the tools, the wish, the designs left to right, and what was said", () => {
    const text = kitContextText(context(), null, "which one is easiest?", [{ transcript: "what can I build", answer_text: "Let me see." }]);
    expect(text).toContain("STATUS: showing 3 designs");
    expect(text).toContain("  o1 tall can: cylinder 6.6 cm wide, 15.7 cm tall; metal; holds weight; on the table, 30 cm to your right, 1.7 m away");
    expect(text).toContain("  and 1 object not named yet");
    expect(text).toContain("TOOLS: tape (a roll is on the table)");
    expect(text).toContain('WISH: "a birdhouse"');
    expect(text).toContain('  1. idea_a "Birdhouse": uses o1; 2 steps. Why: Birds need homes.');
    expect(text).toContain("BUILDING: nothing yet");
    expect(text).toContain("  they said: what can I build\n  you said: Let me see.");
    expect(text.endsWith('SAID: "which one is easiest?"')).toBe(true);
  });

  it("says the words are in the audio on OMNI, and gives the build in progress", () => {
    const text = kitContextText(context({ ideas: [], wish: null, tape: false }), { title: "Birdhouse", step: { index: 2, instruction: "Lay the box on the can." }, of: 3, next: "Tape the box to the can." }, null, []);
    expect(text).toContain('BUILDING: "Birdhouse", step 2 of 3: Lay the box on the can. Next: Tape the box to the can.');
    expect(text).toContain("TOOLS: none");
    expect(text).toContain("  none");
    expect(text.endsWith("SAID: (in the audio)")).toBe(true);
  });
});
