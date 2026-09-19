import type { Assembly, BuildState, Plan } from "@cutonce/schemas";
import { useCommand } from "./useCommand";


interface Props {
  assembly: Assembly | null;
  plan: Plan | null;
  state: BuildState | null;
  noRun: boolean;
  onChanged: () => void;
}

export function RunPanel({ assembly, plan, state, noRun, onChanged }: Props) {
  const { send, busy, error } = useCommand(onChanged);

  const progress = state?.progress;
  const pct = progress ? Math.max(0, Math.min(100, progress.pct)) : 0;
  const step = state?.current_step_id ? plan?.steps.find((s) => s.step_id === state.current_step_id) : undefined;
  const finished = !!progress && progress.total > 0 && progress.built >= progress.total;

  return (
    <section className="card run-panel">
      <div className="run-head">
        <div className="step-title">{assembly ? plan?.name ?? assembly.plan_id : noRun ? "No run yet" : "Loading…"}</div>
      </div>

      <div className="progress" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={pct}>
        <div className="progress-fill" style={{ width: `${pct}%` }} />
      </div>
      <div className="progress-text">
        {progress ? `${progress.built} / ${progress.total} · ${progress.pct}%` : "– / –"}
      </div>

      <div className="run-step">
        <div>
          <div className="label">Current step</div>
          <div className="step-title">
            {step ? `${step.index}. ${step.title}` : state?.current_step_id ?? (finished ? "All steps done" : "–")}
          </div>
        </div>
        <div className="run-minutes">
          <div className="label">Minutes left</div>
          <div className="big">{progress ? Math.round(progress.minutes_left * 10) / 10 : "–"}</div>
        </div>
      </div>

      <div className="row new-run">
        <span className="muted">Nothing shows until Kit builds something.</span>
        <button type="button" disabled={busy} onClick={() => void send({ type: "new_run", seed: "blank" }, "clear")}>
          {busy ? "Clearing…" : "Clear the build"}
        </button>
        {error && <span className="error-text">{error}</span>}
      </div>
    </section>
  );
}
