import { useCallback, useEffect, useMemo, useState } from "react";
import { useLocation } from "react-router-dom";
import { HOLOGRAM_PALETTE, fold, plannedEvents, resolveVisuals, stateForBuilt, type BaseVisual } from "@cutonce/project-model";
import type { BuildState, Plan } from "@cutonce/schemas";
import { authHeaders, describeError, getCurrentAssembly, getPlan, getState, planAssetUrl } from "../api";
import { HologramView, type Background } from "../three/hologram/HologramView";
import { useStream } from "../ws";
import { parsePreviewParams } from "./previewParams";

const REPLAY_START = "2026-01-01T00:00:00.000Z";
const PLAY_SECONDS = 10;
const LEGEND: Array<[BaseVisual, string]> = [
  ["CURRENT_STEP", "Build this now"], ["MISSING", "Not built yet"], ["FUTURE", "Later"],
  ["BUILT_LIVE", "Built"], ["BUILT_REPLAY", "Built (replay)"], ["WRONG", "Wrong"],
];

/** The planned build up to a fraction, as the timeline replay shows it. */
function replayState(plan: Plan, fraction: number): BuildState {
  const events = plannedEvents(plan, "asm_preview", REPLAY_START);
  return fold(plan, "asm_preview", events.slice(0, Math.ceil(events.length * fraction)));
}

/**
 * /preview: a plan drawn exactly as the headset will draw it (blueprint §8), from a person's eye height.
 * The URL decides everything (see previewParams.ts), so the simulation can photograph fixed scenes.
 */
export function PreviewPage() {
  const location = useLocation();
  const p = useMemo(() => parsePreviewParams(location.search), [location.search]);
  const live = p.built === null && p.replay === null;

  const [plan, setPlan] = useState<Plan | null>(null);
  const [compare, setCompare] = useState<Plan | null>(null);
  const [liveState, setLiveState] = useState<BuildState | null>(null);
  const [liveRun, setLiveRun] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [playT, setPlayT] = useState(0);

  // The plan: the one named in the URL, or in live mode the current run's plan.
  useEffect(() => {
    let alive = true;
    setError(null);
    (async () => {
      if (live) {
        const run = await getCurrentAssembly();
        const [runPlan, state] = await Promise.all([getPlan(run.plan_id, run.plan_revision), getState(run.assembly_id)]);
        if (!alive) return;
        setLiveRun(run.assembly_id); setPlan(runPlan); setLiveState(state);
      } else {
        const named = await getPlan(p.planId, p.revision ?? undefined);
        if (alive) setPlan(named);
      }
      if (p.compare) { const other = await getPlan(p.compare); if (alive) setCompare(other); } else setCompare(null);
    })().catch((e) => alive && setError(describeError(e)));
    return () => { alive = false; };
  }, [live, p.planId, p.revision, p.compare]);

  // Live mode follows the run: every appended event or new run refetches the state.
  useStream((msg) => {
    if (!live) return;
    if (msg.type === "assembly_changed") {
      setLiveRun(msg.assembly.assembly_id);
      void Promise.all([getPlan(msg.assembly.plan_id, msg.assembly.plan_revision), getState(msg.assembly.assembly_id)])
        .then(([runPlan, state]) => { setPlan(runPlan); setLiveState(state); }).catch((e) => setError(describeError(e)));
    } else if (msg.type === "event_appended" && msg.assembly_id === liveRun) {
      void getState(msg.assembly_id).then(setLiveState).catch((e) => setError(describeError(e)));
    }
  });

  // replay=play loops the planned build over PLAY_SECONDS.
  useEffect(() => {
    if (p.replay !== "play" || p.still) return;
    const started = performance.now();
    const id = window.setInterval(() => setPlayT(((performance.now() - started) / 1000 / PLAY_SECONDS) % 1), 100);
    return () => window.clearInterval(id);
  }, [p.replay, p.still]);

  const state = useMemo<BuildState | null>(() => {
    if (!plan) return null;
    if (p.replay !== null) return replayState(plan, p.replay === "play" ? (p.still ? 1 : playT) : p.replay);
    if (p.built !== null) return stateForBuilt(plan, p.built);
    return liveState;
  }, [plan, p.replay, p.built, p.still, playT, liveState]);

  const visuals = useMemo(
    () => (plan && state ? resolveVisuals(plan, state, { selected: p.selected, highlighted: p.highlight, replay: p.replay !== null }) : null),
    [plan, state, p.selected, p.highlight, p.replay],
  );
  const background = useMemo<Background>(
    () => (p.bg === "image" && p.bgSrc ? { kind: "image", src: p.bgSrc } : p.bg === "webcam" ? { kind: "webcam" } : { kind: "none" }),
    [p.bg, p.bgSrc],
  );
  const headers = useMemo(() => authHeaders(), []);
  const onReady = useCallback(() => { (window as unknown as { __previewReady?: boolean }).__previewReady = true; }, []);

  if (error) return <main className="preview-page"><p className="preview-error">{error}</p></main>;
  if (!plan || !state || !visuals) return <main className="preview-page"><p className="preview-loading">Loading…</p></main>;
  const step = plan.steps.find((s) => s.step_id === state.current_step_id);

  return (
    <main className="preview-page">
      <HologramView
        plan={plan} visuals={visuals} compare={compare} view={p.view} fov={p.fov} background={background} still={p.still}
        assetUrl={planAssetUrl} authHeaders={headers} onReady={onReady}
      />
      {p.hud && (
        <aside className="preview-hud">
          <p className="preview-hud-title">{plan.name}{live ? " · live" : p.replay !== null ? " · replay" : ""}</p>
          <p className="preview-hud-progress">{state.progress.built} of {state.progress.total} built</p>
          <div className="preview-hud-bar"><div style={{ width: `${state.progress.pct}%` }} /></div>
          <p className="preview-hud-step">{step ? `Step ${step.index} · ${step.title}` : state.progress.built === state.progress.total ? "Complete" : "No current step"}</p>
          {/* Replays show order, not time: step minutes may be placeholders that only space the animation. */}
          {p.replay === null && state.progress.minutes_left > 0 && <p className="preview-hud-muted">about {Math.round(state.progress.minutes_left)} min left</p>}
          {compare && <p className="preview-hud-muted">White outline: {compare.name}</p>}
          <ul className="preview-legend">
            {LEGEND.map(([base, label]) => {
              const s = HOLOGRAM_PALETTE.bases[base];
              return (
                <li key={base}>
                  <i style={{ borderColor: s.edge, background: s.fillAlpha > 0 ? s.fill : "transparent", opacity: Math.max(s.edgeAlpha, 0.5) }} />
                  {label}
                </li>
              );
            })}
          </ul>
        </aside>
      )}
    </main>
  );
}

export default PreviewPage;
