import type { BuildScan, Surface, Twin, TwinShape, Vec3 } from "@cutonce/schemas";

/** One scan as an organised point cloud: cell i is the ray through the photo pixel at the centre of cell i. */
export interface Cloud { cols: number; rows: number; width: number; height: number; xyz: Float64Array; cam: Vec3; spacingRad: number }

export function decodeScan(scan: BuildScan): Cloud {
  const { cols, rows } = scan.grid, n = cols * rows;
  if (scan.points_mm.length !== 3 * n || scan.hit.length !== n) throw new Error(`scan ${scan.scan_id}: a ${cols} × ${rows} grid needs ${n} cells`);
  const xyz = new Float64Array(3 * n).fill(NaN);
  for (let i = 0; i < n; i++) if (scan.hit[i] === "1") for (let k = 0; k < 3; k++) xyz[3 * i + k] = scan.points_mm[3 * i + k]! / 1000;
  const { width, height, fx } = scan.camera.intrinsics;
  return { cols, rows, width, height, xyz, cam: scan.camera.position, spacingRad: (2 * Math.atan(width / 2 / fx)) / cols };
}

/**
 * Every number here was set against synthetic scans with depth noise of 0, 0.5% and 1% of range, from a far, low
 * viewpoint and from a demo one (tests/build-twins.test.ts). Tune them against real recordings with `pnpm build:eval`,
 * and write the reason beside any change.
 */
export const OPTIONS = {
  surfaceBand: 0.015,             // a surface point is within 1.5 cm of the surface's height…
  surfaceBandPerMetre: 0.008,     // …plus 0.8 cm per metre of range: depth noise grows with distance
  levelSeparation: 0.03,          // two levels closer than this are one surface
  minSurfacePoints: 60,
  minSurfaceWidth: 0.45,          // tables and floors are big; a pizza box's lid (35 cm) is not a surface
  maxSurfaceTiltDeg: 8,           // a ramp or a leaning board is not something to build on
  floorBand: 0.12,                // the floor is at height 0 (the headset's tracking origin is floor level), give or take a bad calibration
  floorMaxY: 0.25,
  slantLink: 0.035,               // see `link` below
  minObjectPoints: 8,
  minObjectHeight: 0.015,
  minObjectHeightPerMetre: 0.012, // taller than the noise at that range, or it is a bump in the depth map
  maxObjectSize: 1.5,             // bigger than this is furniture or a wall
  restingGap: 0.25,               // how far above its surface an object's lowest seen point may be (the rest is hidden behind something)
};
export type Options = typeof OPTIONS;

/** A yaw as a quaternion [x, y, z, w]: a right-handed turn about +Y. */
export const yawQuat = (deg: number): [number, number, number, number] => {
  const h = (deg * Math.PI) / 360;
  return [0, Math.sin(h), 0, Math.cos(h)];
};

type P2 = [number, number];
export interface Rect { cx: number; cz: number; len: number; wid: number; yawDeg: number }

/**
 * The smallest rectangle around points in the x/z plane (1° sweep; deterministic). yawDeg is the right-handed turn
 * about +Y that takes local +X onto the long side: a turn θ takes +X to (cos θ, -sin θ) in (x, z).
 */
export function minAreaRect(pts: P2[]): Rect {
  let best = { area: Infinity, deg: 0, minU: 0, maxU: 0, minV: 0, maxV: 0 };
  for (let deg = 0; deg < 90; deg++) {
    const t = (deg * Math.PI) / 180, c = Math.cos(t), s = Math.sin(t);
    let minU = Infinity, maxU = -Infinity, minV = Infinity, maxV = -Infinity;
    for (const [x, z] of pts) {
      const u = x * c + z * s, v = -x * s + z * c;
      if (u < minU) minU = u; if (u > maxU) maxU = u; if (v < minV) minV = v; if (v > maxV) maxV = v;
    }
    const area = (maxU - minU) * (maxV - minV);
    if (area < best.area - 1e-12) best = { area, deg, minU, maxU, minV, maxV };
  }
  const t = (best.deg * Math.PI) / 180, c = Math.cos(t), s = Math.sin(t);
  const u0 = (best.minU + best.maxU) / 2, v0 = (best.minV + best.maxV) / 2;
  let len = best.maxU - best.minU, wid = best.maxV - best.minV, along = best.deg;
  if (wid > len) { [len, wid] = [wid, len]; along += 90; }
  return { cx: u0 * c - v0 * s, cz: u0 * s + v0 * c, len, wid, yawDeg: -along };
}

/** A point in a rectangle's own frame: u along its long side, v along its short one. */
export function inRectFrame(r: Rect, x: number, z: number): P2 {
  const t = (-r.yawDeg * Math.PI) / 180, c = Math.cos(t), s = Math.sin(t);
  return [(x - r.cx) * c + (z - r.cz) * s, -(x - r.cx) * s + (z - r.cz) * c];
}

/** The cloud with the accessors and distances every step below needs. */
function view(cloud: Cloud, opts: Options) {
  const { cols, rows, xyz, cam } = cloud;
  const x = (i: number) => xyz[3 * i]!, y = (i: number) => xyz[3 * i + 1]!, z = (i: number) => xyz[3 * i + 2]!;
  const range = (i: number) => Math.hypot(x(i) - cam[0], y(i) - cam[1], z(i) - cam[2]);
  const ok = (i: number) => !Number.isNaN(xyz[3 * i]!);
  // Walls, and the sides of things: the cells two rows up AND down (where there are any) rise more than they move
  // away. A 1 cm slice of a backsplash is at a table's height as much as the table is; this keeps it from counting as
  // one. Two rows, not one, so depth noise (which moves a wall's points along the ray, not up it) cannot pass for a slope.
  const steep = new Uint8Array(cols * rows);
  const risesTo = (i: number, j: number) => Math.abs(y(i) - y(j)) > Math.hypot(x(i) - x(j), z(i) - z(j));
  for (let i = 0; i < cols * rows; i++) {
    if (!ok(i)) continue;
    let checked = 0, rising = 0;
    for (const step of [-2, 2]) {
      // A real depth sensor returns nothing on a plain painted wall, which is exactly where this mask is needed, so
      // when the cell two rows away is missing, ask the one next door rather than letting the wall through unmasked.
      const j = [i + step * cols, i + (step / 2) * cols].find((k) => k >= 0 && k < cols * rows && ok(k));
      if (j === undefined) continue;
      checked++;
      if (risesTo(i, j)) rising++;
    }
    steep[i] = checked > 0 && rising === checked ? 1 : 0;
  }
  return {
    cols, rows, n: cols * rows, x, y, z, range, steep,
    ok,
    xz: (cells: number[]) => cells.map((i) => [x(i), z(i)] as P2),
    dist3: (i: number, j: number) => Math.hypot(x(i) - x(j), y(i) - y(j), z(i) - z(j)),
    /** How far a point may sit from a surface's height and still be that surface. */
    band: (i: number) => opts.surfaceBand + opts.surfaceBandPerMetre * range(i),
    /**
     * How far apart two neighbouring cells of ONE object can be. Facing the camera: about a ray spacing, so 2.5 of
     * them. Seen at a slant (a box's top from a low angle) rows land 1/sin(angle) further apart: 3.5% of the range
     * covers slants down to about 16°. Without that term a box top 1.9 m away breaks into one strip per row.
     */
    link: (i: number) => { const r = range(i); return Math.max(0.03, 2.5 * cloud.spacingRad * r, opts.slantLink * r); },
  };
}
type View = ReturnType<typeof view>;

/** Cells joined to `start` through grid neighbours within `reach` cells that pass `member` and lie within `maxGap(i)` of each other. */
function grow(v: View, start: number, seen: Uint8Array, reach: number, member: (i: number) => boolean, maxGap: (i: number) => number): number[] {
  const cells: number[] = [], stack = [start];
  seen[start] = 1;
  while (stack.length) {
    const i = stack.pop()!;
    cells.push(i);
    const r = Math.floor(i / v.cols), c = i % v.cols;
    for (let dr = -reach; dr <= reach; dr++) for (let dc = -reach; dc <= reach; dc++) {
      const rr = r + dr, cc = c + dc;
      if ((dr === 0 && dc === 0) || rr < 0 || rr >= v.rows || cc < 0 || cc >= v.cols) continue;
      const j = rr * v.cols + cc;
      if (seen[j] || !member(j) || v.dist3(i, j) > maxGap(i)) continue;
      seen[j] = 1;
      stack.push(j);
    }
  }
  return cells;
}

/**
 * Heights that might be surfaces: the fullest 1 cm bins, best first, none within 3 cm of a better one. A level surface
 * piles its points into one bin, while walls and the sides of objects spread theirs thinly over many. Gravity is
 * known (the tracking frame is level), so no plane fitting and no randomness is needed. Counting all points, not only
 * those whose neighbours say "facing up": per-cell normals are the first thing depth noise destroys.
 */
function findLevels(v: View, opts: Options): number[] {
  const bins = new Map<number, number>();
  for (let i = 0; i < v.n; i++) if (v.ok(i) && !v.steep[i]) { const b = Math.round(v.y(i) / 0.01); bins.set(b, (bins.get(b) ?? 0) + 1); }
  const win = (b: number) => (bins.get(b - 1) ?? 0) + (bins.get(b) ?? 0) + (bins.get(b + 1) ?? 0);
  // Ties go to the bin with more points of its own, then the lower one: a clean scan puts a whole table in ONE bin, so
  // its two neighbours' windows tie with it.
  const ranked = [...bins.keys()].filter((b) => win(b) >= opts.minSurfacePoints)
    .sort((p, q) => win(q) - win(p) || bins.get(q)! - bins.get(p)! || p - q);
  const apart = Math.round(opts.levelSeparation / 0.01), taken: number[] = [], levels: number[] = [];
  for (const b of ranked) {
    if (taken.some((t) => Math.abs(t - b) <= apart)) continue;
    taken.push(b);
    let sum = 0, count = 0;                                          // the level itself: the mean height of the points around the bin
    for (let i = 0; i < v.n; i++) if (v.ok(i) && !v.steep[i] && Math.abs(v.y(i) - b * 0.01) <= 0.015) { sum += v.y(i); count++; }
    levels.push(sum / count);
  }
  return levels;
}

interface Region { y: number; cells: number[] }

/** Tilt of the least-squares plane y = a·x + b·z + c through the cells, in degrees from level. */
function tiltDeg(v: View, cells: number[]): number {
  let mx = 0, my = 0, mz = 0;
  for (const i of cells) { mx += v.x(i); my += v.y(i); mz += v.z(i); }
  mx /= cells.length; my /= cells.length; mz /= cells.length;
  let sxx = 0, sxz = 0, szz = 0, sxy = 0, szy = 0;
  for (const i of cells) { const dx = v.x(i) - mx, dy = v.y(i) - my, dz = v.z(i) - mz; sxx += dx * dx; sxz += dx * dz; szz += dz * dz; sxy += dx * dy; szy += dz * dy; }
  const det = sxx * szz - sxz * sxz;
  if (Math.abs(det) < 1e-12) return 0;
  return (Math.atan(Math.hypot((sxy * szz - szy * sxz) / det, (szy * sxx - sxy * sxz) / det)) * 180) / Math.PI;
}

/**
 * Surfaces: grid-connected cells at each level. A neighbour two cells away still connects (noise knocks single cells
 * out of the band), as long as it is near in 3D as well as in the photo. Everything but the floor must be at least
 * 45 cm wide ALONG ITS OWN SIDES: measured along the room's axes, a 35 cm pizza box turned 20° spans 45 cm.
 */
function findSurfaces(v: View, levels: number[], opts: Options): { surfaces: Surface[]; isSurface: Uint8Array } {
  const isSurface = new Uint8Array(v.n);
  const kept: Region[] = [];
  let floorFound = false;
  for (const level of levels) {
    const at = (i: number) => v.ok(i) && !v.steep[i] && !isSurface[i] && Math.abs(v.y(i) - level) <= v.band(i);
    const seen = new Uint8Array(v.n);
    let found: Region[] = [];
    for (let s = 0; s < v.n; s++) if (!seen[s] && at(s)) {
      const cells = grow(v, s, seen, 2, at, (i) => 2 * v.link(i));
      found.push({ y: cells.reduce((sum, i) => sum + v.y(i), 0) / cells.length, cells });
    }
    // The floor is ONE surface however furniture cuts up the view of it. The tracking origin is floor level, so it is
    // the fullest level near height 0 (levels come fullest first).
    const isFloor = !floorFound && Math.abs(level) <= opts.floorBand;
    if (isFloor) {
      floorFound = true;
      const cells = found.filter((f) => f.cells.length >= 4).flatMap((f) => f.cells);
      found = cells.length ? [{ y: cells.reduce((sum, i) => sum + v.y(i), 0) / cells.length, cells }] : [];
    }
    for (const f of found) {
      if (f.cells.length < opts.minSurfacePoints) continue;
      if (minAreaRect(v.xz(f.cells)).wid < opts.minSurfaceWidth) continue;
      if (tiltDeg(v, f.cells) > opts.maxSurfaceTiltDeg) continue;
      for (const i of f.cells) isSurface[i] = 1;
      kept.push(f);
    }
  }
  // Ids in order of size, so s1 is the biggest surface in view.
  const order = kept.map((_, k) => k).sort((a, b) => kept[b]!.cells.length - kept[a]!.cells.length || kept[b]!.y - kept[a]!.y);
  const surfaces: Surface[] = order.map((k, idx) => {
    const f = kept[k]!;
    const min: P2 = [Infinity, Infinity], max: P2 = [-Infinity, -Infinity];
    for (const i of f.cells) { min[0] = Math.min(min[0], v.x(i)); min[1] = Math.min(min[1], v.z(i)); max[0] = Math.max(max[0], v.x(i)); max[1] = Math.max(max[1], v.z(i)); }
    const kind = f.y < opts.floorMaxY ? "floor" : f.y >= 0.55 && f.y <= 1.2 ? "table" : f.y > 1.2 ? "shelf" : "other";
    const r = minAreaRect(v.xz(f.cells));
    const rect = r.len > 0 && r.wid > 0 ? { centre: [r.cx, r.cz] as P2, len: r.len, wid: r.wid, yaw_deg: r.yawDeg } : undefined;
    return { surface_id: `s${idx + 1}`, kind, y: f.y, min, max, points: f.cells.length, ...(rect ? { rect } : {}) };
  });
  return { surfaces, isSurface };
}

/** A surface's outline: its own turned rectangle when it has one, else its box along the room's axes. */
export const rectOf = (s: Surface): Rect => s.rect
  ? { cx: s.rect.centre[0], cz: s.rect.centre[1], len: s.rect.len, wid: s.rect.wid, yawDeg: s.rect.yaw_deg }
  : { cx: (s.min[0] + s.max[0]) / 2, cz: (s.min[1] + s.max[1]) / 2, len: s.max[0] - s.min[0], wid: s.max[1] - s.min[1], yawDeg: 0 };

/** The four corners of a rectangle, in the room. */
export function cornersOf(r: Rect): P2[] {
  const t = (r.yawDeg * Math.PI) / 180, c = Math.cos(t), sn = Math.sin(t);
  return ([[1, 1], [-1, 1], [-1, -1], [1, -1]] as P2[]).map(([a, b]) => {
    const u = (a * r.len) / 2, w = (b * r.wid) / 2;
    return [r.cx + u * c + w * sn, r.cz - u * sn + w * c];
  });
}

/** Is (x, z) over the surface, within `margin` of its outline? Judged in the surface's own frame, not the room's. */
export function onSurface(s: Surface, x: number, z: number, margin: number): boolean {
  const r = rectOf(s), [u, w] = inRectFrame(r, x, z);
  return Math.abs(u) <= r.len / 2 + margin && Math.abs(w) <= r.wid / 2 + margin;
}
// The floor goes on past what one view shows of it; a table ends where it ends.
const standsOn = (s: Surface, x: number, z: number) => onSurface(s, x, z, s.kind === "floor" ? 0.5 : 0.05);

const MIN_SIDE = 0.005;

interface Body { cells: number[]; rect: Rect; minY: number; maxY: number }

/**
 * Objects: grid-connected points that are not surface. A point at a surface's height over that surface is the surface
 * seen through noise, or the ring of table around an object's base, and never part of an object.
 *
 * A small cluster whose centre lies inside a bigger one's footprint then joins it: it is a face seen edge-on, or a
 * can's top that noise split from its side. Biggest first, so pieces join the body and never the other way round.
 */
function findBodies(v: View, surfaces: Surface[], isSurface: Uint8Array): Body[] {
  const free = new Uint8Array(v.n);
  for (let i = 0; i < v.n; i++) {
    if (!v.ok(i) || isSurface[i]) continue;
    free[i] = surfaces.some((s) => Math.abs(v.y(i) - s.y) <= v.band(i) && standsOn(s, v.x(i), v.z(i))) ? 0 : 1;
  }
  const seen = new Uint8Array(v.n), clusters: number[][] = [];
  for (let s = 0; s < v.n; s++) if (!seen[s] && free[s]) {
    const cells = grow(v, s, seen, 1, (i) => free[i] === 1, v.link);
    if (cells.length >= 3) clusters.push(cells);                     // smaller than this cannot even join a body
  }
  clusters.sort((a, b) => b.length - a.length);
  const bodies: Body[] = [];
  for (const cells of clusters) {
    let sx = 0, sz = 0, lo = Infinity, hi = -Infinity;
    for (const i of cells) { sx += v.x(i); sz += v.z(i); lo = Math.min(lo, v.y(i)); hi = Math.max(hi, v.y(i)); }
    const host = bodies.find((b) => {
      const [u, w] = inRectFrame(b.rect, sx / cells.length, sz / cells.length);
      return Math.abs(u) <= b.rect.len / 2 + 0.02 && Math.abs(w) <= b.rect.wid / 2 + 0.02 && lo <= b.maxY + 0.03 && hi >= b.minY - 0.03;
    });
    if (!host) { bodies.push({ cells: [...cells], rect: minAreaRect(v.xz(cells)), minY: lo, maxY: hi }); continue; }
    host.cells.push(...cells);
    host.minY = Math.min(host.minY, lo); host.maxY = Math.max(host.maxY, hi);
    host.rect = minAreaRect(v.xz(host.cells));
  }
  return bodies;
}

/**
 * The outermost samples of an object sit, on average, half a sample step inside its true edge, so a rectangle drawn
 * around them comes out one step short per side. The step along each side is read off the object's own top-face
 * neighbours (side faces do not move in x/z). It is longer along the line of sight: the far edge of a box top seen from
 * a low angle is otherwise under-measured by up to 4 cm.
 */
function samplePad(v: View, cells: number[], rect: Rect, top: number): P2 {
  const member = new Set(cells);
  const steps: [number[], number[]][] = [[[], []], [[], []]];        // [along the row | down the column][u | v]
  for (const i of cells) {
    if (v.y(i) < top - 0.015) continue;
    const neighbours: [number, number][] = [[0, i % v.cols < v.cols - 1 ? i + 1 : -1], [1, i + v.cols < v.n ? i + v.cols : -1]];
    for (const [dir, j] of neighbours) {
      if (j < 0 || !member.has(j) || v.y(j) < top - 0.015) continue;
      const [u0, w0] = inRectFrame(rect, v.x(i), v.z(i)), [u1, w1] = inRectFrame(rect, v.x(j), v.z(j));
      steps[dir]![0].push(Math.abs(u1 - u0)); steps[dir]![1].push(Math.abs(w1 - w0));
    }
  }
  const median = (list: number[]) => (list.length ? [...list].sort((a, b) => a - b)[Math.floor(list.length / 2)]! : 0);
  const along = (axis: 0 | 1) => {
    const m = [median(steps[0]![axis]), median(steps[1]![axis])].filter((step) => step > 0.002);
    return m.length ? Math.min(...m) : 0;
  };
  return [along(0), along(1)];
}

/** Least-squares circle through points (Kåsa), with how well they fit and how much of the circle they cover. */
function fitCircle(pts: P2[]): { cx: number; cz: number; r: number; rms: number; arcDeg: number } | null {
  if (pts.length < 8) return null;
  let mx = 0, mz = 0;
  for (const [px, pz] of pts) { mx += px; mz += pz; }
  mx /= pts.length; mz /= pts.length;
  let suu = 0, suv = 0, svv = 0, suuu = 0, svvv = 0, suvv = 0, svuu = 0;
  for (const [px, pz] of pts) {
    const u = px - mx, w = pz - mz;
    suu += u * u; suv += u * w; svv += w * w; suuu += u * u * u; svvv += w * w * w; suvv += u * w * w; svuu += w * u * u;
  }
  const det = suu * svv - suv * suv;
  if (Math.abs(det) < 1e-18) return null;                            // the points are on a line: a flat face
  const uc = (0.5 * (suuu + suvv) * svv - 0.5 * (svvv + svuu) * suv) / det;
  const wc = (0.5 * (svvv + svuu) * suu - 0.5 * (suuu + suvv) * suv) / det;
  const r = Math.sqrt(uc * uc + wc * wc + (suu + svv) / pts.length), cx = mx + uc, cz = mz + wc;
  let sq = 0;
  const angles: number[] = [];
  for (const [px, pz] of pts) { const d = Math.hypot(px - cx, pz - cz); sq += (d - r) * (d - r); angles.push(Math.atan2(pz - cz, px - cx)); }
  angles.sort((a, b) => a - b);
  let gap = angles[0]! + 2 * Math.PI - angles[angles.length - 1]!;
  for (let i = 1; i < angles.length; i++) gap = Math.max(gap, angles[i]! - angles[i - 1]!);
  return { cx, cz, r, rms: Math.sqrt(sq / pts.length), arcDeg: ((2 * Math.PI - gap) * 180) / Math.PI };
}

/** One body as a twin standing on a surface, or null when it is not an object to build with. */
function fit(v: View, cloud: Cloud, body: Body, surfaces: Surface[], scanId: string, opts: Options): Twin | null {
  const { cells } = body;
  if (cells.length < opts.minObjectPoints) return null;
  let sx = 0, sz = 0;
  for (const i of cells) { sx += v.x(i); sz += v.z(i); }
  const cx = sx / cells.length, cz = sz / cells.length;
  const base = surfaces.filter((s) => s.y <= body.minY + 0.03 && standsOn(s, cx, cz)).sort((a, b) => b.y - a.y)[0];
  if (!base) return null;                                            // floating: a wall, a person, a lamp
  // Something ON a table reaches down to it. Part of it may be hidden behind something in front, but a patch that
  // starts well above the top — a piece of the wall behind, seen over the table's far edge — is not standing on it.
  if (body.minY - base.y > opts.restingGap) return null;
  const ys = cells.map(v.y).sort((a, b) => a - b);
  const top = ys[Math.min(ys.length - 1, Math.floor(ys.length * 0.95))]!, height = top - base.y;
  const away = Math.hypot(cx - cloud.cam[0], base.y - cloud.cam[1], cz - cloud.cam[2]);
  if (height < opts.minObjectHeight + opts.minObjectHeightPerMetre * away) return null;   // a table rim, a shadow, noise
  // Whatever stands under a higher surface and no taller than it is that furniture's body (legs, an apron, a cabinet
  // front), or is stored under it: nothing to build with.
  if (surfaces.some((s) => s !== base && s.y > base.y && body.maxY <= s.y + 0.02 && onSurface(s, cx, cz, 0.03))) return null;

  const tight = minAreaRect(v.xz(cells)), pad = samplePad(v, cells, tight, top);
  // Seen edge-on (a card facing the camera, a pole one sample wide) a side measures zero. Nothing real is thinner than
  // MIN_SIDE, and a zero is refused by the stream's schema, which would hide every object in the scan with it.
  const rect: Rect = { ...tight, len: Math.max(MIN_SIDE, tight.len + pad[0]), wid: Math.max(MIN_SIDE, tight.wid + pad[1]) };
  if (rect.len > opts.maxObjectSize || height > opts.maxObjectSize) return null;

  // Round or square? Two tests, because a can is rarely seen whole. Seen from above, its points fill a circle and
  // leave the rectangle's corners empty. Seen from the side, its wall is an arc: fit a circle to the points below the
  // top face (the top face lies inside the circle, not on it). Clean data passes; noisy data stays a box, and the
  // label ("tall can") decides the shape later (sizes.ts).
  let corners = 0;
  for (const i of cells) {
    const [u, w] = inRectFrame(rect, v.x(i), v.z(i));
    const nu = u / (rect.len / 2), nw = w / Math.max(rect.wid / 2, 1e-6);
    if (nu * nu + nw * nw > 1.15) corners++;
  }
  const fromAbove = cells.length >= 12 && rect.len / Math.max(rect.wid, 1e-6) < 1.25 && corners / cells.length < 0.05;
  const circle = fitCircle(v.xz(cells.filter((i) => v.y(i) < top - 0.01)));
  const arc = circle !== null && circle.r >= 0.015 && circle.r <= 0.3 && circle.rms <= Math.max(0.0015, 0.04 * circle.r)
    && circle.arcDeg >= 55 && Math.abs(2 * circle.r - rect.len) <= 0.4 * rect.len ? circle : null;
  const round = fromAbove || arc !== null;
  const shape: TwinShape = round
    ? { type: "cylinder", axis: "y", diameter: Math.max(MIN_SIDE, arc ? 2 * arc.r : (rect.len + rect.wid) / 2), length: height }
    : { type: "box", size: [rect.len, height, rect.wid] };
  // A half-seen cylinder's rectangle leans toward the camera; the fitted circle's centre does not.
  const px = arc ? arc.cx : rect.cx, pz = arc ? arc.cz : rect.cz;

  let c0 = v.cols, c1 = -1, r0 = v.rows, r1 = -1;
  for (const i of cells) { const r = Math.floor(i / v.cols), c = i % v.cols; c0 = Math.min(c0, c); c1 = Math.max(c1, c); r0 = Math.min(r0, r); r1 = Math.max(r1, r); }
  const cw = cloud.width / v.cols, ch = cloud.height / v.rows;
  const distance = Math.hypot(px - cloud.cam[0], base.y + height / 2 - cloud.cam[1], pz - cloud.cam[2]);
  const error = cloud.spacingRad * distance + 0.005 + 0.01 * distance;
  // Cut off by the edge of the photo: only part of it was measured, so its size is a lower bound and nothing should rest on it.
  const clipped = c0 === 0 || r0 === 0 || c1 === v.cols - 1 || r1 === v.rows - 1;
  return {
    twin_id: "o0", name: "unknown", label: "object", shape, position: [px, base.y + height / 2, pz],
    yaw_deg: round ? 0 : rect.yawDeg, sits_on: base.surface_id, material: "other", load_bearing: false, cuttable: false,
    confidence: 0, error_m: clipped ? Math.max(error, rect.len / 2) : error, points: cells.length, distance_m: distance,
    bbox_px: [c0 * cw, r0 * ch, (c1 - c0 + 1) * cw, (r1 - r0 + 1) * ch], snapped: false, scan_ids: [scanId],
  };
}

/** Surfaces and unnamed twins (nearest first, ids o1…) from one scan. Pure and deterministic: the same scan gives the same answer. */
export function buildTwins(cloud: Cloud, scanId: string, opts: Options = OPTIONS): { surfaces: Surface[]; twins: Twin[] } {
  const v = view(cloud, opts);
  const { surfaces, isSurface } = findSurfaces(v, findLevels(v, opts), opts);
  const twins = findBodies(v, surfaces, isSurface)
    .flatMap((body) => fit(v, cloud, body, surfaces, scanId, opts) ?? [])
    .sort((a, b) => a.distance_m - b.distance_m)
    .map((t, k) => ({ ...t, twin_id: `o${k + 1}` }));
  return { surfaces, twins };
}
