import { describe, expect, it } from "vitest";
import type { Vec3 } from "@cutonce/schemas";
import { Strict } from "@cutonce/schemas";
import { onSurface, buildTwins, decodeScan, minAreaRect, yawQuat } from "../src/build/twins.js";
import { heightOf } from "../src/build/shape.js";
import { CAMERA, FLOOR, KIT, TABLE, box, can, synthScan, turned, type Prim } from "./build-synth.js";

const run = (prims: Parameters<typeof synthScan>[0], noiseM = 0) =>
  buildTwins(decodeScan(synthScan(prims, CAMERA.cam, CAMERA.lookAt, { noiseM })), "scan_synthetic");

describe("surfaces", () => {
  it("finds the floor and the table top, and not the box tops", () => {
    const { surfaces } = run([FLOOR, TABLE, box(-0.2, 0.55, 0.3, 0.1, 0.2)]);
    const kinds = surfaces.map((s) => [s.kind, Math.round(s.y * 100)]);
    expect(kinds).toContainEqual(["floor", 0]);
    expect(kinds).toContainEqual(["table", 74]);
    expect(surfaces.every((s) => Math.abs(s.y - 0.84) > 0.02)).toBe(true);   // the 30 × 20 cm box top is an object, not a table
  });
});

describe("a wall right behind the table (a kitchen counter's backsplash)", () => {
  // Every height the table's objects reach, the wall reaches too. A slice of it used to be taken for part of the table
  // (in a real kitchen scan, for a second table 4 cm up, which cut every can in half and hid the pizza box); here it
  // welds onto the table's own outline and stretches it from 1.2 m to 3.2 m — the same fault, seen through the table.
  const WALL: Prim = { kind: "box", min: [-2, 0, 1.02], max: [2, 2.5, 1.05] };
  const scene = [FLOOR, TABLE, WALL, box(-0.25, 0.75, 0.35, 0.04, 0.35), can(0.1, 0.5), can(0.22, 0.5), can(0.16, 0.66), can(0.3, 0.9)];

  it("is not a surface, not part of the table, and the objects in front keep their full height", () => {
    const { surfaces, twins } = run(scene, 0.004);
    expect(surfaces.map((s) => s.kind).sort()).toEqual(["floor", "table"]);
    const table = surfaces.find((s) => s.kind === "table")!.rect!;
    expect([Math.abs(Math.max(table.len, table.wid) - 1.2) < 0.06, Math.abs(Math.min(table.len, table.wid) - 0.8) < 0.06]).toEqual([true, true]);
    const heights = twins.map((t) => Math.round(heightOf(t.shape) * 100)).sort((a, b) => a - b);
    expect(heights).toEqual([4, 16, 16, 16, 16]);
  });
});

describe("something standing behind something else", () => {
  it("keeps a tall object whose foot is hidden: only what stands ON the surface reaches down to it", () => {
    // A 40 cm bottle behind a 30 cm box shows only its neck, so its lowest SEEN point is 30 cm above the table. The
    // rule that throws away a patch of wall seen over the table's far edge must not throw this away too.
    const scene = [FLOOR, TABLE, box(0, 0.45, 0.25, 0.3, 0.15), can(0, 0.7, 0.033, 0.4)];
    const { twins } = run(scene, 0.004);
    const bottle = twins.find((t) => Math.abs(heightOf(t.shape) - 0.4) < 0.06);
    expect([twins.length, bottle !== undefined]).toEqual([2, true]);
  });
});

describe("objects on the table and the floor", () => {
  const scene = [FLOOR, TABLE, box(-0.2, 0.55, 0.3, 0.1, 0.2), can(0.15, 0.5), box(0.45, 0.02, 0.35, 0.3, 0.25, 0)];

  it("finds each object once, on the right surface, with sizes within 2 cm", () => {
    const { surfaces, twins } = run(scene);
    expect(twins).toHaveLength(3);
    const table = surfaces.find((s) => s.kind === "table")!, floor = surfaces.find((s) => s.kind === "floor")!;
    const theCan = twins.find((t) => t.shape.type === "cylinder")!;
    expect(theCan.sits_on).toBe(table.surface_id);
    expect(Math.abs((theCan.shape.type === "cylinder" ? theCan.shape.diameter : NaN) - 0.066)).toBeLessThan(0.02);
    expect(Math.abs(heightOf(theCan.shape) - 0.157)).toBeLessThan(0.02);
    const floorBox = twins.find((t) => t.sits_on === floor.surface_id)!;
    expect(floorBox.shape.type).toBe("box");
    expect(Math.abs(heightOf(floorBox.shape) - 0.3)).toBeLessThan(0.02);
    const tableBox = twins.find((t) => t.shape.type === "box" && t.sits_on === table.surface_id)!;
    const s = tableBox.shape.type === "box" ? [...tableBox.shape.size].sort((a, b) => b - a) : [];
    expect(Math.abs(s[0]! - 0.3)).toBeLessThan(0.02);
    expect(Math.abs(s[1]! - 0.2)).toBeLessThan(0.02);
  });

  it("names nothing yet, numbers nearest first, and gives each twin its box in the photo", () => {
    const { twins } = run(scene);
    expect(twins.map((t) => t.name)).toEqual(["unknown", "unknown", "unknown"]);
    expect(twins.map((t) => t.twin_id)).toEqual(["o1", "o2", "o3"]);
    expect(twins[0]!.distance_m).toBeLessThanOrEqual(twins[1]!.distance_m);
    for (const t of twins) expect(t.bbox_px![2]).toBeGreaterThan(0);
  });

  it("still works with 3 mm of depth noise", () => {
    const { twins } = run(scene, 0.003);
    expect(twins).toHaveLength(3);
  });

  it("separates three cans standing 5 cm apart and the pizza box", () => {
    const { twins } = run(KIT);
    expect(twins.filter((t) => t.shape.type === "cylinder")).toHaveLength(3);
    expect(twins.filter((t) => t.shape.type === "box")).toHaveLength(1);
  });
});

describe("minAreaRect and yaw", () => {
  it("finds a rectangle turned 30° and a yaw that points local +X along its long side", () => {
    const pts: [number, number][] = [];
    const a = (30 * Math.PI) / 180, u: [number, number] = [Math.cos(a), Math.sin(a)], v: [number, number] = [-Math.sin(a), Math.cos(a)];
    for (let i = 0; i <= 20; i++) for (let j = 0; j <= 10; j++) {
      const s = (i / 20 - 0.5) * 0.4, t = (j / 10 - 0.5) * 0.1;
      pts.push([1 + s * u[0] + t * v[0], 2 + s * u[1] + t * v[1]]);
    }
    const r = minAreaRect(pts);
    expect(r.len).toBeCloseTo(0.4, 2);
    expect(r.wid).toBeCloseTo(0.1, 2);
    const [, y, , w] = yawQuat(r.yawDeg);
    // rotate (1, 0, 0) about +Y by the quaternion: x' = 1 - 2y², z' = -2wy
    const dir: [number, number] = [1 - 2 * y * y, -2 * w * y];
    expect(Math.abs(dir[0] * u[0] + dir[1] * u[1])).toBeCloseTo(1, 3);
  });
});

/**
 * The scenes above are tidy: boxes square to the room, a table with no legs, no noise worth the name. These are the
 * cases that broke the first version of the builder when it was tried on them: it found no surface at all in a clean
 * scan (a tie in its peak search), and at 1% noise the floor came out as six floors. Noise is seeded, so nothing here
 * is flaky.
 */
describe("a real table, a real pile, real depth noise", () => {
  const slab: Prim = { kind: "box", min: [-0.6, 0.71, 0.2], max: [0.6, 0.74, 1.0] };
  const legs: Prim[] = [[-0.55, 0.25], [0.55, 0.25], [-0.55, 0.95], [0.55, 0.95]].map(([x, z]) => box(x!, z!, 0.05, 0.71, 0.05, 0));
  const cabinet: Prim = { kind: "box", min: [-0.6, 0, 0.2], max: [0.6, 0.74, 1.0] };          // a desk with a solid front
  const pile = [
    { name: "pizza box, turned 20°", prim: turned(-0.28, 0.55, 0.35, 0.04, 0.35, 20), at: [-0.28, 0.55], height: 0.04, tol: 0.03 },
    { name: "tall can A", prim: can(0.05, 0.45), at: [0.05, 0.45], height: 0.157, tol: 0.03 },
    { name: "tall can B, 5 cm from A", prim: can(0.17, 0.45), at: [0.17, 0.45], height: 0.157, tol: 0.03 },
    { name: "energy can", prim: can(0.11, 0.6, 0.0265, 0.135), at: [0.11, 0.6], height: 0.135, tol: 0.03 },
    { name: "sponsor box, turned -30°", prim: turned(0.36, 0.7, 0.2, 0.12, 0.15, -30), at: [0.36, 0.7], height: 0.12, tol: 0.03 },
    { name: "tape roll", prim: can(-0.02, 0.75, 0.05, 0.05), at: [-0.02, 0.75], height: 0.05, tol: 0.03 },
    // On the floor beside the table, which hides part of it from the far viewpoint: found, but its centre is rougher.
    { name: "box on the floor", prim: turned(1.0, 0.9, 0.4, 0.3, 0.3, 10, 0), at: [1.0, 0.9], height: 0.3, tol: 0.07 },
  ];
  const far = { cam: [0, 1.6, -0.75] as Vec3, lookAt: [0.05, 0.74, 0.6] as Vec3 };
  const demo = { cam: [0.05, 1.55, -0.35] as Vec3, lookAt: [0.05, 0.74, 0.55] as Vec3 };     // standing 55 cm from the table's edge
  const scan = (table: Prim[], view: typeof far, sigmaFrac: number, seed: number) =>
    buildTwins(decodeScan(synthScan([FLOOR, ...table, ...pile.map((o) => o.prim)], view.cam, view.lookAt, { sigmaFrac, seed })), "scan_synthetic");

  const tables: [string, Prim[]][] = [["a slab on four legs", [slab, ...legs]], ["a desk with a solid front", [cabinet]]];
  for (const [tableName, table] of tables)
    for (const [viewName, view] of [["from 1 m back", far], ["from the demo spot", demo]] as const)
      for (const sigmaFrac of [0, 0.005, 0.01])
        it(`${tableName}, ${viewName}, depth noise ${sigmaFrac * 100}% of range: the floor, the table and exactly the seven objects`, () => {
          for (const seed of [1, 2, 3]) {
            const { surfaces, twins } = scan(table, view, sigmaFrac, seed);
            expect(surfaces.map((s) => [s.kind, Math.round(s.y * 100) || 0]), `seed ${seed}`).toEqual([["floor", 0], ["table", 74]]);   // || 0: round(-0.1) is -0
            expect(twins, `seed ${seed}: ${twins.map((t) => `${t.twin_id}@${t.position.map((n) => n.toFixed(2))}`).join(" ")}`).toHaveLength(pile.length);
            for (const o of pile) {
              const near = twins.filter((t) => Math.hypot(t.position[0] - o.at[0]!, t.position[2] - o.at[1]!) <= o.tol);
              expect(near, `${o.name}, seed ${seed}`).toHaveLength(1);
              expect(Math.abs(heightOf(near[0]!.shape) - o.height), `${o.name} height, seed ${seed}`).toBeLessThan(0.025);
              expect(near[0]!.sits_on, o.name).toBe(surfaces.find((s) => s.kind === (o.name === "box on the floor" ? "floor" : "table"))!.surface_id);
            }
          }
        });

  // The RAW width of the hardest objects in the kit (cans 5 to 7 cm wide), so a regression shows. Depth noise pushes the
  // outermost samples outwards along the line of sight, and the rectangle is drawn around exactly those, so at 1% noise
  // a 5.3 cm can reads up to 3 cm wide. FR5's ±2 cm is met after sizes.ts gives a named product its standard size
  // (tests/build-sizes.test.ts); an unnamed object keeps this error, and carries it in error_m.
  for (const [sigmaFrac, bar] of [[0.005, 0.015], [0.01, 0.035]] as const)
    it(`measures the cans to within ${bar * 100} cm from the demo spot with ${sigmaFrac * 100}% depth noise`, () => {
      for (const seed of [1, 2, 3]) {
        const { twins } = scan([slab, ...legs], demo, sigmaFrac, seed);
        for (const o of pile.filter((q) => q.name.includes("can"))) {
          const t = twins.find((q) => Math.hypot(q.position[0] - o.at[0]!, q.position[2] - o.at[1]!) <= 0.03)!;
          const width = t.shape.type === "cylinder" ? t.shape.diameter : Math.max(t.shape.size[0], t.shape.size[2]);
          const off = Math.abs(width - (o.name === "energy can" ? 0.053 : 0.066));
          expect(off, `${o.name}, seed ${seed}`).toBeLessThan(bar);
          expect(off, `${o.name}, seed ${seed}: the twin's own error_m must cover what it got wrong`).toBeLessThanOrEqual(t.error_m + 0.003);
        }
      }
    });

  it("says so when the edge of the photo cuts an object off: its size is only a lower bound", () => {
    // A long box on the floor that runs out of the side of the picture, next to one that is wholly in it.
    const cut = turned(1.4, 0.6, 0.9, 0.2, 0.3, 0, 0), whole = turned(0, 0.6, 0.3, 0.2, 0.3, 0, 0);
    const { twins } = buildTwins(decodeScan(synthScan([FLOOR, cut, whole], demo.cam, demo.lookAt)), "scan_synthetic");
    expect(twins).toHaveLength(2);
    const [inView, clipped] = [...twins].sort((a, b) => a.position[0] - b.position[0]);
    expect(inView!.error_m).toBeLessThan(0.05);
    expect(clipped!.error_m).toBeGreaterThan(0.15);
  });
});

/**
 * The headset's tracking frame points wherever the headset was when it started, so a real table is almost never square
 * to the room's axes. Its box along those axes is bigger than the table, by the corners the table does not have.
 */
describe("a table turned 30° in the room", () => {
  const turnedTable = turned(0, 1.0, 1.2, 0.02, 0.6, 30, 0.72);
  // On the floor just past the table's end: inside the table's box along the room's axes, but not under the table.
  const floorBox = box(0.5, 0.55, 0.2, 0.2, 0.2, 0);
  const { surfaces, twins } = run([FLOOR, turnedTable, can(0, 1.0), floorBox]);
  const table = surfaces.find((q) => q.kind === "table")!;

  it("keeps the table's own outline: its true length and width, not the box around it", () => {
    expect(table.rect).toBeDefined();
    expect(Math.abs(table.rect!.len - 1.2)).toBeLessThan(0.08);
    expect(Math.abs(table.rect!.wid - 0.6)).toBeLessThan(0.08);
    expect(table.max[0] - table.min[0]).toBeGreaterThan(1.25);        // the box along the room's axes is wider than the table
  });
  it("still finds the box on the floor beside it: it is not furniture under the table", () => {
    const floor = surfaces.find((q) => q.kind === "floor")!;
    expect(twins.map((t) => t.sits_on).sort()).toEqual([floor.surface_id, table.surface_id].sort());
  });
  it("says a point in the box's empty corner is not on the table", () => {
    expect(onSurface(table, 0, 1.0, 0)).toBe(true);
    expect(onSurface(table, table.max[0] - 0.03, table.min[1] + 0.03, 0)).toBe(false);
  });
});

describe("a flat card seen face-on, with no noise (what the simulator gives)", () => {
  it("never yields a twin with a side of zero: the stream's schema, and a replay's saved labels, refuse one", () => {
    const { twins } = run([FLOOR, TABLE, box(0, 0.5, 0.056, 0.141, 0.001)]);
    for (const t of twins) expect(() => Strict.Twin.parse({ ...t, twin_id: "o1" })).not.toThrow();
    for (const t of twins) expect(Math.min(...(t.shape.type === "box" ? t.shape.size : [t.shape.diameter, t.shape.length]))).toBeGreaterThanOrEqual(0.005);
  });
});
