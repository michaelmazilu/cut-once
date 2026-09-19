import OpenAI from "openai";
import type { CopilotResponse, RetrievedChunk } from "@cutonce/schemas";
import type { Ctx } from "../app.js";
import { callKnowledgeTool, knowledgeToolSpecs } from "../search/tools.js";
import type { ToolName } from "../search/fallbacks.js";
import { CopilotAnswer, copilotAnswerSchema, keepKnown } from "./schema.js";
import type { Gathered } from "./context.js";
import type { CopilotModels } from "./models.js";
import { SYSTEM, userText, type PromptInput } from "./prompt.js";

/** `knowledgeToolSpecs` is in the Responses-API shape; chat.completions nests the same fields under `function`. */
const chatTools = knowledgeToolSpecs.map((t) => ({ type: "function" as const, function: { name: t.name, description: t.description, parameters: t.parameters } }));

const imagePart = (data: Buffer, mime: string) => ({ type: "image_url" as const, image_url: { url: `data:${mime};base64,${data.toString("base64")}` } });

/** The system prompt promises two images; this tells the model when a turn has none. */
const NO_FRAME = "\n\nNO CAMERA FRAME this turn: there are no images. Answer from the tables and documents, and do not describe what you see.";

export interface AskInput extends PromptInput { frames: { annotated: Buffer | null; raw: Buffer | null } }

/**
 * One model call with both frames, plus at most one tool round if the model asks for more.
 * The response schema is built per question (copilot/schema.ts): the part and chunk ids are enums of
 * what actually exists, so strict structured output makes an invented id impossible rather than
 * merely unlikely. Section 10 budgets 1.5–3.0 s here; the caller passes the 6 s timeout.
 */
export async function ask(ctx: Ctx, m: CopilotModels, input: AskInput): Promise<{ draft: CopilotAnswer; toolCalls: string[] }> {
  if (!ctx.cfg.openaiKey) throw new Error("OPENAI_API_KEY is not set");
  const client = new OpenAI({ apiKey: ctx.cfg.openaiKey, timeout: m.budgets.llm, maxRetries: 0 });
  const content = [
    { type: "text" as const, text: userText(input) + (input.frames.raw ? "" : NO_FRAME) },
    ...(input.frames.annotated ? [imagePart(input.frames.annotated, "image/jpeg")] : []),
    ...(input.frames.raw ? [imagePart(input.frames.raw, "image/jpeg")] : []),
  ];
  const messages: OpenAI.Chat.ChatCompletionMessageParam[] = [{ role: "system", content: SYSTEM }, { role: "user", content }];
  const schema = copilotAnswerSchema(input.gathered.plan.parts.map((p) => p.part_id), input.chunks.map((c) => c.chunk_id));
  const response_format = { type: "json_schema" as const, json_schema: { name: "copilot_answer", strict: true, schema } };

  // reasoning_effort "none": gpt-5.6-luna is a reasoning model, and chat completions refuse function tools while it reasons
  // (a 400 the headset sees as 503 copilot_unavailable). It also keeps the turn inside its budget: this is a grounded lookup, not a puzzle.
  let res = await client.chat.completions.create({ model: m.chat, messages, tools: chatTools, response_format, reasoning_effort: "none" });
  const toolCalls: string[] = [];
  const calls = (res.choices[0]?.message?.tool_calls ?? []).flatMap((c) => (c.type === "function" ? [c] : []));
  if (calls.length > 0) {
    messages.push(res.choices[0]!.message);
    // All at once: each call may wait up to the tool budget for Agent Builder, and one after another three of
    // them would spend 6 s of the 9 s cap before the second model call even starts.
    const results = await Promise.all(calls.map((call) => {
      let args: Record<string, unknown> = {};
      try { args = JSON.parse(call.function.arguments || "{}") as Record<string, unknown>; } catch { /* a malformed argument string still gets an answer, just an empty one */ }
      return callKnowledgeTool(ctx, call.function.name as ToolName, args, { timeoutMs: m.budgets.tool });
    }));
    calls.forEach((call, i) => {
      const result = results[i]!;
      toolCalls.push(call.function.name);
      messages.push({ role: "tool", tool_call_id: call.id, content: JSON.stringify(result.ok ? result.data : { error: result.error }) });
    });
    // Second pass without tools: the model has what it asked for, now it must answer.
    res = await client.chat.completions.create({ model: m.chat, messages, response_format, reasoning_effort: "none" });
  }

  const text = res.choices[0]?.message?.content;
  if (!text) throw new Error(`the model returned no content (${res.choices[0]?.finish_reason ?? "unknown reason"})`);
  return { draft: CopilotAnswer.parse(JSON.parse(text)), toolCalls };
}

const clamp01 = (n: number) => (Number.isFinite(n) ? Math.min(1, Math.max(0, n)) : 0.5);

/**
 * Section 10, t≈3.5: the belt to the schema's braces. Drop the "none" sentinel and anything not in
 * the plan or the retrieved set, and turn cited chunk ids into the source card's drawing refs.
 */
export function ground(draft: CopilotAnswer, g: Gathered, chunks: RetrievedChunk[]): Pick<CopilotResponse, "answer_text" | "highlight_parts" | "highlight_style" | "drawing_refs" | "action" | "confidence" | "needs_clarification"> {
  const planIds = g.plan.parts.map((p) => p.part_id);
  const byChunk = new Map(chunks.map((c) => [c.chunk_id, c]));
  const highlight_parts = [...new Set(keepKnown(draft.highlight_parts, planIds))];
  const drawing_refs = [...new Set(keepKnown(draft.chunk_ids, [...byChunk.keys()]))].map((id) => {
    const c = byChunk.get(id)!;
    return { document_id: c.document_id, ...(c.sheet_id ? { sheet_id: c.sheet_id } : {}), page: c.page, chunk_id: c.chunk_id, title: c.title };
  });
  const actionParts = draft.action ? keepKnown(draft.action.part_ids, planIds) : [];
  const dropped = draft.highlight_parts.length - highlight_parts.length;
  return {
    answer_text: draft.answer_text.trim(),
    highlight_parts,
    highlight_style: draft.highlight_style,
    drawing_refs,
    // The model's mark_state suggestion becomes a real voice action only if it names real parts.
    action: draft.action && actionParts.length > 0 ? { type: "mark_state", part_ids: actionParts, new_state: draft.action.new_state, source: "voice" } : null,
    confidence: dropped > 0 ? Math.min(clamp01(draft.confidence), 0.5) : clamp01(draft.confidence),
    needs_clarification: draft.needs_clarification,
  };
}
