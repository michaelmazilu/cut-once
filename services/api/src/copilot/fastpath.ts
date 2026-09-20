import type { BuildEvent, BuildState, CopilotAction, Part, PartState, Plan } from "@cutonce/schemas";
import { isBuildPlan } from "../build/plan.js";

export interface FastPathInput {
  plan: Plan; state: BuildState; selectedPartId: string | null; recentEvents: BuildEvent[];
  /** The headset's mode. "build" from the first scan to the end of the walkthrough. */
  mode?: "upload" | "overlay" | "build";
  /**
   * In build mode: is the run the server is holding the design that is actually on show? While the builder is
   * scanning and picking, build mode hides the hologram, and the run underneath is whatever was built last. Without
   * this, "done" on a fresh session answered "Done. Next: stand the tall can C upright" and marked a part of a run
   * nobody could see.
   */
  buildShowing?: boolean;
}
/**
 * `action: null` is a spoken reply with nothing to apply; `note` is written on the event (blueprint §550 for undo).
 * `wish`, on a scan: what the builder asked for (a string), a plain "what can I build?" (null: forget the last wish),
 * or absent (another look: the wish stays). The pipeline hands it to build mode with the scan.
 */
export interface FastPath {
  action: CopilotAction | null; answer_text: string; highlight_parts: string[]; note?: string; wish?: string | null;
  /** With a wish: it changes the designs on show ("make me something crazier"), rather than asking for a new thing. */
  change?: boolean;
}

/**
 * Lower case, no punctuation, single spaces. "Mark the left rear leg, built." → "mark the left rear leg built".
 * An apostrophe joins its word ("It's done." → "its done"): the transcriber writes them, and the commands below are spelt without.
 */
export const normalise = (s: string) => s.toLowerCase().replace(/['’]/g, "").replace(/[^a-z0-9 ]+/g, " ").replace(/\s+/g, " ").trim();

/** A question never changes the build, whatever the model proposes. "Is the leg in?" asks; "the leg is in" tells. */
export const isQuestion = (s: string) =>
  /\?\s*$/.test(s.trim()) || /^(is|are|was|were|am|do|does|did|can|could|should|would|will|have|has|what|where|which|how|why|when|who)\b/i.test(s.trim());

const STOP = new Set(["the", "a", "an", "this", "that", "it", "my", "is", "as", "please", "now"]);
const words = (s: string) => normalise(s).split(" ").filter((w) => w && !STOP.has(w));

/**
 * Finds the one part a phrase names, by name or alias. Scores by how many of the part's own words
 * the phrase contains, so "left rear leg" beats "leg". Returns null when nothing matches or when
 * two parts tie: a wrong guess writes a wrong event, and falling through to the model is cheap.
 */
export function resolvePart(phrase: string, parts: Part[]): Part | null {
  const said = new Set(words(phrase));
  if (said.size === 0) return null;
  let best: { part: Part; score: number } | null = null;
  let tied = false;
  for (const part of parts) {
    let score = 0;
    for (const label of [part.name, ...part.aliases]) {
      const own = words(label);
      if (own.length === 0 || !own.every((w) => said.has(w))) continue;
      score = Math.max(score, own.length);
    }
    if (score === 0) continue;
    if (!best || score > best.score) { best = { part, score }; tied = false; }
    else if (score === best.score && part.part_id !== best.part.part_id) tied = true;
  }
  return best && !tied ? best.part : null;
}

/** Said when a step command arrives with no build on show: the builder is still choosing, or has not scanned yet. */
const NOTHING_ON_SHOW: FastPath = {
  action: null, highlight_parts: [],
  answer_text: "Nothing is being built yet. Ask me what you can build, then pick a design and I'll walk you through it.",
};

const PLAIN_ASK = /^(what can (i|we) (build|make)( with (this|these|that|all this|all of this|this stuff))?|what could (i|we) (build|make)( with (this|these|that))?|help me build something|build something|make something)$/;
const ASKING = "(?:hey kit )?(?:kit )?(?:(?:can|could|would|will) you |please )?";
/**
 * "Build me a birdhouse", "let's make a robot", "can we build a tower with these": the thing asked for, as said.
 * Outside build mode "make" counts only with "me": "make a list", "make a note", "can I make a change" are ordinary
 * requests about E7 or the desk, and must reach the router and the answer model as before.
 */
const wishPhrases = (verb: string) => [
  new RegExp(`^${ASKING}(?:help me )?(?:build|make) me (an? .+|something(?: .+)?)$`),
  new RegExp(`^(?:hey kit )?(?:i want to|i wanna|i would like to|id like to|lets|let us) ${verb} (an? .+|something(?: .+)?)$`),
  new RegExp(`^(?:hey kit )?(?:can|could) (?:i|we) ${verb} (an? .+?)(?: with (?:this|these|that|all this|this stuff))?$`),
  new RegExp(`^${ASKING}(?:help me )?${verb} (an? .+)$`),
];
const WISH_PHRASES = { build: wishPhrases("(?:build|make)"), elsewhere: wishPhrases("build") };
/** "Something crazier", "something else": a change to the designs on show, so those are not offered again. */
const CHANGE = /^something (else|different|new|other|more|less|crazier|wilder|weirder|cooler|bigger|smaller|taller|shorter|better|fancier|simpler|harder|easier|sillier|funnier|stranger|stronger|sturdier|longer|wider|higher|lower|prettier|nicer)\b/;
/**
 * The wish in a sentence, or null. Kept to short, plain asks: a long one, and "a smaller one" / "another one" (a
 * change to designs already on show, not a new thing), are left to the model, which knows what is on show.
 */
function wishIn(text: string, mode: FastPathInput["mode"]): string | null {
  for (const re of WISH_PHRASES[mode === "build" ? "build" : "elsewhere"]) {
    const wish = re.exec(text)?.[1]?.trim();
    if (!wish) continue;
    return wish.split(" ").length <= 10 && !/\bone$/.test(wish) ? wish : null;
  }
  return null;
}

const STATE_WORDS: Record<string, PartState> = { built: "built", done: "built", in: "built", wrong: "wrong", missing: "missing", out: "missing" };

const markState = (partId: string, newState: PartState): CopilotAction =>
  ({ type: "mark_state", part_ids: [partId], new_state: newState, source: "voice" });

/**
 * Section 10's fast path: a spoken command skips the model entirely and answers in under 1.5 s.
 * Everything here is decided from the plan and the event log, never from a guess — anything
 * uncertain returns null and takes the full pipeline instead.
 */
export function matchFastPath(transcript: string, input: FastPathInput): FastPath | null {
  const text = normalise(transcript);
  const { plan, state, selectedPartId, recentEvents, mode } = input;
  const nameOf = (id: string) => plan.parts.find((p) => p.part_id === id)?.name ?? id;

  // "What can I build?": the rehearsed lines never depend on a model. The headset scans and uploads. A plain ask
  // forgets the last wish; "build me a birdhouse" carries its own; another look ("scan again") keeps the one there is.
  // "Look again" is a rescan only in build mode: anywhere else it asks the copilot to look at the part again.
  const wish = wishIn(text, mode);
  // "Build me something" is a plain ask too.
  if (PLAIN_ASK.test(text) || wish === "something") return { action: { type: "start_scan" }, answer_text: "Let me see what you've got.", highlight_parts: [], wish: null };
  if (wish) return { action: { type: "start_scan" }, answer_text: `Let me see how to make ${wish} from what's here.`, highlight_parts: [], wish, change: CHANGE.test(wish) };
  if (/^scan (this|that|again|the table)$/.test(text) || (mode === "build" && text === "look again")) {
    return { action: { type: "start_scan" }, answer_text: "Let me see what you've got.", highlight_parts: [] };
  }

  // "next" / "back": pure headset navigation, no event. Left alone when nothing is on show — it writes nothing and
  // moving the step the HUD reads is harmless; only the commands that CHANGE the build are held back below.
  if (/^(next|next step|go next|carry on)$/.test(text)) return { action: { type: "step_nav", direction: "next" }, answer_text: "Next step.", highlight_parts: [] };
  if (/^(back|go back|previous|previous step|last step)$/.test(text)) return { action: { type: "step_nav", direction: "back" }, answer_text: "Going back a step.", highlight_parts: [] };

  // "undo": reverse the newest change a person made (voice or manual) that is not itself an undo and has not
  // been undone yet, so saying it twice steps back twice instead of redoing. Seeded demo state is never
  // undone. Expressed as a normal mark_state noted "undo of evt_…" (blueprint §550): the log stays append-only.
  if (/^(undo|undo that|undo it|take that back)$/.test(text)) {
    if (mode === "build" && input.buildShowing === false) return NOTHING_ON_SHOW;
    const undone = new Set(recentEvents.map((e) => /^undo of (evt_\w+)/.exec(e.note ?? "")?.[1]));
    const last = [...recentEvents].reverse().find((e) => e.kind === "part_state" && e.part_id && e.previous_state && e.new_state
      && (e.source === "voice" || e.source === "manual") && !e.note?.startsWith("undo of ") && !undone.has(e.event_id));
    if (!last?.part_id || !last.previous_state) return { action: null, answer_text: "There's nothing to undo.", highlight_parts: [] };
    return {
      action: markState(last.part_id, last.previous_state),
      note: `undo of ${last.event_id}`,
      answer_text: `Undone. The ${nameOf(last.part_id)} is back to ${last.previous_state}.`,
      highlight_parts: [last.part_id],
    };
  }

  // "done" / "mark it built": the part you are pointing at.
  if (/^(done|its done|thats done|mark (it|this) (built|done)|built)$/.test(text)) {
    // Nothing selected: let the model ask which part. Except in a build-mode run, where you are holding the piece, not
    // pointing: there "done" is the whole step. Build mode is also on while scanning and picking, when the run is still
    // the old one, so the plan decides, not the mode alone.
    if (!selectedPartId) {
      if (mode !== "build" || !isBuildPlan(plan)) return null;
      if (input.buildShowing === false) return NOTHING_ON_SHOW;
      return stepDone(plan, state);
    }
    // The headset can point at a part from a plan revision the server no longer runs: say so, do not fail the turn.
    if (!plan.parts.some((p) => p.part_id === selectedPartId)) {
      return { action: null, answer_text: "That part isn't in this plan. Reload the plan on the headset and try again.", highlight_parts: [] };
    }
    if (state.parts[selectedPartId]?.state === "built") return null; // no_op; the full pipeline explains why
    return { action: markState(selectedPartId, "built"), answer_text: `Marked the ${nameOf(selectedPartId)} built.`, highlight_parts: [selectedPartId] };
  }

  // "mark <part> built | done | wrong".
  const marked = /^mark (?:the )?(.+?) (?:as )?(built|done|wrong|missing)$/.exec(text);
  if (marked) {
    const newState = STATE_WORDS[marked[2]!]!;
    const part = resolvePart(marked[1]!, plan.parts);
    if (!part) return null; // ambiguous or unknown: the model can ask
    if (state.parts[part.part_id]?.state === newState) return null;
    return { action: markState(part.part_id, newState), answer_text: `Marked the ${part.name} ${newState}.`, highlight_parts: [part.part_id] };
  }

  return null;
}

/** Build mode's "done": every part of the current step that is not built yet, then the next step, read out. */
function stepDone(plan: Plan, state: BuildState): FastPath | null {
  const step = plan.steps.find((s) => s.step_id === state.current_step_id);
  const todo = step?.part_ids.filter((id) => state.parts[id]?.state !== "built") ?? [];
  if (!step || todo.length === 0) return null;
  const next = plan.steps.find((s) => s.index === step.index + 1);
  return {
    action: { type: "mark_state", part_ids: todo, new_state: "built", source: "voice" },
    answer_text: next ? `Done. Next: ${next.instruction}` : "Done. That's the whole build!",
    highlight_parts: next?.part_ids ?? [],
  };
}
