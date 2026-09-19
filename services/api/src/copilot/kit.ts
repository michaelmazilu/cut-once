import sharp from "sharp";
import { z } from "zod";
import type { Twin, Vec3 } from "@cutonce/schemas";
import type { AiCall } from "../ai.js";
import type { KitBuildContext } from "../build/session.js";
import { describeShape } from "../build/shape.js";
import type { Config } from "../config.js";
import { isQuestion, normalise } from "./fastpath.js";
import type { CopilotModels } from "./models.js";
import { transcribe } from "./stt.js";

/**
 * Kit's build-mode turn: one model call that hears what was said (the voice clip on OMNI; a transcript on OpenAI),
 * sees the headset's photo and the table's objects, and says what the builder wants. The model only classifies and
 * answers: decideKit turns its answer into an action, and code does every action.
 */
export const KitTurn = z.object({
  heard: z.string(),
  intent: z.enum(["question", "ideas", "change", "pick", "done", "next", "back", "undo", "unclear"]),
  wish: z.string().nullable(),
  pick: z.string().nullable(),
  answer: z.string(),
  objects: z.array(z.string()),
  confidence: z.number().min(0).max(1),
});
export type KitTurn = z.infer<typeof KitTurn>;

export const KIT_SYSTEM = [
  "You are Kit, the Kitbash co-pilot. Someone wearing a mixed-reality headset is building things from the real objects on the table in front of them, and talks to you while they work.",
  "You get what they said (as audio, or written under SAID), the headset's photo, and tables: the objects on the table as measured, the designs on show, and the build in progress.",
  "",
  "Work out what they want (intent):",
  "- question: they ask something. Answer it.",
  "- ideas: they want designs, perhaps for something in particular (\"build me a birdhouse\", \"what could I make for my phone?\"). wish: that thing in a few words (\"a birdhouse\"), or null for a plain \"what can I build?\".",
  "- change: they want different or changed designs (\"something crazier\", \"make it taller\", \"use the other box\"). wish: the change in a few words.",
  "- pick: they choose one of the DESIGNS ON SHOW, by name or position (\"the left one\", \"the birdhouse\"). pick: its id.",
  "- done: they have finished the current step (\"done\", \"I've put the can in\").",
  "- next, back: they want the next or the previous step.",
  "- undo: they want the last change undone.",
  "- unclear: you cannot tell. Ask them back in answer.",
  "",
  "Rules:",
  "1. heard: what they said, word for word as best you can.",
  "2. answer is spoken aloud: plain words, at most two short sentences, no lists and no ids.",
  "3. Name objects by what and where they are (\"the can on your left\"). Never invent a size, a material or a count that is not in OBJECTS.",
  "4. objects: the ids (o1, o2…) of the objects your answer is about, in the order you mention them; [] if none.",
  "5. A question is never a command: \"is it done?\" is a question.",
  "6. For ideas, change and pick, answer with one short sentence saying what you will do now, not what you will find.",
  "7. confidence: how sure you are of the intent, from 0 to 1.",
].join("\n");

const cm = (m: number) => Math.round(m * 100);
const unit = (v: Vec3): Vec3 => { const n = Math.hypot(v[0], v[1], v[2]) || 1; return [v[0] / n, v[1] / n, v[2] / n]; };

/**
 * Where an object stood at the last scan, seen from the headset then: "30 cm to your left, 1.2 m away". Right-handed,
 * +Y up: the camera's right is cross(forward, up) = (-f.z, 0, f.x), level.
 */
export function whereFrom(camera: { position: Vec3; forward: Vec3 }, at: Vec3): string {
  const f = unit([camera.forward[0], 0, camera.forward[2]]), right: Vec3 = [-f[2], 0, f[0]];
  const d: Vec3 = [at[0] - camera.position[0], at[1] - camera.position[1], at[2] - camera.position[2]];
  const side = d[0] * right[0] + d[2] * right[2];
  const across = Math.abs(side) < 0.1 ? "in front of you" : `${cm(Math.abs(side))} cm to your ${side < 0 ? "left" : "right"}`;
  return `${across}, ${Math.hypot(d[0], d[1], d[2]).toFixed(1)} m away`;
}

export interface KitTurnHistory { transcript: string; answer_text: string }
/** The design being built, and its step: from the current run's plan when build mode started it. */
export interface KitBuilding { title: string; step: { index: number; instruction: string } | null; of: number; next: string | null }

/** Everything Kit is told besides the voice and the photo, as short tables. */
export function kitContextText(ctx: KitBuildContext, building: KitBuilding | null, said: string | null, turns: KitTurnHistory[]): string {
  const surface = (t: Twin) => ctx.surfaces.find((s) => s.surface_id === t.sits_on)?.kind ?? "surface";
  const objects = ctx.twins.filter((t) => t.name !== "unknown").map((t) => `  ${t.twin_id} ${t.label}: ${describeShape(t.shape)}; ${t.material}; ${t.load_bearing ? "holds weight" : "does not hold weight"}`
    + `; on the ${surface(t)}${ctx.camera ? `, ${whereFrom(ctx.camera, t.position)}` : ""}`);
  const unnamed = ctx.twins.filter((t) => t.name === "unknown").length;
  const ideas = ctx.ideas.map((i, k) => `  ${k + 1}. ${i.idea_id} "${i.title}": uses ${i.uses.join(", ")}; ${i.steps} step${i.steps === 1 ? "" : "s"}. Why: ${i.why}`);
  const build = !building ? "nothing yet"
    : building.step ? `"${building.title}", step ${building.step.index} of ${building.of}: ${building.step.instruction}${building.next ? ` Next: ${building.next}` : " That is the last step."}`
    : `"${building.title}", finished`;
  return [
    `STATUS: ${ctx.status}`,
    "OBJECTS (where they stood at the last scan, seen from the headset):",
    ...(objects.length ? objects : ["  none yet"]),
    ...(unnamed ? [`  and ${unnamed} object${unnamed === 1 ? "" : "s"} not named yet`] : []),
    `TOOLS: ${ctx.tape ? "tape (a roll is on the table)" : "none"}`,
    `WISH: ${ctx.wish ? `"${ctx.wish}"` : "none"}`,
    "DESIGNS ON SHOW (left to right, as the headset shows them):",
    ...(ideas.length ? ideas : ["  none"]),
    `BUILDING: ${build}`,
    ...(turns.length ? ["EARLIER:", ...turns.map((t) => `  they said: ${t.transcript}\n  you said: ${t.answer_text}`)] : []),
    `SAID: ${said === null ? "(in the audio)" : `"${said}"`}`,
  ].join("\n");
}

/** "The left one", "the second one": a design on show by where it stands (the headset lays them out left to right). */
export function pickByPosition<T>(said: string, ideas: T[]): T | null {
  const t = ` ${normalise(said)} `, n = ideas.length;
  if (n === 0) return null;
  if (/ (first|left|leftmost) /.test(t)) return ideas[0]!;
  if (/ (middle|centre|center) /.test(t)) return n === 3 ? ideas[1]! : null;
  if (/ second /.test(t)) return ideas[1] ?? null;
  if (/ third /.test(t)) return ideas[2] ?? null;
  if (/ (right|rightmost|last) /.test(t)) return ideas[n - 1]!;
  return null;
}

export type KitDecision =
  | { kind: "say"; text: string; clarify: boolean }
  | { kind: "command"; phrase: "done" | "next" | "back" | "undo" }
  | { kind: "scan"; wish: string | null; text: string }
  | { kind: "rethink"; wish: string; text: string }
  | { kind: "start"; ideaId: string; title: string };

export interface KitAt {
  canRethink: boolean; building: boolean;
  ideas: { idea_id: string; title: string }[];
  /** Picking a design by its name in a sentence (session.ts's pickIdea). */
  byName: (said: string) => { idea_id: string; title: string } | null;
}

/** The bar for acting on what Kit understood: a state change needs more certainty than a scan or a step turn. */
export const KIT_ACT_MIN = 0.6, KIT_CHANGE_MIN = 0.8;

/**
 * What to do with a Kit turn. Deterministic: the model's intent chooses among actions code already has; anything it
 * is unsure of becomes a spoken question back, and nothing that changes the build happens on a question.
 */
export function decideKit(kit: KitTurn, at: KitAt): KitDecision {
  const say = (text: string, clarify: boolean): KitDecision => ({ kind: "say", text, clarify });
  const unsure = say(kit.answer.trim() || "Sorry, what would you like to do?", true);
  const sure = kit.confidence >= KIT_ACT_MIN;
  switch (kit.intent) {
    case "question": return say(kit.answer.trim() || "I don't have an answer for that. Try asking another way.", kit.confidence < 0.5);
    case "ideas": case "change": {
      if (!sure) return unsure;
      const wish = kit.wish?.trim() || (kit.intent === "change" ? kit.heard.trim() : "") || null;
      if (wish && at.canRethink) return { kind: "rethink", wish, text: kit.answer.trim() || `Let me see how to make ${wish} from what's here.` };
      const text = kit.answer.trim() || (kit.intent === "change" ? "Let me look again with that in mind." : wish ? `Let me see how to make ${wish} from what's here.` : "Let me see what you've got.");
      return { kind: "scan", wish, text };
    }
    case "pick": {
      if (at.building) return say("You're building one already. Say what you'd like instead, and I'll look again.", true);
      const idea = at.ideas.find((i) => i.idea_id === kit.pick) ?? at.byName(kit.heard) ?? pickByPosition(kit.heard, at.ideas);
      return idea && sure ? { kind: "start", ideaId: idea.idea_id, title: idea.title } : say(kit.answer.trim() || "Which one? Say its name, or the left, middle or right one.", true);
    }
    case "done": case "undo":
      return kit.confidence >= KIT_CHANGE_MIN && !isQuestion(kit.heard) ? { kind: "command", phrase: kit.intent } : unsure;
    case "next": case "back":
      return sure ? { kind: "command", phrase: kit.intent } : unsure;
    default:
      return unsure;
  }
}

/** The photo at 1024 px wide, as the labeller sends it: less to upload and to read, and plenty for the question. */
async function smaller(frame: Buffer): Promise<Buffer> {
  try { return await sharp(frame).resize({ width: 1024, withoutEnlargement: true }).jpeg({ quality: 80 }).toBuffer(); }
  catch { return frame; }
}

export interface KitTurnInput {
  cfg: Config; m: CopilotModels; ai: AiCall; audio: Buffer; said?: string | null; frame: Buffer | null;
  context: KitBuildContext; building: KitBuilding | null; turns: KitTurnHistory[]; timeoutMs: number;
}
export interface KitTurnResult { kit: KitTurn; sttMs: number | null; modelMs: number }

/**
 * One Kit turn on one provider. OMNI hears the voice clip itself (the Huawei scenario: speech, vision and language in
 * one call). OpenAI's path transcribes first and sends the words; what Kit heard is then the transcript.
 */
export async function runKitTurn(input: KitTurnInput): Promise<KitTurnResult> {
  const { cfg, m, ai } = input;
  const started = Date.now();
  let said: string | null = input.said ?? null, sttMs: number | null = said === null ? null : 0;
  if (said === null && ai.provider === "openai") {
    said = await transcribe(cfg, m, input.audio);
    sttMs = Date.now() - started;
    if (!said) return { kit: { heard: "", intent: "unclear", wish: null, pick: null, answer: "", objects: [], confidence: 0 }, sttMs, modelMs: 0 };
  }
  const photo = input.frame ? await smaller(input.frame) : null;
  const modelStart = Date.now();
  const kit = KitTurn.parse(await ai.call(cfg, {
    name: "kit_turn", model: ai.model, schema: KitTurn, system: KIT_SYSTEM, text: kitContextText(input.context, input.building, said, input.turns),
    timeoutMs: Math.max(1000, input.timeoutMs - (modelStart - started)), images: photo ? [{ data: photo, mime: "image/jpeg" as const }] : [],
    ...(ai.provider === "omni" && said === null ? { audio: { data: input.audio, format: "wav" as const } } : {}),
  }));
  return { kit: said === null ? kit : { ...kit, heard: said }, sttMs, modelMs: Date.now() - modelStart };
}
