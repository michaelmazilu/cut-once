import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { pillarProblems } from "../pillars.js";

describe("Quest three-pillar regression gate", () => {
  it("passes the checked-in implementation", () => {
    expect(pillarProblems()).toEqual([]);
  });

  it("fails if passive recognition is gated on measured geometry again", () => {
    const problems = pillarProblems(undefined, path => {
      const source = readFileSync(path, "utf8");
      return path.endsWith("RoomScanner.cs")
        ? source.replace("Visualizer.Show(o, o == focus, Drawable(o))", "Visualizer.Show(o, o == focus)")
        : source;
    });
    expect(problems.map(p => p.id)).toContain("P2-VISION-FALLBACK");
  });

  it("fails if Kit stops reading and highlighting the active build instruction on request", () => {
    const problems = pillarProblems(undefined, path => {
      const source = readFileSync(path, "utf8");
      return path.endsWith("fastpath.ts") ? source.replace("highlight_parts: step.part_ids", "highlight_parts: []") : source;
    });
    expect(problems.map(p => p.id)).toContain("P1-BUILD-INSTRUCTIONS");
  });

  it("fails if accepted scans stop recovering missed blueprint messages", () => {
    const problems = pillarProblems(undefined, path => {
      const source = readFileSync(path, "utf8");
      return path.endsWith("BuildMode.cs") ? source.replace("Run(RecoverSession(accepted.session_id))", "") : source;
    });
    expect(problems.map(p => p.id)).toContain("P3-BLUEPRINT-RECOVERY");
  });
});
