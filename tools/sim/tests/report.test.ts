import { describe, expect, it } from "vitest";
import { buildHtml, buildMarkdown } from "../report.js";
import type { ReportInput } from "../types.js";

const base: ReportInput = {
  current: { sha: "abc1234def", dirty: false, created_at: "2026-09-19T12:00:00Z", ci: true },
  baseline: { sha: "9f8e7d6c5b", dirty: false, created_at: "2026-09-19T11:00:00Z", ci: true },
  screens: [
    { scene: "desk-demo-start", note: "demo start", status: "changed", diffPct: 3.2 },
    { scene: "desk-full", note: "desk", status: "same", diffPct: 0 },
  ],
  plans: [{ file: "data/demo/desk.plan.json", diff: { from: { plan_id: "plan_desk_demo", revision: 1 }, to: { plan_id: "plan_desk_demo", revision: 2 },
    changes: [{ part_id: "part_tabletop", name: "Tabletop", change: "changed", moved_mm: 10, resized_mm: 0, fields: [] }], unchanged: 8 } }],
  scenario: { current: { ok: true, steps: [{ name: "mark built", ok: true, ms: 40 }] }, baseline: { ok: true, steps: [{ name: "mark built", ok: true, ms: 35 }] } },
  extraction: { diff: null, skipped: "no OPENAI_API_KEY" },
};

describe("buildMarkdown", () => {
  it("summarises pictures, plans, behaviour and extraction against the baseline", () => {
    const md = buildMarkdown(base);
    expect(md).toContain("abc1234 vs 9f8e7d6");
    expect(md).toContain("1 of 2 scenes changed");
    expect(md).toContain("| desk-demo-start | 3.2% of pixels |");
    expect(md).toContain("Tabletop moved 10 mm");
    expect(md).toContain("1/1 steps pass");
    expect(md).toContain("Blueprint reading: skipped (no OPENAI_API_KEY)");
  });
  it("says so when there is nothing to compare against", () => {
    expect(buildMarkdown({ ...base, baseline: null })).toContain("No earlier run to compare with");
  });
  it("puts newly failing steps first", () => {
    const md = buildMarkdown({ ...base, scenario: { current: { ok: false, steps: [{ name: "copilot answer", ok: false, ms: 9000, detail: "504" }] }, baseline: base.scenario.baseline } });
    expect(md.indexOf("FAILING: copilot answer")).toBeGreaterThan(-1);
    expect(md.indexOf("FAILING")).toBeLessThan(md.indexOf("Pictures"));
  });
  it("scores a blueprint reading against the known-good plan", () => {
    const md = buildMarkdown({ ...base, extraction: { skipped: null, diff: { from: { plan_id: "plan_desk_demo", revision: 1 }, to: { plan_id: "plan_x", revision: 1 },
      changes: [
        { part_id: "part_x_tray", name: "Cable tray", change: "changed", moved_mm: 23, resized_mm: 4, fields: [] },
        { part_id: "part_power_cable", name: "Power cable", change: "removed", moved_mm: null, resized_mm: null, fields: [] },
      ], unchanged: 7 } } });
    expect(md).toContain("Blueprint reading: 7/9 parts within 5 mm");
    expect(md).toContain("Cable tray 23 mm off");
    expect(md).toContain("missing: Power cable");
  });
  it("compares the blueprint reading with the previous run's", () => {
    const diff = (unchanged: number) => ({ from: { plan_id: "plan_desk_demo", revision: 1 }, to: { plan_id: "plan_x", revision: 1 },
      changes: [{ part_id: "part_x_tray", name: "Cable tray", change: "changed" as const, moved_mm: 23, resized_mm: 4, fields: [] }], unchanged });
    const md = buildMarkdown({ ...base, extraction: { skipped: null, diff: diff(8), baseline: diff(5) } });
    expect(md).toContain("Blueprint reading: 8/9 parts within 5 mm (was 5/6)");
  });
});

describe("buildHtml", () => {
  it("shows before, after and difference images for changed scenes", () => {
    const html = buildHtml(base);
    expect(html).toContain('src="baseline/screens/desk-demo-start.png"');
    expect(html).toContain('src="current/screens/desk-demo-start.png"');
    expect(html).toContain('src="diff/desk-demo-start.png"');
  });
});
