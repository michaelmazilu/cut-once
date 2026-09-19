import { partAabb, union, type Aabb, type BaseVisual, type PartVisual } from "@cutonce/project-model";
import type { BuildIdea, Part, Plan, Surface, Twin } from "@cutonce/schemas";

/**
 * Build mode's view on the laptop, as the headset draws it (Device/Build/TwinOverlay.cs and IdeaPreviews.cs): the table,
 * the objects the scan found, and up to three scaled-down designs floating above the pile, left to right as Kit reads
 * them out. It is one Plan so the ordinary HologramView draws it, picks it and frames it; the part ids say what each
 * piece is. The look is separate (sceneVisuals): pointing at a design must only recolour it, never rebuild the scene
 * (which would also move the camera). Pure: no DOM, no three.js.
 *
 * One difference from the headset, on purpose: there the objects are real and seen through passthrough, so only their
 * outline is drawn. A laptop has no passthrough, so they are drawn solid (the palette's "built" look) or there would
 * be nothing to see.
 */
export const PREVIEW = { scale: 0.25, spacing: 0.4, lift: 0.3, maxShown: 3 } as const;
/**
 * The headset's quarter scale is right at arm's reach in a 96° view that wraps round your eyes; the same 90° view on a
 * flat laptop screen makes a quarter-scale design a speck. /sim draws them at half scale, spaced to match.
 */
export const LAPTOP_PREVIEW = { ...PREVIEW, scale: 0.5, spacing: 0.55 } as const;

export interface BuildScene {
  plan: Plan;
  /** Each part's look before anything is pointed at or talked about. */
  base: Record<string, BaseVisual>;
  /** Preview part → the idea it belongs to. */
  ideaOf: Record<string, string>;
  /** Object part → its twin id (o1, o2…), as Kit's highlight_twins names it. */
  twinOf: Record<string, string>;
  /** Part → what to call it on the HUD: an object's name, or a design's title. */
  label: Record<string, string>;
  /** What the camera should frame: the objects and the designs, not the whole table (a big table makes them specks). */
  focus: Aabb | null;
}

type Vec3 = [number, number, number];
type Quat = [number, number, number, number];

/** A turn about +Y, as the server writes yaw_deg: it turns local +X onto (cos θ, 0, −sin θ) (build/label.ts, build/site.ts). */
export const yawQuat = (deg: number): Quat => {
  const half = (deg * Math.PI) / 360;
  return [0, Math.sin(half), 0, Math.cos(half)];
};

const idPart = (s: string) => s.toLowerCase().replace(/[^a-z0-9_]/g, "_");

function scenePart(part_id: string, name: string, shape: Part["shape"], position: Vec3, rotation_quat?: Quat): Part {
  return {
    part_id, name, aliases: [], kind: "scene", layer: "scene", shape, position, ...(rotation_quat ? { rotation_quat } : {}),
    material_id: "mat_scene", step_id: "step_scene", rests_on: [], attaches_to: [], verify_hint: "", install_minutes: 0, doc_refs: [],
  };
}

const named = (t: Twin) => Boolean(t.name) && t.name !== "unknown";

/** Where the pile is: the middle of the named objects, or of all of them when none is named yet (TwinOverlay.TryGetCentre). */
export function pileCentre(twins: Twin[]): Vec3 | null {
  const use = twins.some(named) ? twins.filter(named) : twins;
  if (use.length === 0) return null;
  const sum = use.reduce<Vec3>((s, t) => [s[0] + t.position[0], s[1] + t.position[1], s[2] + t.position[2]], [0, 0, 0]);
  return [sum[0] / use.length, sum[1] / use.length, sum[2] / use.length];
}

function scaled(shape: Part["shape"], k: number): Part["shape"] | null {
  switch (shape.type) {
    case "box": return { type: "box", size: [shape.size[0] * k, shape.size[1] * k, shape.size[2] * k] };
    case "cylinder": return { ...shape, diameter: shape.diameter * k, length: shape.length * k };
    case "polyline": return { ...shape, diameter: shape.diameter * k, points: shape.points.map((p) => [p[0] * k, p[1] * k, p[2] * k] as Vec3) };
    default: return null;                                          // a build design is boxes and cylinders; a mesh has no size to shrink here
  }
}

/**
 * The scene for one moment of build mode. /sim's camera stands on the +Z side looking toward −Z, so the viewer's right is
 * +X: the designs are laid out along +X, and the first one Kit names is the leftmost.
 */
export function buildScene(
  runPlan: Plan, surfaces: Surface[], twins: Twin[], ideas: BuildIdea[],
  look: { scale: number; spacing: number; lift: number; maxShown: number } = PREVIEW,
): BuildScene {
  const parts: Part[] = [];
  const base: Record<string, BaseVisual> = {};
  const ideaOf: Record<string, string> = {};
  const twinOf: Record<string, string> = {};
  const label: Record<string, string> = {};

  // The table (or shelf) the objects stand on, faint, in its own outline.
  const under = new Set(twins.map((t) => t.sits_on).filter((s): s is string => Boolean(s)));
  for (const s of surfaces) {
    if (!under.has(s.surface_id) || s.kind === "floor") continue;
    const id = `part_scene_${idPart(s.surface_id)}`;
    const rect = s.rect ?? { centre: [(s.min[0] + s.max[0]) / 2, (s.min[1] + s.max[1]) / 2] as [number, number], len: s.max[0] - s.min[0], wid: s.max[1] - s.min[1], yaw_deg: 0 };
    parts.push(scenePart(id, s.kind, { type: "box", size: [rect.len, 0.02, rect.wid] }, [rect.centre[0], s.y - 0.01, rect.centre[1]], yawQuat(rect.yaw_deg)));
    base[id] = "FUTURE";
    label[id] = s.kind === "table" ? "The table" : s.kind;
  }

  // The objects the scan found.
  for (const t of twins) {
    const id = `part_twin_${idPart(t.twin_id)}`;
    const shape: Part["shape"] = t.shape.type === "box" ? { type: "box", size: t.shape.size } : { ...t.shape };
    parts.push(scenePart(id, t.label || t.name, shape, t.position, t.yaw_deg ? yawQuat(t.yaw_deg) : undefined));
    base[id] = named(t) ? "BUILT_REPLAY" : "FUTURE";
    twinOf[id] = t.twin_id;
    label[id] = t.label || t.name;
  }

  // Up to three designs above the pile, scaled down, each standing on its own footprint.
  const centre = pileCentre(twins);
  const shown = ideas.slice(0, look.maxShown);
  shown.forEach((idea, i) => {
    const pieces = idea.plan.parts.filter((p) => p.part_id !== "part_surface");  // the build area is the table, not the design
    const box = union(pieces.map(partAabb).filter((b): b is NonNullable<ReturnType<typeof partAabb>> => b !== null));
    if (!box) return;
    const foot: Vec3 = [(box.min[0] + box.max[0]) / 2, box.min[1], (box.min[2] + box.max[2]) / 2];
    const anchor: Vec3 = centre
      ? [centre[0] + (i - (shown.length - 1) / 2) * look.spacing, centre[1] + look.lift, centre[2]]
      : [(i - (shown.length - 1) / 2) * look.spacing, 1.1, 0];                  // no objects (a design list with no scan here): table height
    const k = look.scale;
    for (const p of pieces) {
      const shape = scaled(p.shape, k);
      if (!shape) continue;
      const id = `part_idea${i}_${idPart(p.part_id.replace(/^part_/, ""))}`;
      const at: Vec3 = [anchor[0] + (p.position[0] - foot[0]) * k, anchor[1] + (p.position[1] - foot[1]) * k, anchor[2] + (p.position[2] - foot[2]) * k];
      parts.push(scenePart(id, `${idea.title}: ${p.name}`, shape, at, p.rotation_quat as Quat | undefined));
      base[id] = "CURRENT_STEP";
      ideaOf[id] = idea.idea_id;
      label[id] = idea.title;
    }
  });

  const plan: Plan = {
    ...runPlan, plan_id: "plan_sim_build_scene", name: "Build mode", parts, steps: [], materials: [], markers: [], touch_points: [], layers: ["scene"],
  };
  const focus = union(parts.filter((p) => !p.part_id.startsWith("part_scene_")).map(partAabb).filter((b): b is Aabb => b !== null));
  return { plan, base, ideaOf, twinOf, label, focus };
}

/** The look of a scene: the design under the pointer and the objects Kit is talking about light up. */
export function sceneVisuals(scene: BuildScene, opts: { hoveredIdea?: string | null; highlightTwins?: string[] } = {}): Record<string, PartVisual> {
  const lit = new Set(opts.highlightTwins ?? []);
  const out: Record<string, PartVisual> = {};
  for (const [id, look] of Object.entries(scene.base)) {
    const on = (opts.hoveredIdea && scene.ideaOf[id] === opts.hoveredIdea) || (scene.twinOf[id] !== undefined && lit.has(scene.twinOf[id]!));
    out[id] = { base: look, modifiers: on ? ["HIGHLIGHTED"] : [] };
  }
  return out;
}
