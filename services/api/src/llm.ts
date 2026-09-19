import OpenAI from "openai";
import type { ZodTypeAny, z } from "zod";
import { zodToJsonSchema } from "zod-to-json-schema";
import type { Config } from "./config.js";

const UNSUPPORTED = ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minItems", "maxItems", "minLength", "maxLength", "pattern", "format", "default", "$schema"];

/**
 * A Zod schema as JSON Schema that strict structured-output mode accepts: every property required,
 * additionalProperties false on every object, and no validation keywords the mode may reject.
 * Pass a schema built in "strict" mode with nullable (not optional) fields.
 */
export function toOpenAiSchema(schema: ZodTypeAny): Record<string, unknown> {
  const root = zodToJsonSchema(schema, { $refStrategy: "none", target: "jsonSchema7" }) as Record<string, unknown>;
  const walk = (node: unknown): unknown => {
    if (Array.isArray(node)) return node.map(walk);
    if (!node || typeof node !== "object") return node;
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(node)) if (!UNSUPPORTED.includes(k)) out[k] = walk(v);
    if (out.type === "object" && out.properties) { out.additionalProperties = false; out.required = Object.keys(out.properties as object); }
    return out;
  };
  return walk(root) as Record<string, unknown>;
}

export interface JsonCall<S extends ZodTypeAny> {
  name: string; system: string; text: string; schema: S; strictSchema?: ZodTypeAny;
  images?: { data: Buffer; mime: "image/png" | "image/jpeg" }[]; timeoutMs?: number;
  /** A ready JSON Schema (for example one built per request with enums); sent as-is instead of converting the Zod schema. */
  jsonSchema?: Record<string, unknown>;
  /** Overrides OPENAI_MODEL for this call, for example the copilot's OPENAI_COPILOT_MODEL. */
  model?: string;
  /** A voice clip. Only the OMNI helper (omni.ts) sends one; OpenAI's path transcribes first. */
  audio?: { data: Buffer; format: "wav" | "mp3" };
  /** OpenAI only: how long a reasoning model may think. Unset is the model's default; "none" is for a job with a sub-second budget. */
  reasoningEffort?: "none" | "low" | "medium" | "high";
}

/** The schema actually sent to the model. */
export const schemaFor = (call: JsonCall<ZodTypeAny>) => call.jsonSchema ?? toOpenAiSchema(call.strictSchema ?? call.schema);

/** One model call that must return JSON matching `schema`. Throws if the key is missing, the call fails, or the JSON is wrong. */
export async function jsonCall<S extends ZodTypeAny>(cfg: Config, call: JsonCall<S>): Promise<z.infer<S>> {
  if (!cfg.openaiKey) throw new Error("OPENAI_API_KEY is not set");
  if (call.audio) throw new Error("jsonCall cannot send a voice clip: transcribe it first");
  const client = new OpenAI({ apiKey: cfg.openaiKey, timeout: call.timeoutMs ?? 120_000, maxRetries: 1 });
  const res = await client.chat.completions.create({
    model: call.model ?? cfg.openaiModel,
    messages: [
      { role: "system", content: call.system },
      { role: "user", content: [
        { type: "text", text: call.text },
        ...(call.images ?? []).map((img) => ({ type: "image_url" as const, image_url: { url: `data:${img.mime};base64,${img.data.toString("base64")}` } })),
      ] },
    ],
    response_format: { type: "json_schema", json_schema: { name: call.name, strict: true, schema: schemaFor(call) } },
    ...(call.reasoningEffort ? { reasoning_effort: call.reasoningEffort } : {}),
  });
  const content = res.choices[0]?.message?.content;
  if (!content) throw new Error(`the model returned no content (${res.choices[0]?.finish_reason ?? "unknown reason"})`);
  return call.schema.parse(JSON.parse(content));
}
