using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CutOnce.Core
{
    /// <summary>
    /// The plan's own build order as synthetic events: one missing → built per part (twin of planned.ts).
    /// Used for playing the planned future on the timeline. These never leave the headset.
    /// </summary>
    public static class PlannedEvents
    {
        public static List<BuildEventDto> For(PlanDto plan, string assemblyId, DateTime startUtc, int firstVersion = 1, double secondsPerMinute = 60)
        {
            var events = new List<BuildEventDto>();
            var t = startUtc;
            int version = firstVersion - 1;
            foreach (var step in plan.steps.OrderBy(s => s.index))
            {
                t = t.AddSeconds(step.est_minutes * secondsPerMinute);
                foreach (var partId in step.part_ids)
                {
                    version += 1;
                    string ts = t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                    events.Add(new BuildEventDto
                    {
                        event_id = "evt_" + Ulid.New(t), assembly_id = assemblyId, version = version, timestamp = ts, client_timestamp = ts,
                        kind = "part_state", part_id = partId, previous_state = "missing", new_state = "built",
                        source = "system", confidence = 1, actor = "planner", step_id = step.step_id, note = "planned",
                    });
                }
            }
            return events;
        }
    }
}
