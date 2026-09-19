import { useEffect, useRef, useState, type MutableRefObject } from "react";
import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import type { LineMaterial } from "three/examples/jsm/lines/LineMaterial.js";
import type { LineSegments2 } from "three/examples/jsm/lines/LineSegments2.js";
import { HOLOGRAM_PALETTE, styleFor, type PartVisual } from "@cutonce/project-model";
import type { Plan } from "@cutonce/schemas";
import type { View } from "../../preview/previewParams";
import { useTheme } from "../../ThemeSwitch";
import { buildPartObject, disposeObject } from "../buildPart";
import type { Aabb } from "@cutonce/project-model";
import { placeCamera, planBounds } from "./cameras";
import {
  COMPARE_OUTLINE, animate, applyStyle, bracketPositions, edgePositions, fatLines, gridPositions, lineMaterial, makeMaterials,
  type PartMaterials,
} from "./materials";
import { loadMeshNode } from "./meshAssets";

export type Background = { kind: "none" } | { kind: "webcam" } | { kind: "image"; src: string };

export interface HologramViewProps {
  plan: Plan;
  visuals: Record<string, PartVisual>;
  /** A second plan drawn as thin white outlines (extracted vs known-good). */
  compare?: Plan | null;
  view: View;
  /** Vertical field of view in degrees. The Quest 3 is about 96°. */
  fov: number;
  background: Background;
  /** Freeze pulses so screenshots are identical run to run. */
  still: boolean;
  assetUrl: (planId: string, name: string) => string;
  authHeaders: Record<string, string>;
  /** Called once per plan, after every part (including GLB meshes) is drawn and two frames have rendered. */
  onReady?: () => void;
  /** The part under the pointer, or null. */
  onPoint?: (partId: string | null) => void;
  /** Exposes the camera, so /sim can project parts into a frame exactly as the headset would. */
  cameraRef?: MutableRefObject<THREE.PerspectiveCamera | null>;
  /** What the camera frames, when not the whole plan: /sim's build mode frames the pile and the designs, not the table. */
  frame?: Aabb | null;
}

interface PartRuntime {
  partId: string;
  root: THREE.Group;
  mats: PartMaterials;
  gridMat: LineMaterial;
  edges: LineSegments2[];
  brackets: LineSegments2;
  grid: LineSegments2 | null;
}

interface Stage {
  renderer: THREE.WebGLRenderer;
  scene: THREE.Scene;
  camera: THREE.PerspectiveCamera;
  controls: OrbitControls;
  partsGroup: THREE.Group;
  resolution: THREE.Vector2;
  parts: Map<string, PartRuntime>;
  pickables: THREE.Object3D[];
  ready: { waiting: boolean; frames: number; fired: boolean };
}

/** The stage behind the parts follows the page's theme, like every other surface; the part colours never do. */
function stageColour(host: HTMLElement): string {
  return getComputedStyle(host).getPropertyValue("--bg-sunken").trim() || "#eceef0";
}
const HIDDEN = new THREE.MeshBasicMaterial({ visible: false });
const DEFAULT_VISUAL: PartVisual = { base: "MISSING", modifiers: [] };

export function HologramView(props: HologramViewProps) {
  const { plan, visuals, compare, view, fov, background, still } = props;
  const frameKey = props.frame ? JSON.stringify(props.frame) : "";          // a new object with the same box does not move the camera
  const hostRef = useRef<HTMLDivElement>(null);
  const stageRef = useRef<Stage | null>(null);
  const latest = useRef(props);
  latest.current = props;
  const [notice, setNotice] = useState<string | null>(null);
  const theme = useTheme();

  // ── renderer, camera and the render loop: once per mount ───────────────────
  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;
    let renderer: THREE.WebGLRenderer;
    try {
      renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
    } catch (e) {
      setNotice(e instanceof Error ? e.message : "WebGL is not available");
      return;
    }
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.domElement.className = "hologram-canvas";
    host.appendChild(renderer.domElement);

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(stageColour(host));
    const camera = new THREE.PerspectiveCamera(latest.current.fov, 1, 0.01, 1000);
    const controls = new OrbitControls(camera, renderer.domElement);
    const partsGroup = new THREE.Group();
    scene.add(partsGroup);
    const resolution = new THREE.Vector2(1, 1);
    const stage: Stage = {
      renderer, scene, camera, controls, partsGroup, resolution, parts: new Map(), pickables: [],
      ready: { waiting: false, frames: 0, fired: false },
    };
    stageRef.current = stage;
    if (latest.current.cameraRef) latest.current.cameraRef.current = camera;

    const resize = () => {
      const w = Math.max(1, host.clientWidth), h = Math.max(1, host.clientHeight);
      renderer.setSize(w, h, false);
      resolution.set(w, h);
      camera.aspect = w / h;
      camera.updateProjectionMatrix();
    };
    resize();
    const observer = new ResizeObserver(resize);
    observer.observe(host);

    const raycaster = new THREE.Raycaster();
    const pointer = new THREE.Vector2();
    let lastPoint: string | null = null;
    const onMove = (ev: PointerEvent) => {
      const rect = renderer.domElement.getBoundingClientRect();
      pointer.set(((ev.clientX - rect.left) / rect.width) * 2 - 1, -((ev.clientY - rect.top) / rect.height) * 2 + 1);
      raycaster.setFromCamera(pointer, camera);
      const id = raycaster.intersectObjects(stage.pickables, false)[0]?.object.userData.partId;
      const next = typeof id === "string" ? id : null;
      if (next !== lastPoint) { lastPoint = next; latest.current.onPoint?.(next); }
    };
    renderer.domElement.addEventListener("pointermove", onMove);

    const clock = new THREE.Clock();
    let raf = 0;
    const tick = () => {
      raf = requestAnimationFrame(tick);
      const t = clock.getElapsedTime();
      for (const p of stage.parts.values()) animate(p.mats, t, latest.current.still);
      controls.update();
      renderer.render(scene, camera);
      const r = stage.ready;
      if (r.waiting && !r.fired && ++r.frames >= 2) { r.fired = true; latest.current.onReady?.(); }
    };
    tick();

    return () => {
      cancelAnimationFrame(raf);
      observer.disconnect();
      renderer.domElement.removeEventListener("pointermove", onMove);
      controls.dispose();
      disposeObject(scene);
      renderer.dispose();
      renderer.forceContextLoss();
      renderer.domElement.remove();
      stageRef.current = null;
    };
  }, []);

  // ── parts, floor and the compare plan: rebuilt when the plan changes ───────
  useEffect(() => {
    const stage = stageRef.current;
    if (!stage) return;
    let disposed = false;
    const added: THREE.Object3D[] = [];
    const pending: Promise<unknown>[] = [];
    stage.ready = { waiting: false, frames: 0, fired: false };
    stage.pickables = [];

    for (const part of plan.parts) {
      const obj = buildPartObject(part);
      if (!obj) continue;
      // Keep the shared shape code's geometry and placement; replace its materials with the hologram's.
      const pick = obj.pick;
      for (const child of [...pick.children]) { pick.remove(child); disposeObject(child); }
      obj.fill.dispose(); obj.line.dispose();
      const mats = makeMaterials(HOLOGRAM_PALETTE.bases.MISSING, stage.resolution);
      pick.material = mats.fill;
      pick.geometry.computeBoundingBox();
      const localBox = pick.geometry.boundingBox!.clone();

      const edges = fatLines(edgePositions(pick.geometry), mats.edge);
      const brackets = fatLines(bracketPositions(localBox), mats.edge);
      const gridMat = lineMaterial(stage.resolution);
      const grid = part.shape.type === "box" ? fatLines(gridPositions(localBox, 0.05), gridMat) : null;
      pick.add(edges, brackets);
      if (grid) pick.add(grid);

      const root = new THREE.Group();
      root.add(pick);
      stage.partsGroup.add(root);
      added.push(root);
      stage.pickables.push(pick);
      const runtime: PartRuntime = { partId: part.part_id, root, mats, gridMat, edges: [edges], brackets, grid };
      stage.parts.set(part.part_id, runtime);

      const shape = part.shape as { type: string; uri?: string; node?: string };
      if (shape.type === "mesh" && shape.uri && shape.node) {
        pending.push(loadMeshNode(latest.current.assetUrl(plan.plan_id, shape.uri), shape.node, latest.current.authHeaders).then((node) => {
          if (disposed || !node) return;
          node.traverse((o) => {
            const mesh = o as THREE.Mesh;
            if (!mesh.isMesh) return;
            mesh.material = mats.fill;
            mesh.userData.partId = part.part_id;
            const meshEdges = fatLines(edgePositions(mesh.geometry, 30), mats.edge);
            mesh.add(meshEdges);
            runtime.edges.push(meshEdges);
            stage.pickables.push(mesh);
          });
          // The real model replaces the bounds box; the box keeps its corner brackets for BUILT_LIVE.
          pick.material = HIDDEN;
          edges.visible = false;
          runtime.edges = runtime.edges.filter((e) => e !== edges);
          root.add(node);
          applyVisual(runtime, latest.current.visuals[part.part_id] ?? DEFAULT_VISUAL);
        }).catch((err) => console.warn(`[hologram] could not load ${shape.uri} for ${part.part_id}; showing its bounds`, err)));
      }
    }

    if (compare) {
      const outline = lineMaterial(stage.resolution);
      outline.color.set(COMPARE_OUTLINE.color);
      outline.opacity = COMPARE_OUTLINE.opacity;
      outline.linewidth = COMPARE_OUTLINE.widthPx;
      for (const part of compare.parts) {
        const obj = buildPartObject(part);
        if (!obj) continue;
        for (const child of [...obj.pick.children]) { obj.pick.remove(child); disposeObject(child); }
        obj.fill.dispose(); obj.line.dispose();
        obj.pick.material = HIDDEN;
        obj.pick.raycast = () => {};
        obj.pick.add(fatLines(edgePositions(obj.pick.geometry), outline));
        stage.partsGroup.add(obj.pick);
        added.push(obj.pick);
      }
    }

    const bounds = planBounds(plan);
    for (const [id, runtime] of stage.parts) applyVisual(runtime, latest.current.visuals[id] ?? DEFAULT_VISUAL);
    const target = placeCamera(stage.camera, latest.current.frame ?? bounds, latest.current.view);
    stage.controls.target.copy(target);
    stage.controls.update();
    void Promise.allSettled(pending).then(() => { if (!disposed) stage.ready.waiting = true; });

    return () => {
      disposed = true;
      for (const o of added) { o.removeFromParent(); disposeObject(o); }
      for (const p of stage.parts.values()) { p.mats.fill.dispose(); p.mats.edge.dispose(); p.gridMat.dispose(); }
      stage.parts.clear();
      stage.pickables = [];
    };
  }, [plan, compare]);

  // ── a faint floor grid under the model, in the theme's line colours: 10 cm cells for furniture, 5 m for buildings ──
  useEffect(() => {
    const stage = stageRef.current;
    const host = hostRef.current;
    if (!stage || !host) return;
    const css = getComputedStyle(host);
    const bounds = planBounds(plan);
    const span = Math.max(bounds.max[0] - bounds.min[0], bounds.max[2] - bounds.min[2], 0.5);
    const cell = span < 5 ? 0.1 : 5;
    const divisions = Math.min(200, Math.ceil((span * 2) / cell));
    const floor = new THREE.GridHelper(
      divisions * cell, divisions,
      new THREE.Color(css.getPropertyValue("--border-strong").trim() || "#c2c6cc"),
      new THREE.Color(css.getPropertyValue("--border").trim() || "#dcdee2"),
    );
    floor.position.set((bounds.min[0] + bounds.max[0]) / 2, bounds.min[1] - 0.001, (bounds.min[2] + bounds.max[2]) / 2);
    floor.raycast = () => {};
    stage.partsGroup.add(floor);
    return () => { floor.removeFromParent(); disposeObject(floor); };
  }, [plan, theme]);

  // ── the look: re-applied whenever the build state, selection or highlights change ──
  useEffect(() => {
    const stage = stageRef.current;
    if (!stage) return;
    for (const [id, runtime] of stage.parts) applyVisual(runtime, visuals[id] ?? DEFAULT_VISUAL);
  }, [visuals]);

  // ── camera: re-placed when the view or field of view changes ───────────────
  useEffect(() => {
    const stage = stageRef.current;
    if (!stage) return;
    stage.camera.fov = fov;
    const target = placeCamera(stage.camera, latest.current.frame ?? planBounds(plan), view);
    stage.controls.target.copy(target);
    stage.controls.update();
  }, [view, fov, plan, frameKey]);

  // ── background: the passthrough stand-in ───────────────────────────────────
  const bgKey = background.kind === "image" ? `image:${background.src}` : background.kind;
  useEffect(() => {
    const stage = stageRef.current;
    if (!stage) return;
    let stop = () => {};
    stage.scene.background = new THREE.Color(hostRef.current ? stageColour(hostRef.current) : "#eceef0");
    setNotice(null);
    if (background.kind === "image") {
      new THREE.TextureLoader().load(background.src, (tex) => {
        tex.colorSpace = THREE.SRGBColorSpace;
        if (stageRef.current === stage) stage.scene.background = tex;
      }, undefined, () => setNotice(`Could not load the background image ${background.src}`));
    } else if (background.kind === "webcam") {
      const video = document.createElement("video");
      video.muted = true; video.playsInline = true;
      let stream: MediaStream | null = null;
      navigator.mediaDevices?.getUserMedia({ video: { width: 1280, height: 720 } }).then((s) => {
        stream = s;
        video.srcObject = s;
        void video.play();
        const tex = new THREE.VideoTexture(video);
        tex.colorSpace = THREE.SRGBColorSpace;
        if (stageRef.current === stage) stage.scene.background = tex;
      }).catch(() => setNotice("The webcam was not available, so the background is plain."));
      stop = () => { stream?.getTracks().forEach((t) => t.stop()); };
    }
    return () => stop();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [bgKey]);

  // A theme change recolours a plain stage; a webcam or image background is left alone.
  useEffect(() => {
    const stage = stageRef.current;
    const host = hostRef.current;
    if (stage && host && stage.scene.background instanceof THREE.Color) stage.scene.background.set(stageColour(host));
  }, [theme]);

  return (
    <div className="hologram-host" ref={hostRef}>
      {notice && <p className="hologram-notice">{notice}</p>}
      {still && <span className="sr-only">Animations paused for a still image</span>}
    </div>
  );
}

function applyVisual(p: PartRuntime, v: PartVisual): void {
  const style = styleFor(v);
  applyStyle(p.mats, style);
  for (const e of p.edges) e.visible = !style.brackets;
  p.brackets.visible = style.brackets;
  if (p.grid) {
    p.grid.visible = style.grid;
    p.gridMat.color.set(style.edge);
    p.gridMat.linewidth = 1;
    p.gridMat.opacity = style.edgeAlpha * 0.4;
  }
}

export default HologramView;
