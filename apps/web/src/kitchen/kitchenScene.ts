import * as THREE from "three";
import { GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";
import scene from "../../../../data/build/scenes/kitchen.json";

/**
 * The test kitchen, built from data/build/scenes/kitchen.json. The kitchen is the "real room": it is what the pretend
 * headset photographs and measures. Holograms (twins, designs, labels) live in a separate scene so they never show up
 * in a scan. Plan frame = three.js frame (right-handed, +Y up, metres), so nothing is converted.
 */

type Vec3 = [number, number, number];
interface Common { id: string; base: Vec3; yaw?: number; color: string; opacity?: number; print?: string; model?: string | null; depth?: boolean }
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

function material(s: Shape): THREE.MeshStandardMaterial {
  const transparent = s.opacity !== undefined && s.opacity < 1;
  return new THREE.MeshStandardMaterial({
    color: s.color, roughness: s.kind === "cylinder" && s.color.startsWith("#c8") ? 0.35 : 0.75,
    metalness: s.color === "#c2c6ca" ? 0.6 : 0.0, transparent, opacity: s.opacity ?? 1,
  });
}

/** A shape drawn from its size when there is no model file (or while one loads). */
function primitive(s: Shape): THREE.Mesh {
  const base = material(s);
  let mesh: THREE.Mesh;
  if (s.kind === "box") {
    const [x, y, z] = s.size;
    const mats: THREE.Material[] = [base, base, base, base, base, base];
    if (s.print) {
      const printed = base.clone(); printed.map = printTexture(s.print, s.color, "#3a2a1a"); printed.color.set("#ffffff");
      mats[2] = printed;                                                   // +Y: the top face carries the print
      mats[4] = printed;                                                   // +Z: and the front
    }
    mesh = new THREE.Mesh(new THREE.BoxGeometry(x, y, z), mats);
    mesh.position.y = y / 2;
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

/** Loads a model, scales it to the object's size (by height, keeping its proportions) and stands it on its base. */
async function modelFor(s: Shape, loader: GLTFLoader): Promise<THREE.Object3D | null> {
  if (!s.model) return null;
  try {
    const gltf = await loader.loadAsync(MODEL_ROOT + s.model);
    const model = gltf.scene;
    const box = new THREE.Box3().setFromObject(model);
    const size = box.getSize(new THREE.Vector3());
    const want = targetSize(s);
    const k = size.y > 0 ? want.y / size.y : 1;
    model.scale.setScalar(k);
    const scaled = new THREE.Box3().setFromObject(model);
    const centre = scaled.getCenter(new THREE.Vector3());
    model.position.set(-centre.x, -scaled.min.y, -centre.z);
    model.traverse((o) => { if ((o as THREE.Mesh).isMesh) { o.castShadow = true; o.receiveShadow = true; } });
    const holder = new THREE.Group();
    holder.add(model);
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
    root.add(holder);
    if (s.depth === false) depthInvisible.push(holder);
    void modelFor(s, loader).then((model) => { if (model) { holder.remove(shape); holder.add(model); } });
  }
  return { root, depthInvisible };
}

/** Soft kitchen daylight: a sky/floor fill, a window key light with shadows, and a warm under-cabinet strip. */
export function addLights(target: THREE.Scene): void {
  target.add(new THREE.HemisphereLight(0xf4f1ea, 0x6b5e4d, 1.1));
  const key = new THREE.DirectionalLight(0xffffff, 2.2);
  key.position.set(-2, 3.2, 2.4);
  key.castShadow = true;
  key.shadow.mapSize.set(2048, 2048);
  key.shadow.camera.left = -3; key.shadow.camera.right = 3; key.shadow.camera.top = 3; key.shadow.camera.bottom = -3;
  target.add(key);
  const strip = new THREE.PointLight(0xffe2b8, 0.8, 3);
  strip.position.set(0, 1.4, -0.35);
  target.add(strip);
}
