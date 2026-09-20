/**
 * The steps the Kitbash server runs, ported to the browser so the whole flow works on a laptop.
 *
 * Nothing here knows about three.js or the page: it takes a grid of ray hits and returns surfaces,
 * digital twins and designs, with the same thresholds the real build mode uses:
 *
 *   a surface   60 points and 45 cm across          (services/api/src/build/twins.ts)
 *   an object   8 points and 1.5 cm of height       (same)
 *   neighbours  3 cm apart, more at a distance      (same)
 *   a design    pieces meet within 2 mm, cylinders stay upright, and the weight of everything
 *               above a piece has to land inside what holds it   (build/solver.ts, build/stability.ts)
 *
 * Distances are metres, the same as the plans the headset draws.
 */

export const OPTIONS = {
  surfaceBand: 0.015,
  levelSeparation: 0.03,
  minSurfacePoints: 60,
  minSurfaceWidth: 0.45,
  minObjectPoints: 8,
  minObjectHeight: 0.015,
  minObjectHeightPerMetre: 0.012,
  touch: 0.002,          // how closely pieces have to meet
  balanceMargin: 0.01,   // how far inside its support the weight has to land
  // The headset looks at a table, so it never sees much else. A browser scene has a floor and
  // walls in shot as well, and a wall is "above the floor": these two keep them out.
  maxObjectHeight: 0.6,
  maxObjectWidth: 0.5,
};

/** Standard sizes, the short version of data/build/vocabulary.json. Metres. */
export const VOCAB = [
  { name: "tall_can",      label: "tall can",          shape: "cylinder", d: 0.066, h: 0.157, holds: true },
  { name: "drink_can",     label: "drink can",         shape: "cylinder", d: 0.066, h: 0.122, holds: true },
  { name: "energy_can",    label: "energy drink can",  shape: "cylinder", d: 0.053, h: 0.135, holds: true },
  { name: "water_bottle",  label: "water bottle",      shape: "cylinder", d: 0.070, h: 0.230, holds: false },
  { name: "mug",           label: "mug",               shape: "cylinder", d: 0.090, h: 0.100, holds: true },
  { name: "tape_roll",     label: "tape roll",         shape: "cylinder", d: 0.100, h: 0.045, holds: true },
  { name: "cardboard_box", label: "cardboard box",     shape: "box",      holds: true },
  { name: "book",          label: "book",              shape: "box",      holds: true },
];

const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

/* ── 1. the point cloud ──────────────────────────────────────────────────────
 * Cell i is the ray through pixel i of the photo, so neighbouring cells are
 * neighbouring pixels. That is what lets one object's points be found by walking
 * the grid instead of searching the whole cloud.
 */
export function makeCloud(cols, rows) {
  const n = cols * rows;
  return { cols, rows, n, hit: new Uint8Array(n), xyz: new Float32Array(3 * n),
           up: new Uint8Array(n), range: new Float32Array(n) };
}

export const at = (cloud, i) => ({ x: cloud.xyz[3 * i], y: cloud.xyz[3 * i + 1], z: cloud.xyz[3 * i + 2] });

/* ── 2. surfaces ─────────────────────────────────────────────────────────────
 * Points that face up and share a height are one level. The biggest level wide
 * enough to be furniture is the table; a pizza box lid is not.
 */
export function findSurfaces(cloud, opts = OPTIONS) {
  const bins = new Map();
  for (let i = 0; i < cloud.n; i++) {
    if (!cloud.hit[i] || !cloud.up[i]) continue;
    const b = Math.round(cloud.xyz[3 * i + 1] / 0.01);
    (bins.get(b) ?? bins.set(b, []).get(b)).push(i);
  }
  const levels = [...bins.entries()]
    .map(([b, cells]) => ({ b, cells: gather(bins, b, opts.levelSeparation) }))
    .filter((l) => l.cells.length >= opts.minSurfacePoints)
    .sort((a, b) => b.cells.length - a.cells.length);

  const out = [], taken = [];
  for (const level of levels) {
    // The height of a level is the average of its own points, not the bin it was found in,
    // or a table reads a couple of centimetres high and everything on it measures short.
    const ys = level.cells.map((i) => cloud.xyz[3 * i + 1]);
    const y = ys.reduce((a, b) => a + b, 0) / ys.length;
    if (taken.some((t) => Math.abs(t - y) <= opts.levelSeparation)) continue;
    const xs = level.cells.map((i) => cloud.xyz[3 * i]), zs = level.cells.map((i) => cloud.xyz[3 * i + 2]);
    const min = [Math.min(...xs), Math.min(...zs)], max = [Math.max(...xs), Math.max(...zs)];
    if (max[0] - min[0] < opts.minSurfaceWidth || max[1] - min[1] < opts.minSurfaceWidth) continue;
    taken.push(y);
    out.push({ id: `s${out.length + 1}`, y, min, max, points: level.cells.length });
  }
  return out;
}

const gather = (bins, b, sep) => {
  const reach = Math.round(sep / 0.01), cells = [];
  for (let k = -reach; k <= reach; k++) cells.push(...(bins.get(b + k) ?? []));
  return cells;
};

/* ── 3. objects ──────────────────────────────────────────────────────────────
 * Anything standing on a surface, found by walking grid neighbours. The gap two
 * neighbouring cells may have grows with distance, because rays spread out.
 */
export function findObjects(cloud, surfaces, opts = OPTIONS) {
  if (!surfaces.length) return [];
  const seen = new Uint8Array(cloud.n), out = [];
  const spacing = (i) => Math.max(0.03, 0.02 * cloud.range[i]);

  // Things are built on the highest surface in view: the counter, not the floor.
  const bench = [...surfaces].sort((a, b) => b.y - a.y)[0];

  const standsOn = (i) => {
    const y = cloud.xyz[3 * i + 1], x = cloud.xyz[3 * i], z = cloud.xyz[3 * i + 2];
    const inside = x >= bench.min[0] - 0.02 && x <= bench.max[0] + 0.02 && z >= bench.min[1] - 0.02 && z <= bench.max[1] + 0.02;
    const clear = Math.max(opts.minObjectHeight, opts.minObjectHeightPerMetre * cloud.range[i]);
    const above = y > bench.y + clear && y < bench.y + opts.maxObjectHeight;
    return inside && above ? bench : null;
  };

  for (let start = 0; start < cloud.n; start++) {
    if (seen[start] || !cloud.hit[start]) continue;
    const surface = standsOn(start);
    if (!surface) { seen[start] = 1; continue; }

    const cells = [], stack = [start];
    seen[start] = 1;
    while (stack.length) {
      const i = stack.pop();
      cells.push(i);
      const col = i % cloud.cols, row = (i / cloud.cols) | 0;
      for (let dc = -1; dc <= 1; dc++) for (let dr = -1; dr <= 1; dr++) {
        const c = col + dc, r = row + dr;
        if (c < 0 || r < 0 || c >= cloud.cols || r >= cloud.rows) continue;
        const j = r * cloud.cols + c;
        if (seen[j] || !cloud.hit[j] || !standsOn(j)) continue;
        const a = at(cloud, i), b = at(cloud, j);
        const gap = Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);
        if (gap > spacing(i)) continue;
        seen[j] = 1; stack.push(j);
      }
    }
    if (cells.length < opts.minObjectPoints) continue;
    const twin = fit(cloud, cells, surface, out.length);
    const [w, d] = footprint(twin);
    if (Math.max(w, d) > opts.maxObjectWidth) continue;   // that is furniture, not a thing on it
    twin.id = `o${out.length + 1}`;
    out.push(twin);
  }
  return out;
}

/** A cluster of points becomes a box or an upright cylinder: round seen from above means a cylinder. */
function fit(cloud, cells, surface, index) {
  const xs = [], ys = [], zs = [];
  for (const i of cells) { xs.push(cloud.xyz[3 * i]); ys.push(cloud.xyz[3 * i + 1]); zs.push(cloud.xyz[3 * i + 2]); }
  const cx = xs.reduce((a, b) => a + b, 0) / xs.length, cz = zs.reduce((a, b) => a + b, 0) / zs.length;
  const width = Math.max(...xs) - Math.min(...xs), depth = Math.max(...zs) - Math.min(...zs);
  const top = Math.max(...ys), height = Math.max(top - surface.y, 0.015);

  // Round from above: every point sits about the same distance from the middle.
  const radii = cells.map((i) => Math.hypot(cloud.xyz[3 * i] - cx, cloud.xyz[3 * i + 2] - cz));
  const rMean = radii.reduce((a, b) => a + b, 0) / radii.length;
  const spread = Math.sqrt(radii.reduce((a, r) => a + (r - rMean) ** 2, 0) / radii.length) / (rMean || 1);
  const squareish = Math.abs(width - depth) < 0.25 * Math.max(width, depth);
  const round = squareish && spread < 0.30;

  const shape = round
    ? { type: "cylinder", d: (width + depth) / 2, h: height }
    : { type: "box", size: [width, height, depth] };

  return {
    id: `o${index + 1}`, shape, points: cells.length, cells,
    position: [cx, surface.y + height / 2, cz], base: surface.y, sits_on: surface.id,
    name: "unknown", label: "unknown", holds: true, snapped: false, truth: null,
  };
}

/* ── 4. names and standard sizes ─────────────────────────────────────────────
 * On the headset a model reads the photo and names what it sees. In the browser
 * the scene already knows what each object is, so the name is handed over and only
 * the size is measured. A name that matches a known item snaps to its exact size.
 */
export function nameTwins(twins, truthFor) {
  for (const t of twins) {
    const truth = truthFor(t);
    const item = truth ? VOCAB.find((v) => v.name === truth.name) : null;
    t.truth = truth ? truth.name : null;
    t.label = item ? item.label : truth ? truth.label : "unknown object";
    t.name = item ? item.name : "other";
    t.holds = item ? item.holds : truth ? truth.holds !== false : true;
    t.rolls = truth ? !!truth.rolls : false;

    if (item && item.shape === "cylinder" && item.d) {
      const measured = t.shape.type === "cylinder" ? t.shape.d : Math.max(t.shape.size[0], t.shape.size[2]);
      if (Math.abs(measured - item.d) < 0.03) {
        t.shape = { type: "cylinder", d: item.d, h: item.h };
        t.position[1] = t.base + item.h / 2;
        t.snapped = true;
      }
    }
  }
  return twins;
}

export const topOf = (t) => t.base + height(t);
export const height = (t) => (t.shape.type === "cylinder" ? t.shape.h : t.shape.size[1]);
export const footprint = (t) => (t.shape.type === "cylinder" ? [t.shape.d, t.shape.d] : [t.shape.size[0], t.shape.size[2]]);
export const area = (t) => { const [w, d] = footprint(t); return w * d; };

/* ── 5. designs ──────────────────────────────────────────────────────────────
 * Every piece of a design is an object already on the table. Pieces are stacked
 * middle over middle, each resting on the one below, and the stack is only offered
 * if the weight above each piece lands inside what holds it.
 */
const TEMPLATES = [
  { key: "birdhouse", title: "Birdhouse on a post", why: "A can for the post and a box for the house: put it by a window and watch.",
    wants: [{ role: "post", want: "cylinder", tall: true, holds: true }, { role: "house", want: "box" }] },
  { key: "tower", title: "Tower", why: "Everything you have, biggest at the bottom, as tall as it will safely go.",
    wants: [{ want: "any" }, { want: "any" }, { want: "any" }] },
  { key: "stand", title: "Display stand", why: "A box for the base and something round on top, so the small thing gets the spotlight.",
    wants: [{ want: "box" }, { want: "cylinder" }] },
  { key: "lookout", title: "Lookout tower", why: "The tallest thing you own, standing on the widest thing you own.",
    wants: [{ want: "box" }, { want: "cylinder", tall: true }, { want: "any", small: true }] },
];

export function designsFor(twins, wish) {
  const want = (wish || "").toLowerCase();
  const order = [...TEMPLATES].sort((a, b) => score(b, want) - score(a, want));
  const out = [];
  for (const template of order) {
    const design = build(template, twins);
    if (design) out.push(design);
    if (out.length === 3) break;
  }
  return out;
}

const score = (t, want) => (want && (want.includes(t.key) || t.title.toLowerCase().includes(want)) ? 10 : 0)
  + (want.includes("crazy") || want.includes("crazier") || want.includes("tall") ? (t.key === "lookout" || t.key === "tower" ? 5 : 0) : 0);

function build(template, twins) {
  const free = [...twins].sort((a, b) => area(b) - area(a));
  const picked = [];
  for (const wants of template.wants) {
    const match = free.find((t) => !picked.includes(t)
      && (wants.want === "any" || t.shape.type === wants.want)
      && (!wants.tall || height(t) > 0.12)
      && (!wants.small || area(t) < 0.02)
      && (!wants.holds || t.holds)
      && !t.rolls);
    if (!match) return null;
    picked.push(match);
  }
  if (picked.length < 2) return null;

  // Stack them: widest at the bottom, each piece centred on the one below.
  const stack = template.key === "birdhouse" ? picked : [...picked].sort((a, b) => area(b) - area(a));
  const base = stack[0];
  const pieces = [];
  let y = base.base;
  for (const t of stack) {
    pieces.push({ twin: t, x: base.position[0], z: base.position[2], bottom: y, top: y + height(t) });
    y += height(t);
  }
  const check = balance(pieces);
  return {
    id: `idea_${template.key}`, key: template.key, title: template.title, why: template.why,
    pieces, uses: stack.map((t) => t.id), stable: check.ok, reason: check.reason,
    steps: pieces.map((p, i) => ({
      n: i + 1, twinId: p.twin.id,
      text: i === 0 ? `Put the ${p.twin.label} where the hologram is.` : `Stand the ${p.twin.label} on the ${pieces[i - 1].twin.label}.`,
    })),
  };
}

/** Everything above a piece has to press down inside what holds it, with a margin. */
export function balance(pieces, opts = OPTIONS) {
  for (let i = 0; i < pieces.length - 1; i++) {
    const support = pieces[i], above = pieces.slice(i + 1);
    const mass = above.reduce((a, p) => a + area(p.twin) * height(p.twin), 0);
    if (mass <= 0) continue;
    const cx = above.reduce((a, p) => a + p.x * area(p.twin) * height(p.twin), 0) / mass;
    const cz = above.reduce((a, p) => a + p.z * area(p.twin) * height(p.twin), 0) / mass;
    const [w, d] = footprint(support.twin);
    const insideX = Math.abs(cx - support.x) <= w / 2 - opts.balanceMargin;
    const insideZ = Math.abs(cz - support.z) <= d / 2 - opts.balanceMargin;
    if (!(insideX && insideZ)) return { ok: false, reason: `the weight lands outside the ${support.twin.label}` };
    if (!support.twin.holds) return { ok: false, reason: `the ${support.twin.label} cannot hold anything up` };
  }
  return { ok: true, reason: "the weight lands on its support at every level" };
}

/* ── 6. is the real thing in the hologram? ───────────────────────────────────
 * On the headset this is depth rays through the hologram's box: if they stop inside
 * it, something real is standing there. Here the object's own place is known, so the
 * same question is asked directly, with the same tolerance.
 */
export function placementOk(piece, pose, tol = 0.025) {
  const flat = Math.hypot(pose.x - piece.x, pose.z - piece.z);
  const bottom = Math.abs(pose.bottom - piece.bottom);
  return { ok: flat <= tol && bottom <= tol, off: flat, lift: bottom };
}

export const fmtCm = (m) => (m * 100).toFixed(1).replace(/\.0$/, "");
export function sizeText(t) {
  return t.shape.type === "cylinder"
    ? `${fmtCm(t.shape.d)} × ${fmtCm(t.shape.h)} cm`
    : `${fmtCm(t.shape.size[0])} × ${fmtCm(t.shape.size[2])} × ${fmtCm(t.shape.size[1])} cm`;
}
export { clamp };
