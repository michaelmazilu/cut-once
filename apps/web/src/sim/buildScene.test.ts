import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { partAabb } from "@cutonce/project-model";
import { Strict, type BuildIdea, type Plan, type Surface, type Twin } from "@cutonce/schemas";
import { PREVIEW, buildScene, pileCentre, sceneVisuals, yawQuat } from "./buildScene";

const REPO = join(import.meta.dirname, "..", "..", "..", "..");
const json = (...p: string[]) => JSON.parse(readFileSync(join(REPO, ...p), "utf8")) as unknown;
// Real data: the synthetic kit's saved names (three tall cans and a pizza box) and the design in the stream fixture.
const twins = (json("data", "build", "recordings", "synthetic_kit", "labels.json") as unknown[]).map((t) => Strict.Twin.parse(t) as Twin);
const idea = Strict.BuildIdea.parse((json("data", "fixtures", "build", "ws_build_ideas.json") as { ideas: unknown[] }).ideas[0]) as BuildIdea;
const base = Strict.Plan.parse(json("data", "fixtures", "plan_desk_archetype.json")) as Plan;
const table: Surface = { surface_id: "s2", kind: "table", y: 0.74, min: [-0.6, 0.1], max: [0.6, 0.9], points: 900 };
const three = [0, 1, 2].map((i) => ({ ...idea, idea_id: `idea_fixture_${i}`, title: `Design ${i + 1}` }));

describe("build mode's scene on /sim", () => {
  it("is a plan the schema accepts, so the ordinary hologram view can draw it", () => {
    const { plan } = buildScene(base, [table], twins, three);
    expect(() => Strict.Plan.parse(plan)).not.toThrow();
    expect(plan.parts.every((p) => /^part_[a-z0-9_]+$/.test(p.part_id))).toBe(true);
  });

  it("draws the table under the pile, each object where the scan found it, and none of the old run's parts", () => {
    const { plan, label } = buildScene(base, [table], twins, []);
    const ids = plan.parts.map((p) => p.part_id);
    expect(ids).toEqual(["part_scene_s2", ...twins.map((t) => `part_twin_${t.twin_id}`)]);
    expect(plan.parts.find((p) => p.part_id === "part_twin_o1")!.position).toEqual(twins[0]!.position);
    expect(label.part_twin_o4).toBe(twins[3]!.label);
  });

  it("turns each object by its yaw as the server does: local +X onto (cos θ, 0, −sin θ)", () => {
    const [x, y, z, w] = yawQuat(90);
    // q·(1,0,0)·q⁻¹ for a turn about Y
    const vx = 1 - 2 * (y * y + z * z), vz = 2 * (x * z - w * y);
    expect(vx).toBeCloseTo(0, 6);
    expect(vz).toBeCloseTo(-1, 6);
    const box = buildScene(base, [table], twins, []).plan.parts.find((p) => p.part_id === "part_twin_o4")!;
    expect(box.rotation_quat).toEqual(yawQuat(twins[3]!.yaw_deg));
  });

  it("floats up to three quarter-scale designs above the pile, first on the left, each a part of its idea", () => {
    const { plan, ideaOf, label } = buildScene(base, [table], twins, [...three, { ...idea, idea_id: "idea_fixture_extra", title: "Fourth" }]);
    const previews = plan.parts.filter((p) => ideaOf[p.part_id]);
    expect(new Set(Object.values(ideaOf))).toEqual(new Set(three.map((i) => i.idea_id)));
    expect(previews.some((p) => p.part_id.includes("surface"))).toBe(false);      // the build area is the table, not the design

    const centre = pileCentre(twins)!;
    const xs = three.map((i) => plan.parts.find((p) => ideaOf[p.part_id] === i.idea_id)!.position[0]);
    expect(xs[0]).toBeLessThan(xs[1]!);
    expect(xs[1]).toBeLessThan(xs[2]!);
    expect(xs[1]).toBeCloseTo(centre[0], 6);

    const can = plan.parts.find((p) => p.part_id === "part_idea0_o1")!;
    const real = idea.plan.parts.find((p) => p.part_id === "part_o1")!;
    expect(can.shape.type === "cylinder" && real.shape.type === "cylinder" && can.shape.length / real.shape.length).toBeCloseTo(PREVIEW.scale, 6);
    expect(partAabb(can)!.min[1]).toBeCloseTo(centre[1] + PREVIEW.lift, 6);           // standing on its own footprint, above the pile
    expect(label.part_idea0_o1).toBe("Design 1");
  });

  it("frames the objects and the designs for the camera, not the whole table", () => {
    const { focus } = buildScene(base, [table], twins, three);
    const centre = pileCentre(twins)!;
    expect(focus!.max[1]).toBeGreaterThan(centre[1] + PREVIEW.lift);                   // the designs above the pile are in view
    expect(focus!.max[0] - focus!.min[0]).toBeLessThan(table.max[0] - table.min[0]);   // the table's far corners are not
  });

  it("lights the design under the pointer and the objects Kit is talking about, without rebuilding the scene", () => {
    const scene = buildScene(base, [table], twins, three);
    const visuals = sceneVisuals(scene, { hoveredIdea: "idea_fixture_1", highlightTwins: ["o2"] });
    for (const [id, owner] of Object.entries(scene.ideaOf)) expect(visuals[id]!.modifiers.includes("HIGHLIGHTED")).toBe(owner === "idea_fixture_1");
    expect(visuals.part_twin_o2!.modifiers).toEqual(["HIGHLIGHTED"]);
    expect(visuals.part_twin_o1!.modifiers).toEqual([]);
    expect(visuals.part_twin_o1!.base).toBe("BUILT_REPLAY");                           // objects are solid: no passthrough on a laptop
    expect(Object.keys(visuals).sort()).toEqual(scene.plan.parts.map((p) => p.part_id).sort());
  });
});
