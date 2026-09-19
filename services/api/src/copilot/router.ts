import { z } from "zod";
import type { AiCall } from "../ai.js";
import type { Config } from "../config.js";
import type { CopilotModels } from "./models.js";

/**
 * Outside build mode (a desk or Engineering 7 on show), does a spoken turn want build ideas, or is it a question?
 * Build mode's own turns go to Kit (kit.ts), which sees the objects and the designs on show.
 */
export const Routed = z.object({
  flow: z.enum(["question", "build_ideas"]), confidence: z.number().min(0).max(1),
  /** What they want built, in a few words ("a birdhouse"), or null. Missing counts as null. */
  wish: z.string().nullable().default(null),
});
export type Routed = z.infer<typeof Routed>;
/** Below this the turn is answered as a question, as it was before build mode existed. */
export const ROUTE_MIN = 0.7;
export interface RouteInput { transcript: string; mode: string }

export const ROUTER_SYSTEM = [
  "You route one spoken sentence for Kit, the Kitbash co-pilot. A build is on show (a desk, or the Engineering 7 building) and the person is not choosing a design. Pick one flow:",
  "- build_ideas: they want ideas for what to build or make from the things around them, or want the table scanned.",
  "  e.g. \"what can I build\", \"what could we make with this stuff\", \"can I build a shelf with these\", \"scan the table\", \"make me something for my phone\".",
  "- question: everything else: about the build on show, what a part is, where it goes, why, what is next.",
  "wish: for build_ideas, what they want in a few words, as they said it (\"a birdhouse\", \"something for my phone\"); null for a plain \"what can I build\" and for questions.",
  "Give your confidence from 0 to 1.",
].join("\n");

/**
 * Which flow a spoken turn wants. Null means "treat it as a question": no provider, a timeout, an error or a malformed
 * answer. The pipeline routes on OpenAI, as the rest of an E7 or desk turn runs, with its small router model
 * (OPENAI_ROUTER_MODEL) and COPILOT_ROUTE_MS.
 */
export async function routeTurn(cfg: Config, m: CopilotModels, input: RouteInput, ai: AiCall | null): Promise<Routed | null> {
  if (!ai) return null;
  const budget = m.budgets.route;
  const work = ai.call(cfg, {
    name: "route", model: ai.provider === "openai" ? m.router : ai.model, schema: Routed, system: ROUTER_SYSTEM, timeoutMs: budget,
    text: `MODE: ${input.mode}\nSAID: "${input.transcript}"`,
  }).then((r) => Routed.parse(r));
  const timeout = new Promise<null>((resolve) => { const t = setTimeout(() => resolve(null), budget); t.unref?.(); });
  try { return await Promise.race([work, timeout]); } catch { return null; }
}

export type RouteOutcome = "question" | "scan";

/** What a routed turn does: only a sure "build ideas" scans. Anything else is answered as a question, as before build mode existed. */
export const routeOutcome = (routed: Pick<Routed, "flow" | "confidence"> | null): RouteOutcome =>
  routed?.flow === "build_ideas" && routed.confidence >= ROUTE_MIN ? "scan" : "question";
