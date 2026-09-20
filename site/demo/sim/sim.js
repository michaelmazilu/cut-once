/**
 * Kitbash on a laptop: the same flow the headset runs, in a browser.
 *
 * The scene stands in for your kitchen counter. Everything after that is the real thing:
 * a grid of rays is cast from the camera through the pixels of one view, the hits are grouped
 * into surfaces and objects by `pipeline.js` (the server's own thresholds), the designs are
 * stacked and balance-checked the same way, and a step only finishes when the real object is
 * actually standing inside its hologram.
 *
 * What is NOT the same, and is said on the page: the depth here is exact instead of a noisy
 * sensor, the names come from the scene instead of a model reading a photo, and Kit's words are
 * written by us rather than generated.
 */
import * as THREE from "../vendor/three.module.js";
import { OrbitControls } from "../vendor/OrbitControls.js";
import * as P from "./pipeline.js";

const COLS = 128, ROWS = 96, RAYS_PER_FRAME = 1024;
const CYAN = 0x22d3ee, CYAN_BRIGHT = 0x67e8f9, GREEN = 0x3ddc84, AMBER = 0xffc46b;
const TABLE_Y = 0.75;

/** The objects on the counter. Sizes are the real ones, in metres. */
const KIT = [
  { name: "tall_can",      label: "tall can",        kind: "cylinder", d: 0.066, h: 0.157, colour: 0x1f7a4d, x: -0.30, z:  0.02 },
  { name: "cardboard_box", label: "cardboard box",   kind: "box",      w: 0.17, h: 0.12, dp: 0.11, colour: 0xa9814f, x: -0.02, z: -0.04 },
  { name: "mug",           label: "mug",             kind: "mug",      d: 0.090, h: 0.100, colour: 0xd9dde3, x:  0.26, z:  0.05 },
  { name: "water_bottle",  label: "water bottle",    kind: "cylinder", d: 0.070, h: 0.230, colour: 0x3b5c86, x:  0.44, z: -0.10 },
  { name: "book",          label: "book",            kind: "box",      w: 0.21, h: 0.032, dp: 0.15, colour: 0x8c3b3b, x: -0.42, z: -0.16 },
  { name: "tape_roll",     label: "tape roll",       kind: "tape",     d: 0.100, h: 0.045, colour: 0xc9b072, x:  0.12, z:  0.14 },
];

export function mountSim(root) {
  const canvas = root.querySelector("#sim-canvas");
  const labelLayer = root.querySelector("#sim-labels");
  const panel = root.querySelector("#sim-body");
  const kitLine = root.querySelector("#sim-kit");
  const stagesEl = root.querySelector("#sim-stages");
  const wishEl = root.querySelector("#sim-wish");

  let renderer;
  try {
    renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: false });
  } catch {
    root.querySelector(".sim-stage").innerHTML = "<p class='sim-sorry'>This browser can't run WebGL, so the simulator won't start. The steps below explain the same flow.</p>";
    return;
  }
  renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFSoftShadowMap;

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x0a0f18);
  scene.fog = new THREE.Fog(0x0a0f18, 3.2, 7);

  // A Quest 3's camera sees about 59° from top to bottom. Same here, so the view matches.
  const camera = new THREE.PerspectiveCamera(58.7, 16 / 9, 0.05, 30);
  camera.position.set(0.05, 1.42, 0.92);

  const controls = new OrbitControls(camera, renderer.domElement);
  controls.target.set(0, 0.86, -0.02);
  controls.enablePan = false;
  controls.minDistance = 0.35; controls.maxDistance = 2.4;
  controls.maxPolarAngle = Math.PI * 0.49;
  controls.enableDamping = true; controls.dampingFactor = 0.08;

  scene.add(new THREE.HemisphereLight(0xdfefff, 0x1a2333, 1.1));
  const key = new THREE.DirectionalLight(0xfff3e2, 2.1);
  key.position.set(1.6, 2.6, 1.3); key.castShadow = true;
  key.shadow.mapSize.set(1024, 1024);
  key.shadow.camera.top = 1.4; key.shadow.camera.bottom = -1.4;
  key.shadow.camera.left = -1.4; key.shadow.camera.right = 1.4;
  scene.add(key);
  const fill = new THREE.DirectionalLight(0x9fc4ff, 0.5); fill.position.set(-1.8, 1.4, -1.1); scene.add(fill);

  /* ── the room ─────────────────────────────────────────────────────────── */
  const world = new THREE.Group(); scene.add(world);
  const mat = (colour, rough = 0.85, metal = 0.02) => new THREE.MeshStandardMaterial({ color: colour, roughness: rough, metalness: metal });

  const counter = new THREE.Mesh(new THREE.BoxGeometry(1.6, 0.06, 0.8), mat(0x2b3442, 0.6));
  counter.position.set(0, TABLE_Y - 0.03, 0); counter.receiveShadow = true; world.add(counter);
  const plinth = new THREE.Mesh(new THREE.BoxGeometry(1.5, 0.72, 0.72), mat(0x1b2431, 0.9));
  plinth.position.set(0, (TABLE_Y - 0.06) / 2, -0.02); world.add(plinth);
  const wall = new THREE.Mesh(new THREE.PlaneGeometry(6, 3), mat(0x16202e, 0.95));
  wall.position.set(0, 1.5, -0.42); wall.receiveShadow = true; world.add(wall);
  const floor = new THREE.Mesh(new THREE.PlaneGeometry(8, 8), mat(0x0e1622, 0.95));
  floor.rotation.x = -Math.PI / 2; floor.receiveShadow = true; world.add(floor);

  /* ── the things on the counter ────────────────────────────────────────── */
  const things = [];
  function makeThing(spec) {
    const group = new THREE.Group();
    const m = mat(spec.colour, spec.kind === "mug" ? 0.35 : 0.7, spec.kind === "cylinder" ? 0.35 : 0.05);
    if (spec.kind === "box") {
      const body = new THREE.Mesh(new THREE.BoxGeometry(spec.w, spec.h, spec.dp), m);
      body.position.y = spec.h / 2; group.add(body);
    } else if (spec.kind === "tape") {
      const body = new THREE.Mesh(new THREE.TorusGeometry(spec.d / 2 - 0.012, 0.012, 10, 36), m);
      body.rotation.x = Math.PI / 2; body.position.y = spec.h / 2;
      const inner = new THREE.Mesh(new THREE.CylinderGeometry(spec.d / 2 - 0.022, spec.d / 2 - 0.022, spec.h, 24), mat(0xe8e2cd, 0.9));
      inner.position.y = spec.h / 2;
      group.add(body, inner);
    } else {
      const body = new THREE.Mesh(new THREE.CylinderGeometry(spec.d / 2, spec.d / 2, spec.h, 28), m);
      body.position.y = spec.h / 2; group.add(body);
      if (spec.kind === "mug") {
        const handle = new THREE.Mesh(new THREE.TorusGeometry(0.028, 0.008, 8, 20, Math.PI * 1.3), m);
        handle.position.set(spec.d / 2, spec.h * 0.55, 0); handle.rotation.y = Math.PI / 2; group.add(handle);
      }
      if (spec.kind === "cylinder" && spec.h > 0.2) {
        const cap = new THREE.Mesh(new THREE.CylinderGeometry(spec.d / 2.6, spec.d / 2.6, 0.03, 20), mat(0x22303f, 0.7));
        cap.position.y = spec.h + 0.012; group.add(cap);
      }
    }
    group.traverse((o) => { if (o.isMesh) { o.castShadow = true; o.receiveShadow = true; } });
    group.position.set(spec.x, TABLE_Y, spec.z);
    group.userData = { spec, name: spec.name, label: spec.label, holds: spec.name !== "water_bottle", rolls: false, height: spec.h };
    world.add(group);
    things.push(group);
    return group;
  }
  KIT.forEach(makeThing);
  const homes = things.map((t) => t.position.clone());

  const pickables = [counter, wall, floor, ...things];

  /* ── hologram helpers ─────────────────────────────────────────────────── */
  const holo = new THREE.Group(); world.add(holo);
  const points = new THREE.Group(); world.add(points);
  const raysGroup = new THREE.Group(); world.add(raysGroup);

  function hologram(twinOrShape, colour = CYAN) {
    const shape = twinOrShape.shape ?? twinOrShape;
    const geo = shape.type === "cylinder"
      ? new THREE.CylinderGeometry(shape.d / 2, shape.d / 2, shape.h, 24)
      : new THREE.BoxGeometry(shape.size[0], shape.size[1], shape.size[2]);
    const group = new THREE.Group();
    group.add(new THREE.Mesh(geo, new THREE.MeshBasicMaterial({ color: colour, transparent: true, opacity: 0.16, depthWrite: false })));
    group.add(new THREE.LineSegments(new THREE.EdgesGeometry(geo), new THREE.LineBasicMaterial({ color: colour, transparent: true, opacity: 0.95 })));
    group.userData.setColour = (c) => group.traverse((o) => { if (o.material?.color) o.material.color.setHex(c); });
    return group;
  }

  /* ── the scan ─────────────────────────────────────────────────────────── */
  const raycaster = new THREE.Raycaster();
  let cloud = null, surfaces = [], twins = [], scanning = null, scanResolve = null;

  /** Resolves once the objects have been found and named. */
  function startScan() {
    clearAfterScan();
    cloud = P.makeCloud(COLS, ROWS);
    scanning = { i: 0 };
    setStage("rays");
    say("Scanning… hold still.");
    status("Casting 12,288 depth rays through one view.");
    // On a timer rather than on animation frames: a browser that isn't drawing (another tab,
    // a hidden window) still finishes the scan instead of freezing halfway.
    const timer = setInterval(() => { scanning ? scanChunk() : clearInterval(timer); }, 16);
    return new Promise((res) => { scanResolve = res; });
  }

  /** The whole scan at once, for tests. */
  function scanNow() {
    clearAfterScan();
    cloud = P.makeCloud(COLS, ROWS);
    scanning = { i: 0 };
    while (scanning) scanChunk();
    return { surfaces, twins };
  }

  function scanChunk() {
    if (!scanning) return;
    const origin = camera.position.clone();
    const dir = new THREE.Vector3();
    const end = Math.min(scanning.i + RAYS_PER_FRAME, COLS * ROWS);
    for (let i = scanning.i; i < end; i++) {
      const col = i % COLS, row = (i / COLS) | 0;
      dir.set(((col + 0.5) / COLS) * 2 - 1, 1 - ((row + 0.5) / ROWS) * 2, 0.5).unproject(camera).sub(origin).normalize();
      raycaster.set(origin, dir);
      const hit = raycaster.intersectObjects(pickables, true)[0];
      if (!hit) continue;
      cloud.hit[i] = 1;
      cloud.xyz[3 * i] = hit.point.x; cloud.xyz[3 * i + 1] = hit.point.y; cloud.xyz[3 * i + 2] = hit.point.z;
      cloud.range[i] = hit.distance;
      const n = hit.face ? hit.face.normal.clone().transformDirection(hit.object.matrixWorld) : new THREE.Vector3(0, 1, 0);
      cloud.up[i] = n.y > 0.7 ? 1 : 0;
    }
    scanning.i = end;
    drawPoints();
    if (end >= COLS * ROWS) { scanning = null; finishScan(); }
  }

  function drawPoints() {
    points.clear();
    const xyz = [];
    for (let i = 0; i < cloud.n; i++) if (cloud.hit[i]) xyz.push(cloud.xyz[3 * i], cloud.xyz[3 * i + 1], cloud.xyz[3 * i + 2]);
    const geo = new THREE.BufferGeometry();
    geo.setAttribute("position", new THREE.Float32BufferAttribute(xyz, 3));
    points.add(new THREE.Points(geo, new THREE.PointsMaterial({ color: CYAN_BRIGHT, size: 0.005, sizeAttenuation: true })));
  }

  function finishScan() {
    surfaces = P.findSurfaces(cloud);
    twins = P.findObjects(cloud, surfaces);
    P.nameTwins(twins, (t) => {
      // The scene knows what each thing is; the rays measured how big it is.
      let best = null, bestGap = 0.12;
      for (const thing of things) {
        const gap = Math.hypot(thing.position.x - t.position[0], thing.position.z - t.position[2]);
        if (gap < bestGap) { bestGap = gap; best = thing; }
      }
      return best ? best.userData : null;
    });
    setStage("twins");
    status(`${twins.length} object${twins.length === 1 ? "" : "s"} on ${surfaces.length} surface, measured by the rays.`);
    drawTwins();
    setTimeout(() => {
      setStage("names"); listObjects(); say(kitSeesLine());
      const done = scanResolve; scanResolve = null; done?.();
    }, 700);
  }

  function drawTwins() {
    holo.clear();
    for (const t of twins) {
      const g = hologram(t);
      g.position.set(t.position[0], t.position[1], t.position[2]);
      g.userData.twin = t;
      holo.add(g);
    }
    setTimeout(() => { points.visible = false; }, 900);
  }

  const kitSeesLine = () => {
    const names = twins.map((t) => t.label);
    const last = names.pop();
    return `I can see ${names.length ? names.join(", ") + " and " + last : last}. Ask me what you can build.`;
  };

  /* ── designs ──────────────────────────────────────────────────────────── */
  let ideas = [], chosen = null, stepIndex = 0, ghosts = [];

  function ask(wish) {
    if (!twins.length) { say("Scan the counter first, then ask me again."); return; }
    setStage("design");
    say("Thinking…");
    status("Fitting designs to the objects it measured, then checking each one's balance.");
    panel.innerHTML = "<p class='sim-thinking'>Kit is working it out…</p>";
    setTimeout(() => {
      ideas = P.designsFor(twins, wish).filter((d) => d.stable);
      if (!ideas.length) { say("Nothing on this table stacks up safely. Try the shuffle button and scan again."); listObjects(); return; }
      say(`${ideas.length === 1 ? "One idea" : ideas.length + " ideas"} from exactly what's on your counter. Pick one.`);
      listIdeas();
    }, 650);
  }

  function pick(idea) {
    chosen = idea; stepIndex = 0;
    setStage("build");
    holo.clear(); ghosts = [];
    idea.pieces.forEach((piece, i) => {
      const g = hologram(piece.twin);
      const thing = thingFor(piece.twin);
      g.position.copy(thing.position).add(new THREE.Vector3(0, P.height(piece.twin) / 2, 0));
      g.userData.target = new THREE.Vector3(piece.x, piece.bottom + P.height(piece.twin) / 2, piece.z);
      g.userData.t = -i * 0.3;                       // the pieces fly one after another
      g.userData.piece = piece;
      holo.add(g); ghosts.push(g);
    });
    status(`"${idea.title}" — ${idea.reason}.`);
    say("Watch where each piece goes.");
    setTimeout(() => { stepIndex = 0; announceStep(); }, 1500);
    listSteps();
  }

  const thingFor = (twin) => things.find((t) => t.userData.name === twin.truth && !t.userData.used)
    ?? things.find((t) => t.userData.name === twin.truth) ?? things[0];

  function announceStep() {
    const step = chosen.steps[stepIndex];
    if (!step) return;
    say(step.text);
    speak(step.text);
    listSteps();
    status("Drag the real object into the glowing hologram.");
  }

  /** Called after a drag: is the real thing standing where the hologram is? */
  function checkPlacement(thing) {
    if (!chosen) return;
    const step = chosen.steps[stepIndex];
    if (!step) return;
    const piece = chosen.pieces[stepIndex];
    if (thing.userData.name !== piece.twin.truth) return;
    const test = P.placementOk(piece, { x: thing.position.x, z: thing.position.z, bottom: thing.position.y });
    if (!test.ok) {
      say(`Not quite: ${(test.off * 100).toFixed(0)} cm off. Slide it into the hologram.`);
      return;
    }
    thing.position.set(piece.x, piece.bottom, piece.z);
    const ghost = ghosts[stepIndex];
    ghost.userData.setColour(GREEN);
    ghost.userData.built = true;
    thing.userData.used = true;
    stepIndex++;
    if (stepIndex >= chosen.steps.length) {
      setStage("done");
      say(`That's your ${chosen.title.toLowerCase()}. Half real, half hologram, and it stands up.`);
      speak("Done. Nice build.");
      status("Every step placed. Shuffle the counter and try again, or ask Kit for something else.");
      listSteps();
      return;
    }
    say("Good. Next piece.");
    setTimeout(announceStep, 500);
  }

  /* ── dragging ─────────────────────────────────────────────────────────── */
  const pointer = new THREE.Vector2();
  const plane = new THREE.Plane();
  let dragging = null;

  const pointerAt = (e) => {
    const r = renderer.domElement.getBoundingClientRect();
    pointer.set(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
  };

  renderer.domElement.addEventListener("pointerdown", (e) => {
    pointerAt(e);
    raycaster.setFromCamera(pointer, camera);
    const hit = raycaster.intersectObjects(things, true)[0];
    if (!hit) return;
    let group = hit.object; while (group.parent && !things.includes(group)) group = group.parent;
    if (!things.includes(group)) return;
    dragging = group;
    // While a step is running, lift the piece to the height its hologram waits at.
    const piece = chosen?.pieces[stepIndex];
    const liftTo = piece && piece.twin.truth === group.userData.name ? piece.bottom : group.position.y;
    plane.setFromNormalAndCoplanarPoint(new THREE.Vector3(0, 1, 0), new THREE.Vector3(0, liftTo, 0));
    group.userData.liftTo = liftTo;
    controls.enabled = false;
    renderer.domElement.setPointerCapture(e.pointerId);
    root.querySelector("#sim-hint").textContent = "drop it inside the hologram";
  });

  renderer.domElement.addEventListener("pointermove", (e) => {
    if (!dragging) return;
    pointerAt(e);
    raycaster.setFromCamera(pointer, camera);
    const point = new THREE.Vector3();
    if (!raycaster.ray.intersectPlane(plane, point)) return;
    dragging.position.set(P.clamp(point.x, -0.75, 0.75), dragging.userData.liftTo, P.clamp(point.z, -0.36, 0.36));
  });

  const drop = () => {
    if (!dragging) return;
    const thing = dragging; dragging = null; controls.enabled = true;
    root.querySelector("#sim-hint").textContent = "drag to look · scroll to zoom · drag an object to move it";
    checkPlacement(thing);
    if (!thing.userData.used) settle(thing);
  };
  renderer.domElement.addEventListener("pointerup", drop);
  renderer.domElement.addEventListener("pointercancel", drop);

  /** Nothing floats: a dropped thing lands on whatever is under it. */
  function settle(thing) {
    let top = TABLE_Y;
    for (const other of things) {
      if (other === thing || !other.userData.used) continue;
      const gap = Math.hypot(other.position.x - thing.position.x, other.position.z - thing.position.z);
      if (gap < 0.09) top = Math.max(top, other.position.y + other.userData.height);
    }
    thing.position.y = top;
  }

  /* ── the panel ────────────────────────────────────────────────────────── */
  const STAGES = [["photo", "Photo"], ["rays", "Rays"], ["twins", "Twins"], ["names", "Names"], ["design", "Design"], ["build", "Build"], ["done", "Built"]];
  let stage = "photo";
  function setStage(next) {
    stage = next;
    stagesEl.innerHTML = STAGES.map(([k, l], i) =>
      `<span class="sim-chip${k === stage ? " now" : STAGES.findIndex(([s]) => s === stage) > i ? " past" : ""}">${l}</span>`).join("");
  }
  const say = (text) => { kitLine.innerHTML = `<b>Kit</b> ${text}`; };
  const status = (text) => { root.querySelector("#sim-status").textContent = text; };

  function listObjects() {
    panel.innerHTML = `<h4>What it measured</h4><ul class="sim-list">` + twins.map((t) =>
      `<li><span class="sim-name">${t.label}</span><span class="sim-size">${P.sizeText(t)}</span>${t.snapped ? '<span class="sim-flag">snapped to a known size</span>' : ""}</li>`
    ).join("") + `</ul><p class="sim-note">Sizes come from the rays. Try “birdhouse”, “tower” or “something crazier”.</p>`;
  }

  function listIdeas() {
    panel.innerHTML = `<h4>What you could build</h4>` + ideas.map((idea, i) =>
      `<button class="sim-idea" data-idea="${i}"><span class="sim-idea-t">${idea.title}</span><span class="sim-idea-w">${idea.why}</span>` +
      `<span class="sim-idea-m">uses ${idea.uses.length} of your objects · ${idea.reason}</span></button>`).join("");
    panel.querySelectorAll(".sim-idea").forEach((b) => b.addEventListener("click", () => pick(ideas[+b.dataset.idea])));
  }

  function listSteps() {
    panel.innerHTML = `<h4>${chosen.title}</h4><ol class="sim-steps">` + chosen.steps.map((s, i) =>
      `<li class="${i < stepIndex ? "done" : i === stepIndex ? "now" : ""}">${s.text}</li>`).join("") + `</ol>`;
  }

  /* ── controls ─────────────────────────────────────────────────────────── */
  let sound = false;
  const speak = (text) => {
    if (!sound || !window.speechSynthesis) return;
    const u = new SpeechSynthesisUtterance(text);
    u.rate = 1.05; u.pitch = 1;
    speechSynthesis.cancel(); speechSynthesis.speak(u);
  };

  function clearAfterScan() {
    holo.clear(); points.clear(); raysGroup.clear();
    points.visible = true;
    twins = []; surfaces = []; ideas = []; chosen = null; ghosts = []; stepIndex = 0;
    things.forEach((t) => { t.userData.used = false; });
    panel.innerHTML = "";
    labelLayer.innerHTML = "";
  }

  function resetTable() {
    things.forEach((t, i) => t.position.copy(homes[i]));
    clearAfterScan();
    setStage("photo");
    say("Ready. Press scan when you are.");
    status("The counter is set. Nothing here knows what is on it until it looks.");
  }

  function shuffle() {
    const spots = [];
    for (const thing of things) {
      let x, z, tries = 0;
      do {
        x = -0.6 + Math.random() * 1.2; z = -0.28 + Math.random() * 0.52; tries++;
      } while (tries < 40 && spots.some((s) => Math.hypot(s.x - x, s.z - z) < 0.17));
      spots.push({ x, z });
      thing.position.set(x, TABLE_Y, z);
    }
    clearAfterScan();
    setStage("photo");
    say("Moved everything. Scan again and see.");
    status("Nothing is staged: the objects are somewhere new and nothing has looked yet.");
  }

  root.querySelector("#sim-scan").addEventListener("click", startScan);
  root.querySelector("#sim-shuffle").addEventListener("click", shuffle);
  root.querySelector("#sim-reset").addEventListener("click", resetTable);
  root.querySelector("#sim-ask").addEventListener("click", () => ask(wishEl.value.trim()));
  wishEl.addEventListener("keydown", (e) => { if (e.key === "Enter") ask(wishEl.value.trim()); });
  root.querySelectorAll("[data-wish]").forEach((b) => b.addEventListener("click", () => { wishEl.value = b.dataset.wish; ask(b.dataset.wish); }));
  const soundBtn = root.querySelector("#sim-sound");
  soundBtn.addEventListener("click", () => {
    sound = !sound;
    soundBtn.textContent = sound ? "Sound on" : "Sound off";
    soundBtn.setAttribute("aria-pressed", String(sound));
    if (sound) speak("Kit here. I read every step out loud.");
  });

  /* ── the demo runs itself ─────────────────────────────────────────────────
   * The same buttons a person would press, pressed in order, with the objects
   * carried into their holograms instead of dragged. Touching anything stops it.
   */
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const easeInOut = (x) => (x < 0.5 ? 4 * x * x * x : 1 - Math.pow(-2 * x + 2, 3) / 2);

  function carry(thing, to, ms) {
    return new Promise((res) => {
      const from = thing.position.clone(), t0 = performance.now();
      const step = () => {
        const k = Math.min(1, (performance.now() - t0) / ms);
        thing.position.lerpVectors(from, to, easeInOut(k));
        k < 1 ? requestAnimationFrame(step) : res();
      };
      step();
    });
  }

  let auto = null;
  const playBtn = root.querySelector("#sim-play");

  function stopAuto() {
    if (!auto) return;
    auto.cancelled = true; auto = null;
    controls.autoRotate = false;
    playBtn.textContent = "Play the demo"; playBtn.disabled = false;
  }

  async function playDemo() {
    stopAuto();
    const run = (auto = { cancelled: false });
    const alive = () => !run.cancelled;
    playBtn.textContent = "Playing…"; playBtn.disabled = true;

    try {
      resetTable();
      say("Watch. One photo, then twelve thousand depth rays.");
      await sleep(1100); if (!alive()) return;

      await startScan(); if (!alive()) return;          // rays, twins, names
      await sleep(1200); if (!alive()) return;

      say("Now I ask Kit what I can build from these.");
      await sleep(900); if (!alive()) return;
      ask(""); await sleep(1500); if (!alive()) return;
      if (!ideas.length) return;

      pick(ideas[0]);
      controls.autoRotate = true; controls.autoRotateSpeed = 0.45;
      await sleep(2400); if (!alive()) return;

      while (chosen && stepIndex < chosen.steps.length && alive()) {
        const piece = chosen.pieces[stepIndex], thing = thingFor(piece.twin);
        const over = new THREE.Vector3(piece.x, piece.bottom + 0.10, piece.z);
        await carry(thing, new THREE.Vector3(thing.position.x, piece.bottom + 0.10, thing.position.z), 380); if (!alive()) return;
        await carry(thing, over, 820); if (!alive()) return;
        await carry(thing, new THREE.Vector3(piece.x, piece.bottom, piece.z), 320); if (!alive()) return;
        checkPlacement(thing);
        await sleep(1200);
      }
      if (!alive()) return;
      controls.autoRotate = false;
      status("That was the whole flow. Press shuffle, then play again: the objects move and the design is fitted to where they are now.");
    } finally {
      if (auto === run) { auto = null; controls.autoRotate = false; }
      playBtn.textContent = "Play again"; playBtn.disabled = false;
    }
  }

  playBtn.addEventListener("click", playDemo);
  renderer.domElement.addEventListener("pointerdown", stopAuto);
  ["#sim-scan", "#sim-shuffle", "#sim-reset", "#sim-ask"].forEach((sel) =>
    root.querySelector(sel).addEventListener("click", stopAuto, true));

  // Start once, when the scene itself is on screen. Watching the whole section would never
  // fire: it is taller than most windows, so a large visible fraction is impossible.
  if ("IntersectionObserver" in window) {
    const io = new IntersectionObserver((entries) => {
      for (const e of entries) if (e.isIntersecting) { io.disconnect(); playDemo(); }
    }, { threshold: 0.3 });
    io.observe(root.querySelector(".sim-stage"));
  }

  /* ── labels over the objects ──────────────────────────────────────────── */
  function drawLabels() {
    const wanted = stage === "names" || stage === "design" || stage === "build" || stage === "done";
    if (!wanted || !twins.length) { if (labelLayer.childElementCount) labelLayer.innerHTML = ""; return; }
    while (labelLayer.childElementCount < twins.length) {
      const el = document.createElement("span"); el.className = "sim-label"; labelLayer.appendChild(el);
    }
    const rect = renderer.domElement.getBoundingClientRect();
    twins.forEach((t, i) => {
      const el = labelLayer.children[i];
      const v = new THREE.Vector3(t.position[0], t.base + P.height(t) + 0.045, t.position[2]).project(camera);
      const on = v.z < 1;
      el.style.display = on ? "block" : "none";
      el.textContent = `${t.label} · ${P.sizeText(t)}`;
      el.style.left = `${((v.x + 1) / 2) * rect.width}px`;
      el.style.top = `${((1 - v.y) / 2) * rect.height}px`;
    });
  }

  /* ── loop ─────────────────────────────────────────────────────────────── */
  const clock = new THREE.Clock();
  function resize() {
    const rect = renderer.domElement.parentElement.getBoundingClientRect();
    renderer.setSize(rect.width, rect.height, false);
    camera.aspect = rect.width / Math.max(rect.height, 1);
    camera.updateProjectionMatrix();
  }
  addEventListener("resize", resize);
  resize();

  function frame() {
    const dt = clock.getDelta(), t = clock.elapsedTime;
    for (const g of ghosts) {
      if (g.userData.built) continue;
      g.userData.t = Math.min(1, (g.userData.t ?? 0) + dt / 0.7);
      if (g.userData.t > 0) {
        const k = easeOut(g.userData.t);
        g.position.lerpVectors(g.position, g.userData.target, Math.min(1, k * 0.35 + 0.08));
      }
    }
    const current = ghosts[stepIndex];
    for (const g of ghosts) {
      const lit = g === current && !g.userData.built;
      g.children.forEach((child) => {
        if (child.material) child.material.opacity = child.isLineSegments
          ? (lit ? 0.7 + 0.3 * Math.sin(t * 5) : 0.5)
          : (lit ? 0.18 + 0.1 * Math.sin(t * 5) : 0.1);
      });
    }

    controls.update();
    drawLabels();
    renderer.render(scene, camera);
    requestAnimationFrame(frame);
  }
  const easeOut = (x) => 1 - Math.pow(1 - Math.min(1, Math.max(0, x)), 3);

  // A handle for poking at the run from the console: __kitbash.twins, .ideas, .chosen.
  window.__kitbash = {
    get twins() { return twins; }, get surfaces() { return surfaces; },
    get ideas() { return ideas; }, get chosen() { return chosen; },
    things, play: playDemo, scan: startScan, scanNow, ask,
    /** Renders a frame and counts what it drew: used by the checks, handy when something looks wrong. */
    snapshot() {
      renderer.render(scene, camera);
      const c = document.createElement("canvas"); c.width = 320; c.height = 200;
      const g = c.getContext("2d"); g.drawImage(renderer.domElement, 0, 0, 320, 200);
      const d = g.getImageData(0, 0, 320, 200).data;
      let lit = 0, cyan = 0, green = 0;
      for (let i = 0; i < d.length; i += 4) {
        const r = d[i], gg = d[i + 1], b = d[i + 2];
        if (r + gg + b > 90) lit++;
        if (b > 120 && gg > 110 && r < 120) cyan++;
        if (gg > 120 && r < 120 && b < 130) green++;
      }
      return { lit, cyan, green, of: 320 * 200 };
    },
  };

  resetTable();
  frame();
}
