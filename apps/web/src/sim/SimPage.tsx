import { useCallback, useEffect, useMemo, useRef, useState, type MouseEvent } from "react";
import { useLocation } from "react-router-dom";
import type * as THREE from "three";
import { ulid } from "ulid";
import { partAabb, resolveVisuals } from "@cutonce/project-model";
import type { Assembly, BuildIdea, BuildState, CopilotResponse, PartState, Plan, Surface, Twin } from "@cutonce/schemas";
import {
  askCopilot, authHeaders, describeError, fetchAnswerAudio, getBuildCurrent, getCurrentAssembly, getPlan, getState, planAssetUrl, postEvent,
  replayBuildScan, sayBuild, startBuildIdea,
} from "../api";
import { HologramView } from "../three/hologram/HologramView";
import { useStream } from "../ws";
import { LAPTOP_PREVIEW, buildScene, sceneVisuals } from "./buildScene";
import { buildContextPacket } from "./contextPacket";
import { FRAME_H, FRAME_W, grabFrame, stopWebcam, type FrameSource } from "./frameGrab";
import { silentWav, startRecording, type Recording } from "./micWav";

type Status = { kind: "idle" | "listening" | "thinking" | "info" | "error"; text: string };
/** What Kit last said: an answer (with what it heard and its sources) or something it announced on its own. */
type Line = { heard: string | null; text: string; refs: CopilotResponse["drawing_refs"] };
const IDLE: Status = { kind: "idle", text: "Point at a part. Hold Space to ask." };
const HIGHLIGHT_MS = 6000;
/** POST /v1/build/say refuses more (BuildMode.cs's MaxSpokenCharacters). */
const MAX_SPOKEN = 400;
/** A run build mode started (services/api/src/build/plan.ts names its plans so). Any other run ends build mode. */
const isBuildRun = (planId: string) => planId.startsWith("plan_build_");
/** A preview part's design, by its place in the list: buildScene names them part_idea<i>_… */
const previewIndex = (partId: string | null) => (partId ? /^part_idea(\d+)_/.exec(partId)?.[1] : undefined);

/** scanRun: the run that was current when the scan arrived. Choosing lasts until another run starts (a design is picked). */
interface Kit { session: string | null; scanRun: string | null; wish: string | null; note: string | null; surfaces: Surface[]; twins: Twin[]; ideas: BuildIdea[] }
const NO_KIT: Kit = { session: null, scanRun: null, wish: null, note: null, surfaces: [], twins: [], ideas: [] };

/**
 * /sim: a pretend headset on the laptop. The mouse is the controller ray, B marks built, hold Space to ask
 * through the laptop mic. It sends the server exactly what the Quest will: the same events, the same
 * context packet, a recorded question and one camera frame. So the copilot and the history can be tested
 * without the headset. What it can't test: alignment on the real desk, and how the headset itself draws.
 *
 * Build mode (K, ?mode=build, a scan, or any answer that asks for one) makes every turn Kit's and shows what the
 * headset shows (Device/Build/BuildMode.cs): the objects the scan found, Kit's designs floating above them (click one,
 * or name it, to build it), then the chosen design with every step read aloud. The laptop has no depth camera, so a
 * scan replays a recording (?kit=synthetic_kit) with its saved names.
 */
export function SimPage() {
  const location = useLocation();
  const params = useMemo(() => new URLSearchParams(location.search), [location.search]);
  const still = params.get("still") === "1";
  const recording = params.get("kit") ?? "synthetic_kit";
  const [frameSource, setFrameSource] = useState<FrameSource>(params.get("frame") === "webcam" ? "webcam" : "render");
  const [buildMode, setBuildMode] = useState(params.get("mode") === "build");
  const [kit, setKit] = useState<Kit>(NO_KIT);

  const [run, setRun] = useState<Assembly | null>(null);
  const [plan, setPlan] = useState<Plan | null>(null);
  const [state, setState] = useState<BuildState | null>(null);
  const [pointed, setPointed] = useState<string | null>(null);
  const [status, setStatus] = useState<Status>(IDLE);
  const [line, setLine] = useState<Line | null>(null);
  const [highlight, setHighlight] = useState<string[]>([]);
  const [litTwins, setLitTwins] = useState<string[]>([]);
  const cameraRef = useRef<THREE.PerspectiveCamera | null>(null);
  const mic = useRef<Recording | null>(null);
  const live = useRef({ run, plan, state, pointed, frameSource, buildMode, kit });
  live.current = { run, plan, state, pointed, frameSource, buildMode, kit };

  // ── Kit's voice: one clip at a time, in order, so a step read aloud never talks over the answer before it ──
  const voice = useRef<Promise<void>>(Promise.resolve());
  const speak = useCallback((audioUrl: string | null | undefined) => {
    if (!audioUrl) return;
    voice.current = voice.current.then(async () => {
      try {
        const url = await fetchAnswerAudio(audioUrl);
        await new Promise<void>((resolve) => {
          const player = new Audio(url);
          const done = () => { URL.revokeObjectURL(url); resolve(); };
          player.onended = done; player.onerror = done;
          player.play().catch(done);
        });
      } catch (e) { setStatus({ kind: "error", text: `Kit's voice: ${describeError(e)}` }); }
    });
  }, []);

  const load = useCallback(async (assembly?: Assembly) => {
    const a = assembly ?? await getCurrentAssembly();
    const [p, s] = await Promise.all([getPlan(a.plan_id, a.plan_revision), getState(a.assembly_id)]);
    setRun(a); setPlan(p); setState(s);
    if (isBuildRun(p.plan_id)) setBuildMode(true);                    // a design being built is build mode, however this page was opened
  }, []);
  useEffect(() => { load().catch((e) => setStatus({ kind: "error", text: describeError(e) })); }, [load]);
  useEffect(() => () => stopWebcam(), []);

  // Build mode on: take the session as it stands (a reload, or a scan the Director replayed before this page opened).
  useEffect(() => {
    if (!buildMode) return;
    getBuildCurrent().then((c) => setKit((k) => ({
      ...k, session: c.session?.session_id ?? null, wish: c.wish, surfaces: c.surfaces ?? [], twins: c.twins, ideas: c.ideas,
    })), () => {});
  }, [buildMode]);

  const lastSource = useRef<string | null>(null);
  useStream((msg) => {
    if (msg.type === "assembly_changed") {
      // As on the headset: another run build mode did not start (E7, the desk) ends build mode. The stream sends the
      // current run again on every (re)connect: that is not another run, and must not end build mode (?mode=build, K).
      const other = live.current.run !== null && msg.assembly.assembly_id !== live.current.run.assembly_id;
      if (other && live.current.buildMode && !isBuildRun(msg.assembly.plan_id)) setBuildMode(false);
      lastSource.current = null;
      void load(msg.assembly).catch((e) => setStatus({ kind: "error", text: describeError(e) }));
    } else if (msg.type === "event_appended" && msg.assembly_id === live.current.run?.assembly_id) {
      lastSource.current = msg.event.source;
      void getState(msg.assembly_id).then(setState).catch((e) => setStatus({ kind: "error", text: describeError(e) }));
    } else if (msg.type === "build_inventory") {
      // A scan (the headset's, or the Director's Replay) starts build mode here too. Another session's objects take the
      // old session's designs with them.
      const inv = msg.inventory;
      setBuildMode(true);
      setKit((k) => ({
        ...k, session: inv.session_id, scanRun: live.current.run?.assembly_id ?? null, surfaces: inv.surfaces, twins: inv.twins,
        note: inv.message ?? k.note, ideas: k.session === inv.session_id ? k.ideas : [],
      }));
    } else if (msg.type === "build_ideas" && msg.final) {
      setKit((k) => ({ ...k, session: msg.session_id, ideas: msg.ideas, note: msg.message ?? k.note }));
      if (msg.message) setLine({ heard: null, text: msg.message, refs: [] });
      speak(msg.audio_url);
      // The wish is not on the stream: ask for it when a list is final.
      void getBuildCurrent().then((c) => setKit((k) => ({ ...k, wish: c.wish })), () => {});
    }
  });

  const setPart = useCallback(async (partId: string, next: PartState, source: "manual" | "voice") => {
    const { run: r, plan: p, state: s } = live.current;
    if (!r || !p || !s) return;
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

  /** B, W or M: the part pointed at; with nothing pointed at mid-build, B marks the whole current step (BuildMode.MarkCurrentStep). */
  const markPointed = useCallback(async (next: PartState) => {
    const { plan: p, state: s, pointed: at } = live.current;
    if (!p || !s) return;
    if (at && p.parts.some((x) => x.part_id === at)) { await setPart(at, next, "manual"); return; }
    if (next === "built" && isBuildRun(p.plan_id)) {
      const step = p.steps.find((x) => x.step_id === s.current_step_id);
      if (!step) { setStatus({ kind: "info", text: "Every step is built." }); return; }
      for (const id of step.part_ids) if (s.parts[id]?.state !== "built") await setPart(id, "built", "manual");
      return;
    }
    setStatus({ kind: "info", text: previewIndex(at) !== undefined ? "Click a design to build it." : "Point at a part first." });
  }, [setPart]);

  const pick = useCallback(async (ideaId: string) => {
    const idea = live.current.kit.ideas.find((i) => i.idea_id === ideaId);
    setStatus({ kind: "thinking", text: `Building the ${(idea?.title ?? "design").toLowerCase()}…` });
    try {
      await startBuildIdea(ideaId);                                 // the run arrives on the stream (assembly_changed)
      setStatus(IDLE);
    } catch (e) { setStatus({ kind: "error", text: describeError(e) }); }
  }, []);

  const ask = useCallback(async (audio: Blob, scriptedQueryId: string | null) => {
    const { run: r, plan: p, state: s, pointed: sel, frameSource: src, buildMode: building } = live.current;
    const camera = cameraRef.current;
    if (!r || !p || !s || !camera) return;
    setStatus({ kind: "thinking", text: "Thinking…" });
    try {
      const inPlan = sel !== null && p.parts.some((x) => x.part_id === sel);   // an object or a design preview is not one of the run's parts
      const parts = p.parts.flatMap((part) => {
        const box = partAabb(part);
        return box ? [{ part_id: part.part_id, state: s.parts[part.part_id]?.state ?? "missing", box }] : [];
      });
      const context = buildContextPacket({
        assemblyId: r.assembly_id, planRevision: p.revision, stateVersion: s.version, selected: inPlan ? sel : null, currentStepId: s.current_step_id,
        parts, camera, width: FRAME_W, height: FRAME_H, scriptedQueryId, mode: building ? "build" : "overlay",
      });
      const frame = await grabFrame(src);
      const started = performance.now();
      const response = await askCopilot(r.assembly_id, context, audio, frame);
      setLine({ heard: response.transcript, text: response.answer_text, refs: response.drawing_refs });
      setHighlight(response.highlight_parts);
      setLitTwins(response.highlight_twins ?? []);
      window.setTimeout(() => { setHighlight([]); setLitTwins([]); }, HIGHLIGHT_MS);
      setStatus({ kind: "idle", text: `Answered in ${((performance.now() - started) / 1000).toFixed(1)} s. ${IDLE.text}` });
      speak(response.audio_url);
      if (response.action?.type === "mark_state") {
        for (const id of response.action.part_ids) await setPart(id, response.action.new_state, "voice");
      }
      if (response.action?.type === "start_scan") {
        // The headset would scan the table now; the laptop has no depth camera, so it replays a recorded scan.
        setBuildMode(true);
        setStatus({ kind: "info", text: `Scanning: replaying the ${recording.replace(/_/g, " ")} recording…` });
        await replayBuildScan(`scan_rec_${recording}`, "saved");
      }
    } catch (e) { setStatus({ kind: "error", text: describeError(e) }); }
  }, [setPart, speak, recording]);

  // ── the walkthrough: each new step of a build is read aloud, as the headset does (BuildMode.SpeakCurrentStep) ──
  const spoken = useRef<string | null>(null);
  useEffect(() => {
    if (!run || !plan || !state || !isBuildRun(plan.plan_id) || state.assembly_id !== run.assembly_id) return;
    const key = `${run.assembly_id}:${state.current_step_id ?? "done"}`;
    if (spoken.current === key) return;
    const sameRun = spoken.current?.startsWith(`${run.assembly_id}:`) ?? false;
    spoken.current = key;
    if (sameRun && lastSource.current === "voice") return;           // a spoken "done" already read the next step out
    const step = plan.steps.find((x) => x.step_id === state.current_step_id);
    const text = step ? step.instruction : state.progress.built === state.progress.total ? "That's the whole build. Nice work!" : null;
    if (!text) return;
    setLine({ heard: null, text: step ? `Step ${step.index} of ${plan.steps.length}: ${step.instruction}` : text, refs: [] });
    sayBuild(text.slice(0, MAX_SPOKEN)).then((r) => speak(r.audio_url), (e) => setStatus({ kind: "error", text: `Kit's voice: ${describeError(e)}` }));
  }, [run, plan, state, speak]);

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
      } else if (key === "b") void markPointed("built");
      else if (key === "w") void markPointed("wrong");
      else if (key === "m") void markPointed("missing");
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
  }, [ask, markPointed]);

  // ── what is drawn: the run, or (choosing a design) the objects and Kit's designs ──
  // As on the headset, a scan mid-build ("something crazier") shows the objects and new designs again, over the build
  // still under way; picking one starts another run, and the walkthrough takes over.
  const building = plan !== null && isBuildRun(plan.plan_id);
  const scannedOverThisRun = kit.scanRun !== null && run !== null && kit.scanRun === run.assembly_id;
  const choosing = buildMode && kit.twins.length > 0 && (!building || scannedOverThisRun);
  const hoveredIdea = choosing ? kit.ideas[Number(previewIndex(pointed) ?? -1)]?.idea_id ?? null : null;
  const scene = useMemo(
    () => (choosing && plan ? buildScene(plan, kit.surfaces, kit.twins, kit.ideas, LAPTOP_PREVIEW) : null),
    [choosing, plan, kit.surfaces, kit.twins, kit.ideas],
  );
  const sceneLook = useMemo(() => (scene ? sceneVisuals(scene, { hoveredIdea, highlightTwins: litTwins }) : null), [scene, hoveredIdea, litTwins]);
  // What is drawn changed (the designs arrived, a design was picked): what the pointer was over is gone with it.
  const drawnPlan = scene?.plan ?? plan;
  useEffect(() => { setPointed(null); }, [drawnPlan]);
  const runVisuals = useMemo(
    () => (plan && state ? resolveVisuals(plan, state, { selected: pointed, highlighted: highlight }) : null),
    [plan, state, pointed, highlight],
  );
  const headers = useMemo(() => authHeaders(), []);
  const onReady = useCallback(() => { (window as unknown as { __previewReady?: boolean }).__previewReady = true; }, []);

  // A click (not the end of a drag that turned the view) on a design builds it: the headset's trigger.
  const downAt = useRef<[number, number] | null>(null);
  const onClick = (e: MouseEvent) => {
    const d = downAt.current;
    if (!d || Math.hypot(e.clientX - d[0], e.clientY - d[1]) > 5 || !hoveredIdea) return;
    void pick(hoveredIdea);
  };

  if (!plan || !state || !runVisuals) {
    return <main className="preview-page"><p className={status.kind === "error" ? "preview-error" : "preview-loading"}>{status.kind === "error" ? status.text : "Loading…"}</p></main>;
  }
  const step = plan.steps.find((s) => s.step_id === state.current_step_id);
  const pointedName = pointed ? scene?.label[pointed] ?? plan.parts.find((p) => p.part_id === pointed)?.name ?? pointed : null;
  const hint = !buildMode ? null
    : choosing && kit.ideas.length ? `Click a design to build it, or say “the left one” or “build the ${kit.ideas[0]!.title.toLowerCase()}”.`
    : choosing ? "Kit is designing with what it found…"
    : building ? "Say “done” when a step is built (or press B). Ask Kit anything while you work."
    : "Hold Space and ask “What can I build?”";

  return (
    <main className="preview-page" onPointerDown={(e) => { downAt.current = [e.clientX, e.clientY]; }} onClick={onClick}
      style={hoveredIdea ? { cursor: "pointer" } : undefined}>
      <HologramView
        plan={scene?.plan ?? plan} visuals={sceneLook ?? runVisuals} frame={scene?.focus ?? null} view={!scene && building ? "orbit" : "operator"} fov={90} still={still}
        background={frameSource === "webcam" ? { kind: "webcam" } : { kind: "none" }}
        assetUrl={planAssetUrl} authHeaders={headers} onReady={onReady} onPoint={setPointed} cameraRef={cameraRef}
      />
      <aside className="preview-hud">
        <p className="preview-hud-title">{choosing ? "Build mode" : plan.name} · pretend headset</p>
        {choosing ? (
          <>
            <p className="preview-hud-progress">{kit.twins.length} objects found</p>
            {kit.wish && <p className="preview-hud-muted">Asked for: {kit.wish}</p>}
            {kit.note && kit.ideas.length === 0 && <p className="preview-hud-muted">{kit.note}</p>}
            {kit.ideas.length > 0 && (
              <ol className="sim-designs">
                {kit.ideas.slice(0, 3).map((idea) => <li key={idea.idea_id} className={idea.idea_id === hoveredIdea ? "on" : undefined}>{idea.title}</li>)}
              </ol>
            )}
          </>
        ) : (
          <>
            <p className="preview-hud-progress">{state.progress.built} of {state.progress.total} built</p>
            <div className="preview-hud-bar"><div style={{ width: `${state.progress.pct}%` }} /></div>
            <p className="preview-hud-step">{step ? `Step ${step.index} · ${step.title}` : "Complete"}</p>
          </>
        )}
        <p className="preview-hud-muted">Pointing at: <span className="sim-pointing">{pointedName ?? "nothing"}</span></p>
        {hint && <p className="sim-hint">{hint}</p>}
      </aside>
      <aside className="sim-keys">
        <p><kbd>mouse</kbd>point (controller ray){choosing && kit.ideas.length > 0 ? " · click a design to build it" : ""}</p>
        <p><kbd>B</kbd>mark built · <kbd>W</kbd>wrong · <kbd>M</kbd>missing</p>
        <p><kbd>Space</kbd>hold to ask (laptop mic)</p>
        <p><kbd>1</kbd><kbd>2</kbd><kbd>3</kbd>scripted questions</p>
        <p><kbd>F</kbd>camera: {frameSource === "webcam" ? "webcam" : "hologram view"}</p>
        <p><kbd>K</kbd>build mode: {buildMode ? "on (Kit)" : "off"}</p>
      </aside>
      {line && (
        <section className="sim-answer" aria-live="polite">
          {line.heard !== null && <p className="sim-transcript">“{line.heard}”</p>}
          <p>{line.text}</p>
          {line.refs.map((r) => (
            <span key={`${r.document_id}-${r.page}`} className="sim-source">{r.title} · page {r.page}</span>
          ))}
        </section>
      )}
      <p className={`sim-status ${status.kind}`}>{status.text}</p>
    </main>
  );
}

export default SimPage;
