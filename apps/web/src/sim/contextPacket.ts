import * as THREE from "three";
import type { Aabb } from "@cutonce/project-model";
import type { CopilotContext, PartState } from "@cutonce/schemas";

/**
 * Where a part lands in a camera frame, as the headset's PartProjector will compute it (blueprint §10):
 * the rectangle around its eight projected corners, clipped to the frame, and how much of it is inside.
 */
export interface Projected { bbox_px: [number, number, number, number]; in_frame: number; distance_m: number }

export function projectBox(camera: THREE.PerspectiveCamera, box: Aabb, width: number, height: number): Projected | null {
  camera.updateMatrixWorld();
  const corners: THREE.Vector3[] = [];
  for (const x of [box.min[0], box.max[0]]) for (const y of [box.min[1], box.max[1]]) for (const z of [box.min[2], box.max[2]]) corners.push(new THREE.Vector3(x, y, z));
  // Camera space looks down -Z: only corners in front of the lens can be projected.
  const front = corners.filter((c) => c.clone().applyMatrix4(camera.matrixWorldInverse).z < -camera.near);
  if (front.length === 0) return null;
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (const c of front) {
    const p = c.clone().project(camera);
    const x = ((p.x + 1) / 2) * width, y = ((1 - p.y) / 2) * height;
    minX = Math.min(minX, x); maxX = Math.max(maxX, x); minY = Math.min(minY, y); maxY = Math.max(maxY, y);
  }
  const area = (maxX - minX) * (maxY - minY);
  const x0 = Math.max(0, minX), y0 = Math.max(0, minY), x1 = Math.min(width, maxX), y1 = Math.min(height, maxY);
  const inside = Math.max(0, x1 - x0) * Math.max(0, y1 - y0);
  const centre = new THREE.Vector3((box.min[0] + box.max[0]) / 2, (box.min[1] + box.max[1]) / 2, (box.min[2] + box.max[2]) / 2);
  const [rx0, ry0, rx1, ry1] = [x0, y0, x1, y1].map(Math.round) as [number, number, number, number]; // round edges, not sizes
  return {
    bbox_px: [rx0, ry0, Math.max(0, rx1 - rx0), Math.max(0, ry1 - ry0)],
    in_frame: area > 0 ? Math.round((inside / area) * 1000) / 1000 : 0,
    distance_m: Math.round(camera.position.distanceTo(centre) * 1000) / 1000,
  };
}

const randomHex = (n: number) => Array.from(crypto.getRandomValues(new Uint8Array(n)), (b) => (b % 16).toString(16)).join("");

/**
 * The packet the headset sends with every question. Parts less than 5% in view are left out. `mode` "build" (from the
 * first scan to the end of the walkthrough) makes the turn Kit's.
 */
export function buildContextPacket(args: {
  assemblyId: string; planRevision: number; stateVersion: number; selected: string | null; currentStepId: string | null;
  parts: { part_id: string; state: PartState; box: Aabb }[];
  camera: THREE.PerspectiveCamera; width: number; height: number; scriptedQueryId?: string | null; mode?: "overlay" | "build";
}): CopilotContext {
  const { camera, width, height } = args;
  const tanHalf = Math.tan(THREE.MathUtils.degToRad(camera.fov / 2));
  const visible = args.parts.flatMap((p) => {
    const r = projectBox(camera, p.box, width, height);
    return r && r.in_frame >= 0.05 ? [{ part_id: p.part_id, state: p.state, ...r }] : [];
  });
  return {
    context_id: `ctx_${randomHex(12)}`,
    assembly_id: args.assemblyId,
    plan_revision: args.planRevision,
    state_version: args.stateVersion,
    mode: args.mode ?? "overlay",
    selected_part_id: args.selected,
    selection_source: args.selected ? "controller_ray" : "none",
    current_step_id: args.currentStepId,
    visible_parts: visible,
    // The frame is the view stretched to width × height, so each axis keeps its own focal length.
    camera: { width, height, fx: width / 2 / (camera.aspect * tanHalf), fy: height / 2 / tanHalf, cx: width / 2, cy: height / 2 },
    scripted_query_id: args.scriptedQueryId ?? null,
    client_sent_at: new Date().toISOString(),
  };
}
