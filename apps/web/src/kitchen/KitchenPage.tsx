import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import { CSS2DObject, CSS2DRenderer } from "three/examples/jsm/renderers/CSS2DRenderer.js";
import type { Assembly, BuildIdea, BuildState, CopilotResponse, Plan, Twin } from "@cutonce/schemas";
import {
  askCopilot, describeError, fetchAnswerAudio, getBuildCurrent, getCurrentAssembly, getPlan, getState, newBuildSession, postBuildScan,
  sendDirectorCommand, startBuildIdea,
} from "../api";
import { applyLook, buildPartObject, disposeObject, type PartObject } from "../three/buildPart";
import { buildContextPacket } from "../sim/contextPacket";
import { startRecording, type Recording } from "../sim/micWav";
import { useStream } from "../ws";
import { disposeGroup, twinGlow } from "./glow";
import { addLights, buildKitchen, KITCHEN } from "./kitchenScene";
import { CaptureRig, PHOTO_H, PHOTO_W, scanCamera } from "./scanCapture";

/**
 * /kitchen: build mode against a pretend kitchen, no headset. The kitchen is the room; this page is the headset.
 * Scan (S, or say "What can I build?" holding Space) sends the server exactly what the Quest sends: a photo through the
 * Quest's lens and a 128 × 96 depth grid. The real pipeline answers (outlines, names, Kit's designs), and a design you
 * start lands in the kitchen where the server put it, its pieces flying in from the objects they are made of.
 */

type Status = { kind: "idle" | "busy" | "error"; text: string };
const IDLE = "Drag to look around. Press S to scan, or hold Space and ask \"What can I build?\"";

interface Flight { obj: THREE.Object3D; from: THREE.Vector3; to: THREE.Vector3; start: number; lift: number }
interface World {
  renderer: THREE.WebGLRenderer; labels: CSS2DRenderer; camera: THREE.PerspectiveCamera; controls: OrbitControls;
  room: THREE.Scene; overlay: THREE.Scene; twins: THREE.Group; design: THREE.Group; rig: CaptureRig;
  depthInvisible: THREE.Object3D[]; parts: PartObject[]; flights: Flight[];
}

const FLY_MS = 700, STAGGER_MS = 300;
const ease = (t: number) => { const c = Math.min(1, Math.max(0, t)); return c * c * (3 - 2 * c); };

export function KitchenPage() {
  const host = useRef<HTMLDivElement | null>(null);
  const world = useRef<World | null>(null);
  const [status, setStatus] = useState<Status>({ kind: "idle", text: IDLE });
  const [twins, setTwins] = useState<Twin[]>([]);
  const [labelled, setLabelled] = useState(false);
  const [ideas, setIdeas] = useState<BuildIdea[]>([]);
  const [finalIdeas, setFinalIdeas] = useState(false);
  const [answer, setAnswer] = useState<CopilotResponse | null>(null);
  const [highlight, setHighlight] = useState<string[]>([]);
  const [run, setRun] = useState<Assembly | null>(null);
  const [plan, setPlan] = useState<Plan | null>(null);
  const [state, setState] = useState<BuildState | null>(null);
  const session = useRef<string | null>(null);
  const live = useRef({ ideas, twins, run, plan, state });
  live.current = { ideas, twins, run, plan, state };
  const recording = useRef<Recording | null>(null);

  // ── the 3D world: the kitchen (the room) and, drawn over it, the holograms ──────────────────────────────────
  useEffect(() => {
    const el = host.current;
    if (!el) return;
    const renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setSize(el.clientWidth, el.clientHeight);
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.shadowMap.enabled = true;
    renderer.autoClear = false;
    el.appendChild(renderer.domElement);
    const labels = new CSS2DRenderer();
    labels.setSize(el.clientWidth, el.clientHeight);
    labels.domElement.className = "kitchen-labels";
    el.appendChild(labels.domElement);

    const camera = new THREE.PerspectiveCamera(60, el.clientWidth / el.clientHeight, 0.05, 30);
    camera.position.set(...KITCHEN.camera.position);
    const controls = new OrbitControls(camera, renderer.domElement);
    controls.target.set(...KITCHEN.camera.target);
    controls.enableDamping = true;
    controls.maxPolarAngle = Math.PI * 0.49;
    controls.update();

    const room = new THREE.Scene();
    room.background = new THREE.Color("#cdc6ba");
    addLights(room);
    const kitchen = buildKitchen();
    room.add(kitchen.root);
    const overlay = new THREE.Scene();
    const twinsGroup = new THREE.Group(), design = new THREE.Group();
    overlay.add(twinsGroup, design);
    overlay.add(new THREE.HemisphereLight(0xffffff, 0x444444, 1.2));

    const w: World = { renderer, labels, camera, controls, room, overlay, twins: twinsGroup, design, rig: new CaptureRig(),
      depthInvisible: kitchen.depthInvisible, parts: [], flights: [] };
    world.current = w;

    let frame = 0;
    const loop = () => {
      frame = requestAnimationFrame(loop);
      controls.update();
      const now = performance.now();
      w.flights = w.flights.filter((f) => {
        const e = ease((now - f.start) / FLY_MS);
        f.obj.position.lerpVectors(f.from, f.to, e);
        f.obj.position.y += 4 * e * (1 - e) * f.lift;
        return e < 1;
      });
      renderer.clear();
      renderer.render(room, camera);
      renderer.render(overlay, camera);                                     // over the room, tested against its depth
      labels.render(overlay, camera);
    };
    loop();
    const onResize = () => {
      camera.aspect = el.clientWidth / el.clientHeight; camera.updateProjectionMatrix();
      renderer.setSize(el.clientWidth, el.clientHeight); labels.setSize(el.clientWidth, el.clientHeight);
    };
    window.addEventListener("resize", onResize);
    (window as unknown as { __kitchen?: World }).__kitchen = w;              // for tests and debugging in the browser
    return () => {
      cancelAnimationFrame(frame);
      window.removeEventListener("resize", onResize);
      controls.dispose(); w.rig.dispose(); renderer.dispose();
      el.replaceChildren();
      world.current = null;
    };
  }, []);

  // ── the objects found: a glow on each and a floating name ────────────────────────────────────────────────────
  useEffect(() => {
    const w = world.current;
    if (!w) return;
    for (const child of [...w.twins.children]) { w.twins.remove(child); disposeGroup(child); }
    for (const t of twins) {
      const named = labelled && t.name !== "unknown";
      const g = twinGlow(t, { named, highlighted: highlight.includes(t.twin_id) });
      const top = t.shape.type === "box" ? t.shape.size[1] / 2 : t.shape.axis === "y" ? t.shape.length / 2 : t.shape.diameter / 2;
      const tag = document.createElement("div");
      tag.className = `kitchen-label${named ? "" : " unnamed"}`;
      tag.textContent = named ? `${t.label} · ${sizeText(t)}` : "…";
      const label = new CSS2DObject(tag);
      label.position.set(0, top + 0.05, 0);
      g.add(label);
      w.twins.add(g);
    }
  }, [twins, labelled, highlight]);

  // ── the run on show: nothing until Kit builds something; a design lands where the server put it ─────────────
  const showRun = useCallback(async (assembly?: Assembly) => {
    const a = assembly ?? await getCurrentAssembly();
    const [p, s] = await Promise.all([getPlan(a.plan_id, a.plan_revision), getState(a.assembly_id)]);
    const w = world.current;
    const changed = live.current.plan?.plan_id !== p.plan_id;
    setRun(a); setPlan(p); setState(s);
    if (!w || !changed) return;
    for (const part of w.parts) { w.design.remove(part.root); disposeObject(part.root); }
    w.parts = []; w.flights = [];
    const idea = live.current.ideas.find((i) => i.plan.plan_id === p.plan_id);
    if (p.parts.length === 0 || !idea) return;
    w.design.position.set(...idea.origin.position);
    w.design.quaternion.set(...idea.origin.rotation_quat);
    w.design.updateMatrixWorld(true);
    const byTwin = new Map(live.current.twins.map((t) => [t.twin_id, t]));
    const order = [...p.steps].sort((x, y) => x.index - y.index).flatMap((st) => st.part_ids);
    const now = performance.now();
    order.forEach((partId, k) => {
      const part = p.parts.find((x) => x.part_id === partId);
      const obj = part ? buildPartObject(part) : null;
      if (!obj || !part) return;
      w.design.add(obj.root);
      w.parts.push(obj);
      const twin = byTwin.get(idea.twin_of[partId] ?? "");
      if (!twin) return;
      const from = w.design.worldToLocal(new THREE.Vector3(...twin.position));
      const to = obj.root.position.clone();
      w.flights.push({ obj: obj.root, from, to, start: now + k * STAGGER_MS, lift: 0.15 + 0.25 * from.distanceTo(to) });
      obj.root.position.copy(from);
    });
    setTwins([]);                                                           // the pieces are on their way: the room's glow goes
  }, []);

  // Built parts look built; the current step's parts glow.
  useEffect(() => {
    const w = world.current;
    if (!w || !state || !plan) return;
    const step = plan.steps.find((s) => s.step_id === state.current_step_id);
    for (const p of w.parts) {
      const st = state.parts[p.partId]?.state;
      applyLook(p, st === "built" ? "built" : step?.part_ids.includes(p.partId) ? "highlight" : "default");
    }
  }, [state, plan]);

  useEffect(() => {
    (async () => {
      const cur = await getBuildCurrent().catch(() => null);
      if (cur?.session) { session.current = cur.session.session_id; setTwins(cur.twins); setLabelled(true); setIdeas(cur.ideas); setFinalIdeas(true); }
      await showRun();
    })().catch((e) => setStatus({ kind: "error", text: describeError(e) }));
  }, [showRun]);

  useStream((msg) => {
    if (msg.type === "build_inventory") {
      session.current = msg.inventory.session_id;
      setTwins(msg.inventory.twins); setLabelled(msg.inventory.labelled);
      if (msg.inventory.message) setStatus({ kind: "idle", text: msg.inventory.message });
      else if (!msg.inventory.labelled) setStatus({ kind: "busy", text: `Found ${msg.inventory.twins.length} objects. Naming them…` });
    } else if (msg.type === "build_ideas") {
      setIdeas(msg.ideas); setFinalIdeas(msg.final);
      if (msg.final) {
        setStatus({ kind: "idle", text: msg.message ?? IDLE });
        if (msg.audio_url) void play(msg.audio_url);
      }
    } else if (msg.type === "assembly_changed") void showRun(msg.assembly).catch((e) => setStatus({ kind: "error", text: describeError(e) }));
    else if (msg.type === "event_appended" && msg.assembly_id === live.current.run?.assembly_id) void getState(msg.assembly_id).then(setState);
  });

  // ── scanning and asking ───────────────────────────────────────────────────────────────────────────────────────
  const scan = useCallback(async () => {
    const w = world.current;
    if (!w) return;
    setStatus({ kind: "busy", text: "Scanning the kitchen…" });
    try {
      const { upload } = await w.rig.scan(w.room, w.camera, w.depthInvisible, { sessionId: session.current });
      const accepted = await postBuildScan(upload);
      session.current = accepted.session_id;
      setStatus({ kind: "busy", text: "Scan sent. Finding objects…" });
    } catch (e) { setStatus({ kind: "error", text: `Scan: ${describeError(e)}` }); }
  }, []);

  /** A spoken question (`audio`), or a typed one (`question`). */
  const ask = useCallback(async (audio: Blob | null, question?: string) => {
    const w = world.current, { run: r, plan: p, state: s } = live.current;
    if (!w || !r || !p || !s) return;
    setStatus({ kind: "busy", text: "Thinking…" });
    try {
      const context = { ...buildContextPacket({ assemblyId: r.assembly_id, planRevision: p.revision, stateVersion: s.version, selected: null,
        currentStepId: s.current_step_id, parts: [], camera: scanCamera(w.camera), width: PHOTO_W, height: PHOTO_H }),
        mode: (session.current ? "build" : "overlay") as "build" | "overlay" };
      const photo = await w.rig.photo(w.room, w.camera);
      const response = await askCopilot(r.assembly_id, context, audio, photo, question);
      setAnswer(response);
      setHighlight(response.highlight_twins ?? []);
      window.setTimeout(() => setHighlight([]), 6000);
      setStatus({ kind: "idle", text: IDLE });
      if (response.action?.type === "start_scan") await scan();
      if (response.audio_url) await play(response.audio_url);
    } catch (e) { setStatus({ kind: "error", text: describeError(e) }); }
  }, [scan]);

  useEffect(() => {
    let spaceDown = false;
    const down = async (e: KeyboardEvent) => {
      if (e.target instanceof HTMLInputElement) return;
      if (e.key === " ") {
        e.preventDefault();
        if (e.repeat || spaceDown || recording.current) return;
        spaceDown = true;
        setStatus({ kind: "busy", text: "Listening… release Space to ask" });
        try {
          const rec = await startRecording();
          if (spaceDown) recording.current = rec; else await rec.stop();
        } catch (err) { setStatus({ kind: "error", text: `Microphone: ${describeError(err)}` }); }
      } else if (e.key.toLowerCase() === "s") void scan();
    };
    const up = async (e: KeyboardEvent) => {
      if (e.key !== " ") return;
      spaceDown = false;
      const rec = recording.current;
      recording.current = null;
      if (rec) void ask(await rec.stop());
    };
    window.addEventListener("keydown", down);
    window.addEventListener("keyup", up);
    return () => { window.removeEventListener("keydown", down); window.removeEventListener("keyup", up); };
  }, [ask, scan]);

  const [typed, setTyped] = useState("");
  const askTyped = (e: FormEvent) => {
    e.preventDefault();
    const q = typed.trim();
    if (!q) return;
    setTyped("");
    void ask(null, q);
  };

  const start = async (idea: BuildIdea) => {
    setStatus({ kind: "busy", text: `Building the ${idea.title.toLowerCase()}…` });
    try { await startBuildIdea(idea.idea_id); } catch (e) { setStatus({ kind: "error", text: describeError(e) }); }
  };
  const startOver = async () => {
    try {
      await sendDirectorCommand({ type: "new_run", seed: "blank" });
      const s = await newBuildSession();
      session.current = s.session_id;
      setTwins([]); setIdeas([]); setAnswer(null);
      setStatus({ kind: "idle", text: IDLE });
    } catch (e) { setStatus({ kind: "error", text: describeError(e) }); }
  };

  const step = plan && state ? plan.steps.find((s) => s.step_id === state.current_step_id) : undefined;
  return (
    <main className="preview-page kitchen-page">
      <div ref={host} className="kitchen-canvas" />
      <aside className="preview-hud kitchen-panel">
        <p className="preview-hud-title">Kitchen · pretend headset</p>
        {plan && plan.parts.length > 0 && state ? (
          <>
            <p className="preview-hud-progress">{plan.name}: {state.progress.built} of {state.progress.total} placed</p>
            <p className="preview-hud-step">{step ? `Step ${step.index}: ${step.instruction}` : "Done!"}</p>
          </>
        ) : <p className="preview-hud-muted">Nothing built yet.</p>}
        {twins.length > 0 ? <p className="preview-hud-muted">{twins.length} objects{labelled ? "" : " (naming…)"}</p>
          : !plan?.parts.length && <p className="preview-hud-muted">No scan yet.</p>}
        {ideas.length > 0 && (
          <div className="kitchen-ideas">
            <p className="label">{finalIdeas ? "Kit's ideas" : "Ideas so far (Kit is still thinking)"}</p>
            {ideas.map((i) => (
              <div key={i.idea_id} className="kitchen-idea">
                <strong>{i.title}</strong> <small>{i.source === "rule" ? "from the library" : "Kit's own"}</small>
                <p>{i.why}</p>
                <button type="button" onClick={() => void start(i)}>Build this</button>
              </div>
            ))}
          </div>
        )}
        <form className="kitchen-ask" onSubmit={askTyped}>
          <input value={typed} onChange={(e) => setTyped(e.target.value)} onKeyDown={(e) => { if (e.key === "Enter") askTyped(e); }}
            placeholder="Ask Kit: What can I build?" aria-label="Ask Kit" />
          <button type="submit" disabled={!typed.trim()}>Ask</button>
        </form>
        <div className="kitchen-actions">
          <button type="button" className="primary" onClick={() => void scan()}>Scan (S)</button>
          <button type="button" onClick={() => void startOver()}>Start over</button>
        </div>
      </aside>
      {answer && (
        <section className="sim-answer" aria-live="polite">
          <p className="sim-transcript">“{answer.transcript}”</p>
          <p>{answer.answer_text}</p>
        </section>
      )}
      <p className={`sim-status ${status.kind === "error" ? "error" : status.kind === "busy" ? "thinking" : "idle"}`}>{status.text}</p>
    </main>
  );
}

async function play(audioUrl: string): Promise<void> {
  try {
    const url = await fetchAnswerAudio(audioUrl);
    const player = new Audio(url);
    player.onended = () => URL.revokeObjectURL(url);
    await player.play();
  } catch { /* speech is a bonus: the text is on screen */ }
}

function sizeText(t: Twin): string {
  const cm = (m: number) => Math.round(m * 1000) / 10;
  const s = t.shape;
  const txt = s.type === "cylinder" ? `${cm(s.diameter)} × ${cm(s.length)} cm` : `${cm(s.size[0])} × ${cm(s.size[2])} × ${cm(s.size[1])} cm`;
  return t.snapped ? txt : `≈ ${txt}`;
}

export default KitchenPage;
