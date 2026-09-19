import type { BuildEvent, Plan } from "@cutonce/schemas";
import { ulid } from "ulid";

/** The plan's own build order as synthetic events: one `missing → built` per part. Used for replays. */
export function plannedEvents(plan: Plan, assemblyId: string, start: string, secondsPerMinute = 60): BuildEvent[] {
  let t = Date.parse(start);
  let version = 0;
  const events: BuildEvent[] = [];
  for (const step of [...plan.steps].sort((a, b) => a.index - b.index)) {
    t += step.est_minutes * secondsPerMinute * 1000;
    for (const partId of step.part_ids) {
      version += 1;
      const ts = new Date(t).toISOString();
      events.push({
        event_id: `evt_${ulid(t)}`, assembly_id: assemblyId, version, timestamp: ts, client_timestamp: ts,
        kind: "part_state", part_id: partId, previous_state: "missing", new_state: "built",
        source: "system", confidence: 1, actor: "planner", step_id: step.step_id, note: "planned",
      });
    }
  }
  return events;
}
