import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { CURRENT, ROOT } from "./paths.js";

export interface Scene { name: string; url: string; note: string }

/** The fixed set of pictures taken on every run. Add scenes at the end; never rename one (names are the comparison key). */
export function scenes(): Scene[] {
  const demoStart = (JSON.parse(readFileSync(join(ROOT, "data/demo/seeds/demo_start.json"), "utf8")) as { built: string[] }).built.join(",");
  const p = (q: string) => `/preview?${q}&still=1`;
  const list: Scene[] = [
    { name: "desk-empty", url: p("plan=plan_desk_demo&built="), note: "Nothing built: every ghost" },
    { name: "desk-demo-start", url: p(`plan=plan_desk_demo&built=${demoStart}`), note: "The state the live demo starts in" },
    { name: "desk-all-built", url: p("plan=plan_desk_demo&built=all"), note: "Finished: brackets only" },
    { name: "desk-top", url: p(`plan=plan_desk_demo&built=${demoStart}&view=top`), note: "From above: layout and alignment" },
    { name: "desk-cable-answer", url: p(`plan=plan_desk_demo&built=${demoStart}&highlight=part_power_cable,part_cable_tray`), note: "What 'where does this cable go?' lights up" },
    { name: "sim-idle", url: "/sim?still=1&frame=render", note: "The pretend headset at rest, on the live run" },
  ];
  if (existsSync(join(CURRENT, "extracted.plan.json"))) {
    list.push({ name: "extracted-vs-known", url: p("plan=plan_desk_extracted&built=&compare=plan_desk_demo"), note: "AI-read desk (ghost) over the known-good desk (white)" });
  }
  return list;
}
