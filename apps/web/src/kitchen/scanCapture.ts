import * as THREE from "three";
import type { BuildScanUpload } from "@cutonce/schemas";

/**
 * A pretend headset scan of the kitchen: exactly what the Quest uploads (BuildScanCapture.cs), from a render.
 * - The photo: the kitchen seen through a camera with the Quest 3 passthrough lens (1280 × 960, 58.7° tall).
 * - The depth: one point per cell of a 128 × 96 grid through that photo's pixels. Rendered in one pass that writes each
 *   pixel's world position (not 12,288 raycasts, which are too slow against detailed models), then read back.
 * Clear objects are hidden from the depth pass, as a real depth sensor misses clear plastic. A little noise is added
 * along each ray, like the Quest's depth.
 */

export const PHOTO_W = 1280, PHOTO_H = 960, GRID_COLS = 128, GRID_ROWS = 96;
/** The Quest 3 passthrough lens as measured in the simulator: focal length 853.6 px at 1280 × 960. */
export const FOCAL_PX = 853.6;
export const FOV_Y_DEG = THREE.MathUtils.radToDeg(2 * Math.atan(PHOTO_H / 2 / FOCAL_PX));

type Vec3 = [number, number, number];

/**
 * World positions read back from a float render target → the upload's points (mm) and hit mask, row-major from the
 * photo's top-left. WebGL reads rows bottom-up, so rows are flipped here. Alpha 0 is a miss (the clear colour).
 */
export function encodePoints(rgba: Float32Array, cols: number, rows: number, camera: Vec3, noiseM = 0, seed = 1): { points_mm: number[]; hit: string } {
  let s = seed >>> 0;
  const rand = () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s / 4294967296; };
  const points_mm: number[] = new Array(cols * rows * 3).fill(0);
  let hit = "";
  for (let r = 0; r < rows; r++) {
    const glRow = rows - 1 - r;
    for (let c = 0; c < cols; c++) {
      const i = (glRow * cols + c) * 4, cell = r * cols + c;
      if (rgba[i + 3]! < 0.5) { hit += "0"; continue; }
      let x = rgba[i]!, y = rgba[i + 1]!, z = rgba[i + 2]!;
      if (noiseM > 0) {
        const dx = x - camera[0], dy = y - camera[1], dz = z - camera[2], d = Math.hypot(dx, dy, dz);
        const k = 1 + ((rand() + rand() + rand() - 1.5) * noiseM) / d;       // noise along the ray, about ±noiseM
        x = camera[0] + dx * k; y = camera[1] + dy * k; z = camera[2] + dz * k;
      }
      points_mm[cell * 3] = Math.round(x * 1000); points_mm[cell * 3 + 1] = Math.round(y * 1000); points_mm[cell * 3 + 2] = Math.round(z * 1000);
      hit += "1";
    }
  }
  return { points_mm, hit };
}

const worldPosition = new THREE.ShaderMaterial({
  vertexShader: "varying vec3 vWorld; void main() { vec4 w = modelMatrix * vec4(position, 1.0); vWorld = w.xyz; gl_Position = projectionMatrix * viewMatrix * w; }",
  fragmentShader: "varying vec3 vWorld; void main() { gl_FragColor = vec4(vWorld, 1.0); }",
  side: THREE.DoubleSide,
});

/** The camera the scan uses: the viewer's pose with the Quest's passthrough lens. */
export function scanCamera(view: THREE.Camera): THREE.PerspectiveCamera {
  const cam = new THREE.PerspectiveCamera(FOV_Y_DEG, PHOTO_W / PHOTO_H, 0.05, 20);
  view.getWorldPosition(cam.position);
  view.getWorldQuaternion(cam.quaternion);
  cam.updateMatrixWorld();
  return cam;
}

/** One offscreen renderer for photos and depth, separate from the page's, so a scan never disturbs what you see. */
export class CaptureRig {
  private readonly canvas = document.createElement("canvas");
  private readonly renderer: THREE.WebGLRenderer;
  private readonly depthTarget = new THREE.WebGLRenderTarget(GRID_COLS, GRID_ROWS, {
    type: THREE.FloatType, format: THREE.RGBAFormat, minFilter: THREE.NearestFilter, magFilter: THREE.NearestFilter, depthBuffer: true,
  });

  constructor() {
    this.canvas.width = PHOTO_W; this.canvas.height = PHOTO_H;
    this.renderer = new THREE.WebGLRenderer({ canvas: this.canvas, antialias: true, preserveDrawingBuffer: true });
    this.renderer.setSize(PHOTO_W, PHOTO_H, false);
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;
    this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
    this.renderer.shadowMap.enabled = true;
  }

  /** The photo only (a JPEG), for a copilot question. */
  async photo(room: THREE.Scene, view: THREE.Camera): Promise<Blob> {
    this.renderer.setRenderTarget(null);
    this.renderer.render(room, scanCamera(view));
    return await new Promise<Blob>((resolve, reject) => this.canvas.toBlob((b) => (b ? resolve(b) : reject(new Error("no photo"))), "image/jpeg", 0.88));
  }

  /** A full scan: the photo and the depth grid, as the headset uploads them. */
  async scan(room: THREE.Scene, view: THREE.Camera, depthInvisible: THREE.Object3D[], opts: { sessionId: string | null; noiseM?: number }):
    Promise<{ upload: BuildScanUpload; photo: Blob }> {
    const cam = scanCamera(view);
    const photo = await this.photo(room, view);

    const hidden = depthInvisible.filter((o) => o.visible);
    hidden.forEach((o) => { o.visible = false; });
    const background = room.background, override = room.overrideMaterial;
    room.background = null; room.overrideMaterial = worldPosition;
    this.renderer.setRenderTarget(this.depthTarget);
    this.renderer.setClearColor(0x000000, 0);
    this.renderer.clear();
    this.renderer.render(room, cam);
    const rgba = new Float32Array(GRID_COLS * GRID_ROWS * 4);
    this.renderer.readRenderTargetPixels(this.depthTarget, 0, 0, GRID_COLS, GRID_ROWS, rgba);
    this.renderer.setRenderTarget(null);
    room.background = background; room.overrideMaterial = override;
    hidden.forEach((o) => { o.visible = true; });

    const position: Vec3 = [cam.position.x, cam.position.y, cam.position.z];
    const f = new THREE.Vector3(0, 0, -1).applyQuaternion(cam.quaternion);
    const { points_mm, hit } = encodePoints(rgba, GRID_COLS, GRID_ROWS, position, opts.noiseM ?? 0.004, Date.now() & 0xffff);
    const photo_b64 = await blobToBase64(photo);
    const focal = PHOTO_H / 2 / Math.tan(THREE.MathUtils.degToRad(FOV_Y_DEG / 2));
    return {
      photo,
      upload: {
        session_id: opts.sessionId, device_id: "web-kitchen", grid: { cols: GRID_COLS, rows: GRID_ROWS }, points_mm, hit,
        camera: { position, forward: [f.x, f.y, f.z], intrinsics: { width: PHOTO_W, height: PHOTO_H, fx: focal, fy: focal, cx: PHOTO_W / 2, cy: PHOTO_H / 2 } },
        photo_b64,
      },
    };
  }

  dispose(): void { this.depthTarget.dispose(); this.renderer.dispose(); }
}

async function blobToBase64(blob: Blob): Promise<string> {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  let s = "";
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
}
