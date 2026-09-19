import * as THREE from "three";
import { GLTFLoader, type GLTF } from "three/examples/jsm/loaders/GLTFLoader.js";
import { RGBELoader } from "three/examples/jsm/loaders/RGBELoader.js";
import scene from "../../../../data/build/scenes/kitchen.json";

/**
 * The test kitchen, built from data/build/scenes/kitchen.json. The kitchen is the "real room": it is what the pretend
 * headset photographs and measures. Holograms (twins, designs, labels) live in a separate scene so they never show up
 * in a scan. Plan frame = three.js frame (right-handed, +Y up, metres), so nothing is converted.
 */

type Vec3 = [number, number, number];
interface Texture { name: string; tile: number }
interface Common {
  id: string; base: Vec3; yaw?: number; color: string; opacity?: number; print?: string; depth?: boolean;
  model?: string | null; fit?: "stretch" | "height"; hole?: number; texture?: Texture; roughness?: number; top_image?: string;
}
export type Shape =
  | (Common & { kind: "box"; size: Vec3 })
  | (Common & { kind: "cylinder"; diameter: number; height: number })
  | (Common & { kind: "sphere"; diameter: number });
export type KitchenObject = Shape & { truth: string | null; label: string };
export type RoomPiece = Shape & { surface: string };
export interface KitchenFile { version: 1; name: string; camera: { position: Vec3; target: Vec3 }; room: RoomPiece[]; objects: KitchenObject[] }

export const KITCHEN = scene as unknown as KitchenFile;

/** Where on the page models live: apps/web/public/kitchen/<file>. */
export const MODEL_ROOT = "/kitchen/";

// The same texture image is used by several materials (each face tiles it differently): load and decode it once.
THREE.Cache.enabled = true;

export interface BuiltKitchen {
  /** The real room: photographed and measured by a scan. */
  root: THREE.Group;
  /** Objects a depth sensor does not see (clear plastic): hidden during the depth pass only. */
  depthInvisible: THREE.Object3D[];
}

export const heightOf = (s: Shape) => (s.kind === "box" ? s.size[1] : s.kind === "cylinder" ? s.height : s.diameter);

/** A printed label on a box top or a can's side, so a vision model reads the object as a real one. */
function printTexture(text: string, background: string, ink = "#ffffff"): THREE.CanvasTexture {
  const c = document.createElement("canvas");
  c.width = 512; c.height = 256;
  const g = c.getContext("2d")!;
  g.fillStyle = background; g.fillRect(0, 0, c.width, c.height);
  g.fillStyle = ink; g.font = "bold 96px Helvetica, Arial, sans-serif"; g.textAlign = "center"; g.textBaseline = "middle";
  g.fillText(text, c.width / 2, c.height / 2);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}

const textures = new THREE.TextureLoader();

/**
 * A Poly Haven material (colour, normal, and ambient-occlusion/roughness/metal packed as R/G/B) tiled across a face of
 * `u` × `v` metres, tinted by `tint`.
 */
function surface(t: Texture, u: number, v: number, tint: string): THREE.MeshStandardMaterial {
  const map = (kind: string, colour = false) => {
    const tex = textures.load(`${MODEL_ROOT}materials/${t.name}_${kind}_1k.jpg`);
    tex.wrapS = tex.wrapT = THREE.RepeatWrapping;
    tex.repeat.set(Math.max(u / t.tile, 0.05), Math.max(v / t.tile, 0.05));
    tex.anisotropy = 8;
    if (colour) tex.colorSpace = THREE.SRGBColorSpace;
    return tex;
  };
  const arm = map("arm");
  return new THREE.MeshStandardMaterial({ color: tint, map: map("diffuse", true), normalMap: map("nor_gl"), aoMap: arm, roughnessMap: arm, roughness: 1, metalness: 0 });
}

/** A textured box's six faces (+X, −X, +Y, −Y, +Z, −Z), each tiled by its own size so the texture keeps its scale. */
function surfaceFaces(t: Texture, [x, y, z]: Vec3, tint: string): THREE.Material[] {
  const side = surface(t, z, y, tint), top = surface(t, x, z, tint), front = surface(t, x, y, tint);
  return [side, side, top, top, front, front];
}

/** Doors on a run of cabinets: the gaps between them, a drawer line on the base run, and a steel handle on each. */
function cabinetDoors(size: Vec3, upper: boolean): THREE.Group {
  const [w, h, d] = size, doors = Math.max(1, Math.round(w / 0.6)), g = new THREE.Group();
  const gap = new THREE.MeshStandardMaterial({ color: "#8d8a84", roughness: 0.9 });
  const steel = new THREE.MeshStandardMaterial({ color: "#c9ccd0", roughness: 0.25, metalness: 1 });
  const front = d / 2 + 0.001;
  for (let i = 1; i < doors; i++) {
    const seam = new THREE.Mesh(new THREE.BoxGeometry(0.004, h, 0.002), gap);
    seam.position.set(-w / 2 + (i * w) / doors, h / 2, front);
    g.add(seam);
  }
  const drawer = upper ? null : new THREE.Mesh(new THREE.BoxGeometry(w, 0.004, 0.002), gap);
  if (drawer) { drawer.position.set(0, h - 0.17, front); g.add(drawer); }
  if (!upper) {
    const kick = new THREE.Mesh(new THREE.BoxGeometry(w, 0.09, 0.004), new THREE.MeshStandardMaterial({ color: "#3b3936", roughness: 0.8 }));
    kick.position.set(0, 0.045, front);
    g.add(kick);
  }
  for (let i = 0; i < doors; i++) {
    const x = -w / 2 + ((i + 0.5) * w) / doors, handle = new THREE.Mesh(new THREE.BoxGeometry(0.14, 0.012, 0.018), steel);
    handle.position.set(x, upper ? 0.06 : h - 0.085, front + 0.012);
    handle.castShadow = true;
    g.add(handle);
    if (!upper) { const low = handle.clone(); low.position.y = h - 0.36; g.add(low); }
  }
  return g;
}

function material(s: Shape): THREE.MeshStandardMaterial {
  const transparent = s.opacity !== undefined && s.opacity < 1;
  return new THREE.MeshStandardMaterial({
    color: s.color, roughness: s.roughness ?? (s.kind === "cylinder" && s.color.startsWith("#c8") ? 0.35 : 0.75),
    metalness: s.color === "#c2c6ca" ? 0.6 : 0.0, transparent, opacity: s.opacity ?? 1,
  });
}

/** A shape drawn from its size when there is no model file (or while one loads). */
function primitive(s: Shape): THREE.Mesh {
  const base = material(s);
  let mesh: THREE.Mesh;
  if (s.kind === "box") {
    const [x, y, z] = s.size;
    const mats: THREE.Material[] = s.texture ? surfaceFaces(s.texture, s.size, s.color) : [base, base, base, base, base, base];
    if (s.top_image) {
      const lid = textures.load(MODEL_ROOT + s.top_image);
      lid.colorSpace = THREE.SRGBColorSpace; lid.anisotropy = 8;
      mats[2] = new THREE.MeshStandardMaterial({ map: lid, roughness: 0.85 });   // +Y: the top face
    } else if (s.print) {
      const printed = base.clone(); printed.map = printTexture(s.print, s.color, "#3a2a1a"); printed.color.set("#ffffff");
      mats[2] = printed;                                                   // +Y: the top face carries the print
      mats[4] = printed;                                                   // +Z: and the front
    }
    mesh = new THREE.Mesh(new THREE.BoxGeometry(x, y, z), mats);
    mesh.position.y = y / 2;
  } else if (s.kind === "cylinder" && s.hole) {
    // A ring (a roll of tape): its cross-section, a rectangle from the hole to the rim, turned about the axis.
    const r0 = s.hole / 2, r1 = s.diameter / 2, h = s.height;
    const profile = [new THREE.Vector2(r0, 0), new THREE.Vector2(r1, 0), new THREE.Vector2(r1, h), new THREE.Vector2(r0, h), new THREE.Vector2(r0, 0)];
    const tape = new THREE.MeshPhysicalMaterial({ color: s.color, roughness: 0.28, clearcoat: 0.6, clearcoatRoughness: 0.2, side: THREE.DoubleSide });
    mesh = new THREE.Mesh(new THREE.LatheGeometry(profile, 64), tape);
  } else if (s.kind === "cylinder") {
    const mats: THREE.Material[] = [base, base, base];
    if (s.print) { const side = base.clone(); side.map = printTexture(s.print, s.color); side.color.set("#ffffff"); mats[0] = side; }
    mesh = new THREE.Mesh(new THREE.CylinderGeometry(s.diameter / 2, s.diameter / 2, s.height, 48), mats);
    mesh.position.y = s.height / 2;
  } else {
    mesh = new THREE.Mesh(new THREE.SphereGeometry(s.diameter / 2, 32, 16), base);
    mesh.position.y = s.diameter / 2;
  }
  mesh.castShadow = true; mesh.receiveShadow = true;
  return mesh;
}

/** The target size of a shape, as a box: models are scaled to it so the scene's measurements stay true. */
function targetSize(s: Shape): THREE.Vector3 {
  if (s.kind === "box") return new THREE.Vector3(...s.size);
  if (s.kind === "cylinder") return new THREE.Vector3(s.diameter, s.height, s.diameter);
  return new THREE.Vector3(s.diameter, s.diameter, s.diameter);
}

/**
 * Some downloaded models use the old specular-glossiness material, which three.js no longer reads: take its colour
 * texture and colour, so the model is not drawn plain white.
 */
async function adoptSpecGloss(gltf: GLTF): Promise<void> {
  const json = gltf.parser.json as { materials?: { extensions?: Record<string, { diffuseTexture?: { index: number }; diffuseFactor?: number[] }> }[] };
  const jobs: Promise<void>[] = [];
  gltf.scene.traverse((o) => {
    const mesh = o as THREE.Mesh;
    if (!mesh.isMesh) return;
    for (const m of Array.isArray(mesh.material) ? mesh.material : [mesh.material]) {
      const index = gltf.parser.associations.get(m)?.materials;
      const sg = index === undefined ? undefined : json.materials?.[index]?.extensions?.KHR_materials_pbrSpecularGlossiness;
      const std = m as THREE.MeshStandardMaterial;
      if (!sg || std.map) continue;
      if (sg.diffuseFactor) std.color.setRGB(sg.diffuseFactor[0]!, sg.diffuseFactor[1]!, sg.diffuseFactor[2]!, THREE.LinearSRGBColorSpace);
      std.metalness = 0; std.roughness = 0.35;          // glTF's default is bare metal, which draws it black
      if (sg.diffuseTexture) jobs.push(gltf.parser.getDependency("texture", sg.diffuseTexture.index).then((t: THREE.Texture) => {
        t.colorSpace = THREE.SRGBColorSpace; std.map = t; std.needsUpdate = true;
      }));
    }
  });
  await Promise.all(jobs);
}

/**
 * Loads a model and fits it to the object's size, standing on its base. "stretch" scales each axis to the size (after
 * turning the model a quarter if its long side runs the other way), so what a scan measures is exactly the file's size;
 * "height" scales evenly to the height, for things a squash would spoil (a mug's handle).
 */
async function modelFor(s: Shape, loader: GLTFLoader): Promise<THREE.Object3D | null> {
  if (!s.model) return null;
  try {
    const gltf = await loader.loadAsync(MODEL_ROOT + s.model);
    await adoptSpecGloss(gltf);
    const model = gltf.scene;
    const turn = new THREE.Group();
    turn.add(model);
    const want = targetSize(s);
    let size = new THREE.Box3().setFromObject(turn).getSize(new THREE.Vector3());
    const fit = s.fit ?? (s.kind === "sphere" ? "height" : "stretch");
    if (fit === "stretch" && s.kind === "box" && (size.x > size.z) !== (want.x > want.z)) {
      turn.rotation.y = Math.PI / 2;
      size = new THREE.Box3().setFromObject(turn).getSize(new THREE.Vector3());
    }
    const k = size.y > 0 ? want.y / size.y : 1;
    const scaler = new THREE.Group();
    scaler.add(turn);
    if (fit === "stretch") scaler.scale.set(want.x / size.x, k, want.z / size.z);
    else scaler.scale.setScalar(k);
    const holder = new THREE.Group();
    holder.add(scaler);
    const box = new THREE.Box3().setFromObject(holder), centre = box.getCenter(new THREE.Vector3());
    scaler.position.set(-centre.x, -box.min.y, -centre.z);
    model.traverse((o) => { if ((o as THREE.Mesh).isMesh) { o.castShadow = true; o.receiveShadow = true; } });
    return holder;
  } catch (err) {
    console.warn(`kitchen: ${s.model} did not load, drawing ${s.id} as a shape`, err);
    return null;
  }
}

/** Builds the kitchen. Shapes appear at once; any model files replace their shape as they arrive. */
export function buildKitchen(file: KitchenFile = KITCHEN): BuiltKitchen {
  const root = new THREE.Group();
  root.name = "kitchen";
  const depthInvisible: THREE.Object3D[] = [];
  const loader = new GLTFLoader();
  for (const s of [...file.room, ...file.objects]) {
    const holder = new THREE.Group();
    holder.name = s.id;
    holder.position.set(...s.base);
    holder.rotation.y = THREE.MathUtils.degToRad(s.yaw ?? 0);
    const shape = primitive(s);
    holder.add(shape);
    if ("surface" in s && s.surface === "cabinet") holder.add(cabinetDoors((s as Extract<Shape, { kind: "box" }>).size, s.base[1] > 1));
    root.add(holder);
    if (s.depth === false) depthInvisible.push(holder);
    void modelFor(s, loader).then((model) => { if (model) { holder.remove(shape); holder.add(model); } });
  }
  return { root, depthInvisible };
}

/**
 * Kitchen daylight: a real interior's light (a Poly Haven HDR) for the soft fill and the reflections on metal and
 * glass, a window key light for the shadows, and a warm under-cabinet strip. The HDR is handed to three.js as an
 * equirectangular map, so each renderer (the page's and the capture rig's) makes its own lighting from it.
 */
export function addLights(target: THREE.Scene): void {
  target.add(new THREE.HemisphereLight(0xf4f1ea, 0x6b5e4d, 0.35));
  const key = new THREE.DirectionalLight(0xfff6e8, 2.0);
  key.position.set(-2, 3.2, 2.4);
  key.castShadow = true;
  key.shadow.mapSize.set(2048, 2048);
  key.shadow.bias = -0.0004; key.shadow.normalBias = 0.01;
  key.shadow.camera.left = -3; key.shadow.camera.right = 3; key.shadow.camera.top = 3; key.shadow.camera.bottom = -3;
  target.add(key);
  const strip = new THREE.PointLight(0xffe2b8, 0.6, 2.5);
  strip.position.set(0, 1.38, -0.4);
  target.add(strip);
  new RGBELoader().load(`${MODEL_ROOT}kiara_interior_1k.hdr`, (hdr) => {
    hdr.mapping = THREE.EquirectangularReflectionMapping;
    target.environment = hdr;
    target.environmentIntensity = 0.9;
  }, undefined, (err) => console.warn("kitchen: the room light (HDR) did not load; using the plain lights", err));
}
