import type { Surface, Vec3 } from "@cutonce/schemas";
import { onSurface, rectOf } from "./twins.js";

type P2 = [number, number];
export interface Box2 { min: P2; max: P2 }

/**
 * Where the design's frame sits in the room. Its +Z (the front) turns toward the viewer; it goes to the right or
 * left of the pile, then in front of it, wherever it fits on the surface with 2 cm to spare. A spot where something
 * else stands (an object the design does not use) is skipped: a hologram drawn through the sponsor's box is no guide.
 * When none of those is clear (a kitchen counter: too shallow for "in front", and crowded), the clear spot on the
 * surface nearest the pile, turned to face the viewer from there. When nothing is clear anywhere, the spot that
 * covers the least of what stands there.
 * A turn θ about +Y takes design +X to (cos θ, -sin θ) and +Z to (sin θ, cos θ) in the room's (x, z).
 */
export function chooseSite(surface: Surface, pile: Box2, design: { w: number; d: number }, camera: Vec3, obstacles: Box2[] = []):
  { position: Vec3; rotation_quat: [number, number, number, number]; yaw_deg: number } {
  const pc: P2 = [(pile.min[0] + pile.max[0]) / 2, (pile.min[1] + pile.max[1]) / 2];
  const facing = (c: P2) => Math.atan2(camera[0] - c[0], camera[2] - c[1]);
  const axes = (th: number) => ({ right: [Math.cos(th), -Math.sin(th)] as P2, toward: [Math.sin(th), Math.cos(th)] as P2 });
  const theta = facing(pc), { right, toward } = axes(theta);
  const corners: P2[] = [[pile.min[0], pile.min[1]], [pile.max[0], pile.min[1]], [pile.max[0], pile.max[1]], [pile.min[0], pile.max[1]]];
  const reach = (dir: P2) => Math.max(...corners.map((c) => Math.abs((c[0] - pc[0]) * dir[0] + (c[1] - pc[1]) * dir[1])));
  const at = (dir: P2, k: number): P2 => [pc[0] + dir[0] * k, pc[1] + dir[1] * k];
  const gap = 0.05;
  const candidates: P2[] = [
    at(right, reach(right) + design.w / 2 + gap), at(right, -(reach(right) + design.w / 2 + gap)),
    at(toward, reach(toward) + design.d / 2 + gap), pc,
  ];
  const fits = (c: P2, th: number) => {
    const a = axes(th);
    return ([[-1, -1], [1, -1], [1, 1], [-1, 1]] as P2[]).every(([sx, sz]) => {
      const x = c[0] + a.right[0] * sx * (design.w / 2) + a.toward[0] * sz * (design.d / 2);
      const z = c[1] + a.right[1] * sx * (design.w / 2) + a.toward[1] * sz * (design.d / 2);
      return onSurface(surface, x, z, -0.02);               // in the table's own frame: its box along the room's axes has corners the table lacks
    });
  };
  // The turned design's outer bound along the room's axes, against each obstacle's box: how much of them it covers.
  const covered = (c: P2, th: number) => {
    const a = axes(th);
    const reachX = (Math.abs(a.right[0]) * design.w + Math.abs(a.toward[0]) * design.d) / 2;
    const reachZ = (Math.abs(a.right[1]) * design.w + Math.abs(a.toward[1]) * design.d) / 2;
    return obstacles.reduce((sum, o) => sum + Math.max(0, Math.min(c[0] + reachX, o.max[0]) - Math.max(c[0] - reachX, o.min[0]))
      * Math.max(0, Math.min(c[1] + reachZ, o.max[1]) - Math.max(c[1] - reachZ, o.min[1])), 0);
  };
  const clear = (c: P2, th: number) => covered(c, th) === 0;
  const site = (c: P2, th: number) =>
    ({ position: [c[0], surface.y, c[1]] as Vec3, rotation_quat: [0, Math.sin(th / 2), 0, Math.cos(th / 2)] as [number, number, number, number], yaw_deg: (th * 180) / Math.PI });

  const beside = candidates.find((q) => fits(q, theta) && clear(q, theta));
  if (beside) return site(beside, theta);
  let best: { c: P2; th: number; d: number } | null = null, least: { c: P2; th: number; d: number; area: number } | null = null;
  const r = rectOf(surface), t = (r.yawDeg * Math.PI) / 180, cs = Math.cos(t), sn = Math.sin(t), STEP = 0.04;
  for (let u = -r.len / 2; u <= r.len / 2; u += STEP) for (let w = -r.wid / 2; w <= r.wid / 2; w += STEP) {
    const c: P2 = [r.cx + u * cs + w * sn, r.cz - u * sn + w * cs], th = facing(c), d = Math.hypot(c[0] - pc[0], c[1] - pc[1]);
    if (!fits(c, th)) continue;
    const area = covered(c, th);
    if (area === 0 && (!best || d < best.d)) best = { c, th, d };
    if (!least || area < least.area - 1e-9 || (Math.abs(area - least.area) <= 1e-9 && d < least.d)) least = { c, th, d, area };
  }
  if (best) return site(best.c, best.th);
  if (least) return site(least.c, least.th);
  return site(pc, theta);
}
