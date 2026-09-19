import * as THREE from "three";
import { describe, expect, it } from "vitest";
import { disposeGroup } from "./glow";
import { KITCHEN } from "./kitchenScene";
import { FOV_Y_DEG, encodePoints } from "./scanCapture";

describe("encodePoints", () => {
  // A 2 × 2 grid as WebGL reads it back: bottom row first. Top-left is a hit at (0.1, 0.9, -0.3); bottom-right misses.
  const rgba = new Float32Array([
    /* bottom-left */ 0.5, 0.92, -0.2, 1, /* bottom-right */ 0, 0, 0, 0,
    /* top-left */ 0.1, 0.9, -0.3, 1, /* top-right */ -0.2, 1.1, -0.4, 1,
  ]);

  it("turns world positions into millimetres, row-major from the photo's top-left, with a hit mask", () => {
    const { points_mm, hit } = encodePoints(rgba, 2, 2, [0, 1.6, 1.6], 0);
    expect(hit).toBe("1110");
    expect(points_mm).toEqual([100, 900, -300, -200, 1100, -400, 500, 920, -200, 0, 0, 0]);
  });

  it("adds noise along the ray only, a few millimetres, the same for the same seed", () => {
    const a = encodePoints(rgba, 2, 2, [0, 1.6, 1.6], 0.004, 7), b = encodePoints(rgba, 2, 2, [0, 1.6, 1.6], 0.004, 7);
    expect(a).toEqual(b);
    const exact = encodePoints(rgba, 2, 2, [0, 1.6, 1.6], 0).points_mm;
    a.points_mm.forEach((v, i) => expect(Math.abs(v - exact[i]!)).toBeLessThanOrEqual(8));
  });
});

describe("the kitchen", () => {
  it("uses the Quest 3 passthrough lens (about 58.7° tall)", () => expect(FOV_Y_DEG).toBeCloseTo(58.7, 0));

  it("stands every counter object on the counter top, apart from each other", () => {
    const counter = KITCHEN.objects.filter((o) => Math.abs(o.base[1] - 0.92) < 1e-6);
    expect(counter.length).toBeGreaterThan(10);
    const footprint = (o: (typeof counter)[number]) => (o.kind === "box" ? Math.max(o.size[0], o.size[2]) : o.diameter) / 2;
    for (let i = 0; i < counter.length; i++) for (let j = i + 1; j < counter.length; j++) {
      const a = counter[i]!, b = counter[j]!;
      if (a.id.startsWith("apple") || b.id.startsWith("apple") || a.id.startsWith("plant") || b.id.startsWith("plant") || a.id === "fruit_bowl" || b.id === "fruit_bowl") continue;
      const d = Math.hypot(a.base[0] - b.base[0], a.base[2] - b.base[2]);
      expect([a.id, b.id, d > footprint(a) + footprint(b) - 0.02]).toEqual([a.id, b.id, true]);
    }
  });

  it("gives every vocabulary object a truth name, so a scan of it can be scored", () => {
    const named = KITCHEN.objects.filter((o) => o.truth).map((o) => o.truth);
    expect(named).toEqual(expect.arrayContaining(["pizza_box", "drink_can", "water_bottle", "cardboard_box", "tape_roll", "mug"]));
  });
});

describe("clearing the objects' glows", () => {
  it("takes each label's tag out of the page, however deep it hangs", () => {
    // A glow holds its label; three only tells a label it was removed when it is removed from ITS parent, so
    // emptying the group left the tags floating over the kitchen after a build began.
    const group = new THREE.Group(), glow = new THREE.Group();
    let removed = 0;
    const label = Object.assign(new THREE.Object3D(), { isCSS2DObject: true, element: { remove: () => { removed++; } } });
    glow.add(label as unknown as THREE.Object3D);
    glow.add(new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1), new THREE.MeshBasicMaterial()));
    group.add(glow);
    disposeGroup(glow);
    expect(removed).toBe(1);
  });
});
