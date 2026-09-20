import "../env.js";
import { S, type BuildState, type CopilotResponse } from "@cutonce/schemas";
import { loadConfig } from "../config.js";

/**
 * Exercises the same HTTP boundary as the Quest, but supplies a typed question so the result is
 * deterministic and does not depend on a microphone. The last real Quest frame is attached when
 * one is available, and the returned speech stream must contain audio.
 *
 *   pnpm copilot:smoke
 *   pnpm copilot:smoke -- --question "hey what do I build" --base http://127.0.0.1:8080
 */
const cfg = loadConfig();
const arg = (name: string) => {
  const i = process.argv.indexOf(name);
  return i >= 0 ? process.argv[i + 1] : undefined;
};
const base = (arg("--base") ?? `http://127.0.0.1:${cfg.port}`).replace(/\/$/, "");
const question = arg("--question") ?? "hey waht do i build";
const headers = { Authorization: `Bearer ${cfg.apiToken}` };

async function json<T>(path: string): Promise<T> {
  const response = await fetch(base + path, { headers, signal: AbortSignal.timeout(10_000) });
  const text = await response.text();
  if (!response.ok) throw new Error(`${path}: HTTP ${response.status} ${text.slice(0, 300)}`);
  return JSON.parse(text) as T;
}

const assembly = await json<{ assembly_id: string; plan_revision: number }>("/v1/assemblies/current");
const state = await json<BuildState>(`/v1/assemblies/${encodeURIComponent(assembly.assembly_id)}/state`);
const last: { frame_bytes?: number } = await json<{ frame_bytes?: number }>("/v1/copilot/debug/last").catch(() => ({}));

const context = {
  context_id: `ctx_smoke_${Date.now()}`,
  assembly_id: assembly.assembly_id,
  plan_revision: assembly.plan_revision,
  state_version: state.version,
  mode: "overlay" as const,
  selected_part_id: null,
  selection_source: "none" as const,
  current_step_id: state.current_step_id,
  visible_parts: [],
  camera: null,
  scripted_query_id: null,
  client_sent_at: new Date().toISOString(),
};
const parsedContext = S.CopilotContext.parse(context);
const form = new FormData();
form.append("context", JSON.stringify(parsedContext));
form.append("question", question);
if (last.frame_bytes) {
  const frame = await fetch(base + "/v1/copilot/debug/frame.jpg", { headers, signal: AbortSignal.timeout(10_000) });
  if (frame.ok) form.append("frame", await frame.blob(), "frame.jpg");
}

const started = performance.now();
const response = await fetch(`${base}/v1/assemblies/${encodeURIComponent(assembly.assembly_id)}/copilot/query`, {
  method: "POST",
  headers,
  body: form,
  signal: AbortSignal.timeout(45_000),
});
const body = await response.text();
if (!response.ok) throw new Error(`copilot query: HTTP ${response.status} ${body.slice(0, 500)}`);
const answer = S.CopilotResponse.parse(JSON.parse(body)) as CopilotResponse;
const responseMs = Math.round(performance.now() - started);

if (answer.transcript !== question) throw new Error(`transcript mismatch: ${JSON.stringify(answer.transcript)}`);
if (!answer.answer_text.trim()) throw new Error("copilot returned an empty answer");
if (!answer.audio_url) throw new Error("copilot returned no audio URL");

const audio = await fetch(base + answer.audio_url, { headers, signal: AbortSignal.timeout(15_000) });
const audioBytes = (await audio.arrayBuffer()).byteLength;
if (!audio.ok || audioBytes < 1_000) throw new Error(`answer audio: HTTP ${audio.status}, ${audioBytes} bytes`);

console.log(JSON.stringify({
  ok: true,
  question,
  assembly_id: assembly.assembly_id,
  used_last_quest_frame: Boolean(last.frame_bytes),
  response_ms: responseMs,
  answer: answer.answer_text,
  action: answer.action,
  highlights: answer.highlight_parts,
  twin_highlights: answer.highlight_twins ?? [],
  timings_ms: answer.timings_ms,
  audio_bytes: audioBytes,
  audio_type: audio.headers.get("content-type"),
}, null, 2));
