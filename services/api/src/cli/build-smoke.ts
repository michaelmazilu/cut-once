import "../env.js";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { S, type BuildIdea, type BuildScan, type BuildState, type CopilotResponse, type Inventory, type Plan } from "@cutonce/schemas";
import { loadConfig, REPO_ROOT } from "../config.js";

/** A destructive-on-purpose live rehearsal: scan the synthetic table, start one idea and advance its first step. */
const cfg = loadConfig();
const arg = (name: string) => {
  const i = process.argv.indexOf(name);
  return i >= 0 ? process.argv[i + 1] : undefined;
};
const base = (arg("--base") ?? `http://127.0.0.1:${cfg.port}`).replace(/\/$/, "");
const headers = { Authorization: `Bearer ${cfg.apiToken}` };
const recording = join(REPO_ROOT, "data", "build", "recordings", "synthetic_kit");
const photo = readFileSync(join(recording, "photo.jpg"));
const scan = JSON.parse(readFileSync(join(recording, "scan.json"), "utf8")) as BuildScan;

async function call<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(base + path, {
    method,
    headers: { ...headers, ...(body === undefined ? {} : { "content-type": "application/json" }) },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(45_000),
  });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path}: HTTP ${response.status} ${text.slice(0, 500)}`);
  return (text ? JSON.parse(text) : null) as T;
}

const messages: Record<string, unknown>[] = [];
const socket = new WebSocket(`${base.replace(/^http/, "ws")}/v1/stream?token=${encodeURIComponent(cfg.apiToken)}&client=smoke&id=build-smoke`);
await new Promise<void>((resolve, reject) => {
  const timer = setTimeout(() => reject(new Error("websocket did not open within 5 seconds")), 5_000);
  socket.addEventListener("open", () => { clearTimeout(timer); resolve(); }, { once: true });
  socket.addEventListener("error", () => { clearTimeout(timer); reject(new Error("websocket failed to open")); }, { once: true });
});
socket.addEventListener("message", (event) => {
  try { messages.push(JSON.parse(String(event.data)) as Record<string, unknown>); } catch { /* ignore non-JSON */ }
});

async function waitFor(test: (message: Record<string, unknown>) => boolean, label: string, timeoutMs = 60_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const found = messages.find(test);
    if (found) return found;
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error(`no ${label} message within ${timeoutMs} ms`);
}

async function ask(assembly: { assembly_id: string; plan_revision: number }, state: BuildState, mode: "overlay" | "build", question: string) {
  const context = S.CopilotContext.parse({
    context_id: `ctx_build_smoke_${Date.now()}`,
    assembly_id: assembly.assembly_id,
    plan_revision: assembly.plan_revision,
    state_version: state.version,
    mode,
    selected_part_id: null,
    selection_source: "none",
    current_step_id: state.current_step_id,
    visible_parts: [],
    camera: null,
    scripted_query_id: null,
    client_sent_at: new Date().toISOString(),
  });
  const form = new FormData();
  form.append("context", JSON.stringify(context));
  form.append("question", question);
  form.append("frame", new Blob([photo], { type: "image/jpeg" }), "frame.jpg");
  const started = performance.now();
  const response = await fetch(`${base}/v1/assemblies/${encodeURIComponent(assembly.assembly_id)}/copilot/query`, {
    method: "POST", headers, body: form, signal: AbortSignal.timeout(45_000),
  });
  const text = await response.text();
  if (!response.ok) throw new Error(`copilot ${JSON.stringify(question)}: HTTP ${response.status} ${text.slice(0, 500)}`);
  const answer = S.CopilotResponse.parse(JSON.parse(text)) as CopilotResponse;
  if (!answer.answer_text.trim() || !answer.audio_url) throw new Error(`incomplete copilot response for ${JSON.stringify(question)}`);
  const audio = await fetch(base + answer.audio_url, { headers, signal: AbortSignal.timeout(15_000) });
  const audioBytes = (await audio.arrayBuffer()).byteLength;
  if (!audio.ok || audioBytes < 1_000) throw new Error(`audio for ${JSON.stringify(question)}: HTTP ${audio.status}, ${audioBytes} bytes`);
  return { answer, audioBytes, ms: Math.round(performance.now() - started) };
}

try {
  const before = await call<{ assembly_id: string; plan_revision: number }>("GET", "/v1/assemblies/current");
  const beforeState = await call<BuildState>("GET", `/v1/assemblies/${encodeURIComponent(before.assembly_id)}/state`);
  const invitation = await ask(before, beforeState, "overlay", "hey waht do i build");
  if (invitation.answer.action?.type !== "start_scan") throw new Error(`build invitation did not start a scan: ${JSON.stringify(invitation.answer.action)}`);

  const accepted = await call<{ session_id: string }>("POST", "/v1/build/scans", {
    device_id: "smoke",
    grid: scan.grid,
    points_mm: scan.points_mm,
    hit: scan.hit,
    camera: scan.camera,
    photo_b64: photo.toString("base64"),
  });
  const inventoryMessage = await waitFor((message) => {
    const inventory = message.inventory as Inventory | undefined;
    return message.type === "build_inventory" && inventory?.session_id === accepted.session_id && inventory.labelled;
  }, "labelled build_inventory");
  const ideasMessage = await waitFor((message) => message.type === "build_ideas" && message.session_id === accepted.session_id && message.final === true, "final build_ideas");
  const inventory = inventoryMessage.inventory as Inventory;
  const ideas = ideasMessage.ideas as BuildIdea[];
  const named = inventory.twins.filter((t) => t.name !== "unknown");
  if (named.length < 4) throw new Error(`object recognition named ${named.length} objects; expected at least 4`);
  if (!ideas.length) throw new Error("the scan produced no build ideas");
  for (const idea of ideas) S.Plan.parse(idea.plan);

  const chosen = ideas[0]!;
  const started = await call<{ assembly_id: string; plan_id: string; revision: number }>("POST", `/v1/build/ideas/${encodeURIComponent(chosen.idea_id)}/start`, {});
  const assembly = await call<{ assembly_id: string; plan_id: string; plan_revision: number }>("GET", `/v1/assemblies/${encodeURIComponent(started.assembly_id)}`);
  const plan = await call<Plan>("GET", `/v1/plans/${encodeURIComponent(started.plan_id)}?revision=${started.revision}`);
  const state = await call<BuildState>("GET", `/v1/assemblies/${encodeURIComponent(started.assembly_id)}/state`);
  S.Plan.parse(plan);
  if (!plan.parts.length || !plan.steps.length) throw new Error("started build has no hologram parts or coaching steps");

  const coaching = await ask(assembly, state, "build", "Which object should I pick up first? Point it out.");
  const knownTwins = new Set(inventory.twins.map((t) => t.twin_id));
  const invalidTwin = (coaching.answer.highlight_twins ?? []).find((id) => !knownTwins.has(id));
  if (invalidTwin) throw new Error(`copilot highlighted unknown twin ${invalidTwin}`);

  const done = await ask(assembly, state, "build", "done");
  if (done.answer.action?.type !== "mark_state") throw new Error(`"done" did not advance the build: ${JSON.stringify(done.answer.action)}`);
  const afterState = await call<BuildState>("GET", `/v1/assemblies/${encodeURIComponent(started.assembly_id)}/state`);
  if (afterState.version <= state.version) throw new Error("build state did not advance after the coaching command");

  console.log(JSON.stringify({
    ok: true,
    invitation: { answer: invitation.answer.answer_text, action: invitation.answer.action, ms: invitation.ms, audio_bytes: invitation.audioBytes },
    recognition: { session_id: accepted.session_id, named_objects: named.map((t) => ({ id: t.twin_id, label: t.label })), ideas: ideas.map((idea) => idea.title) },
    hologram: { assembly_id: started.assembly_id, title: chosen.title, parts: plan.parts.length, steps: plan.steps.length },
    coaching: { answer: coaching.answer.answer_text, twin_highlights: coaching.answer.highlight_twins ?? [], ms: coaching.ms, audio_bytes: coaching.audioBytes },
    progression: { answer: done.answer.answer_text, action: done.answer.action, version_before: state.version, version_after: afterState.version, highlights: done.answer.highlight_parts },
  }, null, 2));
} finally {
  socket.close();
}
