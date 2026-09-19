import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useLocation } from "react-router-dom";
import type * as THREE from "three";
import { ulid } from "ulid";
import { partAabb, resolveVisuals } from "@cutonce/project-model";
import type { Assembly, BuildState, CopilotResponse, PartState, Plan } from "@cutonce/schemas";
import {
  askCopilot, authHeaders, describeError, fetchAnswerAudio, getBuildCurrent, getCurrentAssembly, getPlan, getState, planAssetUrl, postEvent,
  replayBuildScan,
} from "../api";
import { HologramView } from "../three/hologram/HologramView";
import { useStream } from "../ws";
import { buildContextPacket } from "./contextPacket";
import { FRAME_H, FRAME_W, grabFrame, stopWebcam, type FrameSource } from "./frameGrab";
import { silentWav, startRecording, type Recording } from "./micWav";

type Status = { kind: "idle" | "listening" | "thinking" | "info" | "error"; text: string };
const IDLE: Status = { kind: "idle", text: "Point at a part. Hold Space to ask." };
const HIGHLIGHT_MS = 6000;
/** A run build mode started (services/api/src/build/plan.ts names its plans so). Any other run ends build mode. */
const isBuildRun = (planId: string) => planId.startsWith("plan_build_");

/**
 * /sim: a pretend headset on the laptop. The mouse is the controller ray, B marks built, hold Space to ask
 * through the laptop mic. It sends the server exactly what the Quest will: the same events, the same
 * context packet, a recorded question and one camera frame. So the copilot and the history can be tested
 * without the headset. What it can't test: alignment on the real desk, and how the headset itself draws.
 *
 * Build mode (K, ?mode=build, or any answer that asks for a scan) makes every turn Kit's, as on the headset. The
 * laptop has no depth camera, so a scan replays a recording (?kit=synthetic_kit) with its saved names.
 */
export function SimPage() {
  const location = useLocation();
  const params = useMemo(() => new URLSearchParams(location.search), [location.search]);
  const still = params.get("still") === "1";
  const recording = params.get("kit") ?? "synthetic_kit";
  const [frameSource, setFrameSource] = useState<FrameSource>(params.get("frame") === "webcam" ? "webcam" : "render");
  const [buildMode, setBuildMode] = useState(params.get("mode") === "build");
  const [kit, setKit] = useState<{ wish: string | null; designs: string[]; note: string | null }>({ wish: null, designs: [], note: null });

  const [run, setRun] = useState<Assembly | null>(null);
  const [plan, setPlan] = useState<Plan | null>(null);
  const [state, setState] = useState<BuildState | null>(null);
  const [pointed, setPointed] = useState<string | null>(null);
  const [status, setStatus] = useState<Status>(IDLE);
  const [answer, setAnswer] = useState<CopilotResponse | null>(null);
  const [highlight, setHighlight] = useState<string[]>([]);
  const cameraRef = useRef<THREE.PerspectiveCamera | null>(null);
  const mic = useRef<Recording | null>(null);
  const live = useRef({ run, plan, state, pointed, frameSource, buildMode });
  live.current = { run, plan, state, pointed, frameSource, buildMode };

  const load = useCallback(async (assembly?: Assembly) => {
    const a = assembly ?? await getCurrentAssembly();
    const [p, s] = await Promise.all([getPlan(a.plan_id, a.plan_revision), getState(a.assembly_id)]);
    setRun(a); setPlan(p); setState(s);
  }, []);
  useEffect(() => { load().catch((e) => setStatus({ kind: "error", text: describeError(e) })); }, [load]);
  useEffect(() => () => stopWebcam(), []);

  useStream((msg) => {
    if (msg.type === "assembly_changed") {
      // As on the headset: a run build mode did not start (E7, the desk) ends build mode.
      if (live.current.buildMode && !isBuildRun(msg.assembly.plan_id)) setBuildMode(false);
      void load(msg.assembly).catch((e) => setStatus({ kind: "error", text: describeError(e) }));
    } else if (msg.type === "event_appended" && msg.assembly_id === live.current.run?.assembly_id) {
      void getState(msg.assembly_id).then(setState).catch((e) => setStatus({ kind: "error", text: describeError(e) }));
    } else if (msg.type === "build_inventory" && msg.inventory.message) {
      setKit((k) => ({ ...k, note: msg.inventory.message }));
    } else if (msg.type === "build_ideas" && msg.final) {
      setKit((k) => ({ ...k, designs: msg.ideas.map((i) => i.title), note: msg.message ?? k.note }));
      // The wish is not on the stream: ask for it when a list is final.
      void getBuildCurrent().then((c) => setKit((k) => ({ ...k, wish: c.wish })), () => {});
    }
  });

  const setPart = useCallback(async (partId: string | null, next: PartState, source: "manual" | "voice") => {
    const { run: r, plan: p, state: s } = live.current;
    if (!r || !p || !s) return;
    if (!partId) { setStatus({ kind: "info", text: "Point at a part first." }); return; }
    const name = p.parts.find((x) => x.part_id === partId)?.name ?? partId;
    const before = s.parts[partId]?.state ?? "missing";
    if (before === next) { setStatus({ kind: "info", text: `${name} is already ${next}.` }); return; }
    const now = new Date().toISOString();
    try {
      await postEvent(r.assembly_id, {
        event_id: `evt_${ulid()}`, assembly_id: r.assembly_id, version: null, timestamp: now, client_timestamp: now, kind: "part_state",
        part_id: partId, previous_state: before, new_state: next, source, confidence: 1, actor: "sim",
      });
      setStatus({ kind: "info", text: `${name}: ${before} → ${next}` });
    } catch (e) { setStatus({ kind: "error", text: describeError(e) }); }
  }, []);

  const ask = useCallback(async (audio: Blob, scriptedQueryId: string | null) => {
    const { run: r, plan: p, state: s, pointed: sel, frameSource: src, buildMode: building } = live.current;
    const camera = cameraRef.current;
    if (!r || !p || !s || !camera) return;
    setStatus({ kind: "thinking", text: "Thinking…" });
    try {
      const parts = p.parts.flatMap((part) => {
        const box = partAabb(part);
        return box ? [{ part_id: part.part_id, state: s.parts[part.part_id]?.state ?? "missing", box }] : [];
      });
      const context = buildContextPacket({
        assemblyId: r.assembly_id, planRevision: p.revision, stateVersion: s.version, selected: sel, currentStepId: s.current_step_id,
        parts, camera, width: FRAME_W, height: FRAME_H, scriptedQueryId, mode: building ? "build" : "overlay",
      });
      const frame = await grabFrame(src);
      const started = performance.now();
      const response = await askCopilot(r.assembly_id, context, audio, frame);
      setAnswer(response);
      setHighlight(response.highlight_parts);
      window.setTimeout(() => setHighlight([]), HIGHLIGHT_MS);
      setStatus({ kind: "idle", text: `Answered in ${((performance.now() - started) / 1000).toFixed(1)} s. ${IDLE.text}` });
      if (response.action?.type === "mark_state") {
        for (const id of response.action.part_ids) await setPart(id, response.action.new_state, "voice");
      }
      if (response.action?.type === "start_scan") {
        // The headset would scan the table now; the laptop has no depth camera, so it replays a recorded scan.
        setBuildMode(true);
        setStatus({ kind: "info", text: `Scanning: replaying the ${recording.replace(/_/g, " ")} recording…` });
        await replayBuildScan(`scan_rec_${recording}`, "saved");
      }
      if (response.audio_url) {
        const url = await fetchAnswerAudio(response.audio_url);
        const player = new Audio(url);
        player.onended = () => URL.revokeObjectURL(url);
        await player.play().catch(() => {});
      }
    } catch (e) { setStatus({ kind: "error", text: describeError(e) }); }
  }, [setPart]);

  useEffect(() => {
    // The microphone opens asynchronously (the first time behind a permission prompt), so Space can be
    // released, or pressed again, before it is ready. Track the key itself, and allow one opening at a time.
    let spaceDown = false;
    let opening = false;
    const down = async (e: KeyboardEvent) => {
      if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return;
      const key = e.key.toLowerCase();
      if (key === " ") {
        e.preventDefault();
        if (e.repeat || spaceDown) return;
        spaceDown = true;
        if (opening || mic.current) return;
        opening = true;
        // The first use shows a permission prompt, so say what is happening while the mic opens.
        setStatus({ kind: "listening", text: "Opening the microphone… keep holding Space" });
        try {
          const rec = await startRecording();
          if (spaceDown) {
            mic.current = rec;
            setStatus({ kind: "listening", text: "Listening… release Space to ask" });
          } else {
            await rec.stop(); // released while the mic was opening: nothing was said
            setStatus({ kind: "info", text: "Hold Space while you speak." });
          }
        } catch (err) { setStatus({ kind: "error", text: `Microphone: ${describeError(err)}` }); }
        finally { opening = false; }
      } else if (key === "b") void setPart(live.current.pointed, "built", "manual");
      else if (key === "w") void setPart(live.current.pointed, "wrong", "manual");
      else if (key === "m") void setPart(live.current.pointed, "missing", "manual");
      else if (key === "1" || key === "2" || key === "3") void ask(silentWav(), `q${key}`);
      else if (key === "f") setFrameSource((f) => (f === "render" ? "webcam" : "render"));
      else if (key === "k") setBuildMode((on) => !on);
    };
    const up = async (e: KeyboardEvent) => {
      if (e.key !== " ") return;
      e.preventDefault();
      spaceDown = false;
      if (!mic.current) return;
      const rec = mic.current;
      mic.current = null;
      void ask(await rec.stop(), null);
    };
    window.addEventListener("keydown", down);
    window.addEventListener("keyup", up);
    return () => { window.removeEventListener("keydown", down); window.removeEventListener("keyup", up); };
  }, [ask, setPart]);

  const visuals = useMemo(
    () => (plan && state ? resolveVisuals(plan, state, { selected: pointed, highlighted: highlight }) : null),
    [plan, state, pointed, highlight],
  );
  const headers = useMemo(() => authHeaders(), []);
  const onReady = useCallback(() => { (window as unknown as { __previewReady?: boolean }).__previewReady = true; }, []);

  if (!plan || !state || !visuals) {
    return <main className="preview-page"><p className={status.kind === "error" ? "preview-error" : "preview-loading"}>{status.kind === "error" ? status.text : "Loading…"}</p></main>;
  }
  const step = plan.steps.find((s) => s.step_id === state.current_step_id);
  const pointedName = pointed ? plan.parts.find((p) => p.part_id === pointed)?.name ?? pointed : null;

  return (
    <main className="preview-page">
      <HologramView
        plan={plan} visuals={visuals} view="operator" fov={90} still={still}
        background={frameSource === "webcam" ? { kind: "webcam" } : { kind: "none" }}
        assetUrl={planAssetUrl} authHeaders={headers} onReady={onReady} onPoint={setPointed} cameraRef={cameraRef}
      />
      <aside className="preview-hud">
        <p className="preview-hud-title">{plan.name} · pretend headset</p>
        <p className="preview-hud-progress">{state.progress.built} of {state.progress.total} built</p>
        <div className="preview-hud-bar"><div style={{ width: `${state.progress.pct}%` }} /></div>
        <p className="preview-hud-step">{step ? `Step ${step.index} · ${step.title}` : "Complete"}</p>
        <p className="preview-hud-muted">Pointing at: <span className="sim-pointing">{pointedName ?? "nothing"}</span></p>
        {buildMode && (
          <div className="sim-kit">
            <p className="preview-hud-title">Build mode · every turn is Kit's</p>
            {kit.wish && <p className="preview-hud-muted">Asked for: {kit.wish}</p>}
            <p className="preview-hud-muted">
              {kit.designs.length ? `On show, left to right: ${kit.designs.map((d, i) => `${i + 1}. ${d}`).join(" · ")}` : "No designs yet: ask “what can I build?”"}
            </p>
            {kit.note && <p className="preview-hud-muted">{kit.note}</p>}
          </div>
        )}
      </aside>
      <aside className="sim-keys">
        <p><kbd>mouse</kbd>point (controller ray)</p>
        <p><kbd>B</kbd>mark built · <kbd>W</kbd>wrong · <kbd>M</kbd>missing</p>
        <p><kbd>Space</kbd>hold to ask (laptop mic)</p>
        <p><kbd>1</kbd><kbd>2</kbd><kbd>3</kbd>scripted questions</p>
        <p><kbd>F</kbd>camera: {frameSource === "webcam" ? "webcam" : "hologram view"}</p>
        <p><kbd>K</kbd>build mode: {buildMode ? "on (Kit)" : "off"}</p>
      </aside>
      {answer && (
        <section className="sim-answer" aria-live="polite">
          <p className="sim-transcript">“{answer.transcript}”</p>
          <p>{answer.answer_text}</p>
          {answer.drawing_refs.map((r) => (
            <span key={`${r.document_id}-${r.page}`} className="sim-source">{r.title} · page {r.page}</span>
          ))}
        </section>
      )}
      <p className={`sim-status ${status.kind}`}>{status.text}</p>
    </main>
  );
}

export default SimPage;
