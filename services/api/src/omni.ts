import OpenAI from "openai";
import type { ZodTypeAny, z } from "zod";
import type { Config } from "./config.js";
import { schemaFor, type JsonCall } from "./llm.js";

/** When a call answered: time to the first streamed text, the whole call, and how many tries it took. */
export interface OmniTiming { firstTokenMs: number | null; totalMs: number; attempts: number }

/**
 * The JSON objects in a model's text reply, last first: the answer usually comes after any thinking or example. Each
 * is a balanced {…} that parses (braces inside strings do not count); fences, prose and anything else are skipped.
 */
export function jsonObjects(text: string): unknown[] {
  const found: unknown[] = [];
  for (let start = text.indexOf("{"); start >= 0; start = text.indexOf("{", start + 1)) {
    let depth = 0, inString = false, escaped = false;
    for (let i = start; i < text.length; i++) {
      const ch = text[i];
      if (inString) { if (escaped) escaped = false; else if (ch === "\\") escaped = true; else if (ch === "\"") inString = false; continue; }
      if (ch === "\"") inString = true;
      else if (ch === "{") depth++;
      else if (ch === "}" && --depth === 0) {
        // An object that parses is kept, and the search goes on after it; one that does not, from its next brace.
        try { found.push(JSON.parse(text.slice(start, i + 1))); start = i; } catch { /* not JSON: prose in braces */ }
        break;
      }
    }
  }
  return found.reverse();
}

const issues = (err: z.ZodError) => err.issues.slice(0, 5).map((i) => `${i.path.join(".") || "(root)"}: ${i.message}`).join("; ");

/**
 * One call to the OMNI model (Qwen3.5-Omni through yibuapi's OpenAI-compatible API) that must return JSON matching
 * `schema`. The same input as the OpenAI helper, plus an optional voice clip, so a caller can switch providers.
 *
 * Why it differs from jsonCall: Alibaba documents Qwen-Omni as streaming its replies and offers no mode that forces
 * valid JSON. So the reply is streamed, the JSON is pulled out of the text, checked here, and asked for once more
 * with the reason when it is wrong. One deadline covers both tries, and aborts a reply still streaming at the end.
 */
export async function omniJsonCall<S extends ZodTypeAny>(cfg: Config, call: JsonCall<S>, timing?: (t: OmniTiming) => void): Promise<z.infer<S>> {
  if (!cfg.omniKey || !cfg.omniBaseUrl) throw new Error("OMNI_API_KEY and OMNI_BASE_URL must both be set");
  const budget = call.timeoutMs ?? 60_000, t0 = Date.now(), deadline = t0 + budget;
  const client = new OpenAI({ apiKey: cfg.omniKey, baseURL: cfg.omniBaseUrl, timeout: budget, maxRetries: 0 });
  const system = `${call.system}\n\nReply with ONE JSON object and nothing else: no prose, no code fences. It must match this JSON Schema:\n${JSON.stringify(schemaFor(call))}`;
  const content: OpenAI.Chat.ChatCompletionContentPart[] = [
    { type: "text", text: call.text },
    ...(call.images ?? []).map((img) => ({ type: "image_url" as const, image_url: { url: `data:${img.mime};base64,${img.data.toString("base64")}` } })),
    ...(call.audio ? [{
      type: "input_audio" as const,
      // Alibaba's examples send a data URL with no media type; OpenAI's shape is bare base64. OMNI_AUDIO picks.
      input_audio: { data: cfg.omniAudio === "base64" ? call.audio.data.toString("base64") : `data:;base64,${call.audio.data.toString("base64")}`, format: call.audio.format },
    }] : []),
  ];
  const messages: OpenAI.Chat.ChatCompletionMessageParam[] = [{ role: "system", content: system }, { role: "user", content }];
  let firstToken: number | null = null, why = "out of time";
  for (let attempt = 1; attempt <= 2; attempt++) {
    const left = deadline - Date.now();
    if (left <= 0) break;
    const abort = new AbortController();
    const timer = setTimeout(() => abort.abort(), left);
    let text = "";
    try {
      const stream = await client.chat.completions.create(
        { model: call.model ?? cfg.omniModel, messages, stream: true, modalities: ["text"] }, { signal: abort.signal });
      for await (const part of stream) {
        const delta = part.choices[0]?.delta?.content;
        if (!delta) continue;
        if (firstToken === null) firstToken = Date.now() - t0;
        text += delta;
      }
    } catch (err) {
      if (!abort.signal.aborted) throw err;
    } finally { clearTimeout(timer); }
    // The deadline ends a stream quietly: say so, not that half a reply was not JSON.
    if (abort.signal.aborted) { why = "out of time"; break; }
    // The first object, from the last back, that matches the schema: a thinking model may write others before it.
    const checks = jsonObjects(text).map((value) => call.schema.safeParse(value));
    const good = checks.find((c) => c.success);
    if (good?.success) { timing?.({ firstTokenMs: firstToken, totalMs: Date.now() - t0, attempts: attempt }); return good.data; }
    why = checks[0] && !checks[0].success ? issues(checks[0].error) : "no JSON object in the reply";
    messages.push({ role: "assistant", content: text || "(nothing)" }, { role: "user", content: `That reply was not valid (${why}). Reply again with the JSON object only.` });
  }
  throw new Error(why === "out of time" ? `the OMNI model ran out of time (${budget} ms)` : `the OMNI model did not return valid JSON: ${why}`);
}
