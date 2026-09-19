import { describe, expect, it } from "vitest";
import { parsePreviewParams } from "./previewParams";

describe("parsePreviewParams", () => {
  it("defaults to the live desk from the operator's eye height", () => {
    expect(parsePreviewParams("")).toEqual({
      planId: "plan_desk_demo", revision: null, built: null, replay: null, highlight: [], selected: null,
      compare: null, view: "operator", fov: 90, bg: "none", bgSrc: null, still: false, hud: true,
    });
  });
  it("reads an explicit build state", () => {
    expect(parsePreviewParams("?built=").built).toEqual([]);
    expect(parsePreviewParams("?built=all").built).toBe("all");
    expect(parsePreviewParams("?built=part_tabletop,part_left_front_leg").built).toEqual(["part_tabletop", "part_left_front_leg"]);
  });
  it("reads replay, highlight, compare and the camera", () => {
    const p = parsePreviewParams("?plan=plan_build_a&replay=0.5&highlight=a,b&compare=plan_desk_demo&view=orbit&fov=200&still=1&hud=0");
    expect(p).toMatchObject({ planId: "plan_build_a", replay: 0.5, highlight: ["a", "b"], compare: "plan_desk_demo", view: "orbit", fov: 120, still: true, hud: false });
    expect(parsePreviewParams("?replay=play").replay).toBe("play");
    expect(parsePreviewParams("?replay=7").replay).toBe(1);
    expect(parsePreviewParams("?view=sideways").view).toBe("operator");
  });
});
