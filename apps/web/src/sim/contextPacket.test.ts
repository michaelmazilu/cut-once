import * as THREE from "three";
import { describe, expect, it } from "vitest";
import { Strict } from "@cutonce/schemas";
import { buildContextPacket, projectBox } from "./contextPacket";

const cam = () => {
  const c = new THREE.PerspectiveCamera(90, 1280 / 960, 0.01, 100);
  c.position.set(0, 0, 2); c.lookAt(0, 0, 0); c.updateMatrixWorld(); c.updateProjectionMatrix();
  return c;
};

describe("projectBox", () => {
  it("puts a centred box in the middle of the frame", () => {
    const p = projectBox(cam(), { min: [-0.1, -0.1, -0.1], max: [0.1, 0.1, 0.1] }, 1280, 960)!;
    const [x, y, w, h] = p.bbox_px;
    expect(x + w / 2).toBeCloseTo(640, 0);
    expect(y + h / 2).toBeCloseTo(480, 0);
    expect(p.in_frame).toBe(1);
    expect(p.distance_m).toBeCloseTo(2, 1);
  });
  it("returns null for a box behind the camera and a partial share at the edge", () => {
    expect(projectBox(cam(), { min: [-0.1, -0.1, 2.5], max: [0.1, 0.1, 2.7] }, 1280, 960)).toBeNull();
    const edge = projectBox(cam(), { min: [2.3, -0.1, -0.1], max: [2.9, 0.1, 0.1] }, 1280, 960)!;
    expect(edge.in_frame).toBeGreaterThan(0);
    expect(edge.in_frame).toBeLessThan(1);
  });
});

describe("buildContextPacket", () => {
  it("builds a packet the server's schema accepts", () => {
    const packet = buildContextPacket({
      assemblyId: "asm_demo_1", planRevision: 1, stateVersion: 3, selected: "part_a", currentStepId: "step_04",
      parts: [
        { part_id: "part_a", state: "missing", box: { min: [-0.1, -0.1, -0.1], max: [0.1, 0.1, 0.1] } },
        { part_id: "part_b", state: "built", box: { min: [-0.1, -0.1, 2.5], max: [0.1, 0.1, 2.7] } },
      ],
      camera: cam(), width: 1280, height: 960,
    });
    expect(Strict.CopilotContext.parse(packet).visible_parts.map((v) => v.part_id)).toEqual(["part_a"]);
    expect(packet.camera!.cx).toBe(640);
    expect(packet.selection_source).toBe("controller_ray");
    expect(packet.mode).toBe("overlay");
  });
  it("says build mode when the pretend headset is in it, so Kit answers the turn", () => {
    const packet = buildContextPacket({ assemblyId: "asm_demo_1", planRevision: 1, stateVersion: 0, selected: null, currentStepId: null, parts: [], camera: cam(), width: 1280, height: 960, mode: "build" });
    expect(Strict.CopilotContext.parse(packet)).toMatchObject({ mode: "build", selection_source: "none" });
  });
});
