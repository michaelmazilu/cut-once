// The real OpenAI request code over real HTTP: transcribe(), ask() with its tool round, camera verification and
// `pnpm g0`, against a stand-in OpenAI and an Agent Builder MCP endpoint that accepts connections and never answers.
// The other copilot tests replace transcribe() and ask() with fakes; this file is what proves the calls themselves.
import { execFile } from "node:child_process";
import { readFileSync } from "node:fs";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { createServer as createTcp, type AddressInfo, type Server as TcpServer, type Socket } from "node:net";
import { join } from "node:path";
import { promisify } from "node:util";
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { Strict, type CopilotContext } from "@cutonce/schemas";
import { REPO_ROOT } from "../src/config.js";
import { auth, makeApp } from "./helpers.js";

type Seen = { path: string; model?: string; schema?: string; tools: number; images: number; toolResults: number; effort?: string; at: number };
const seen: Seen[] = [];
const stand = { toolRound: false, flow: "question", heard: "where does the power cable run" };
/** The answer model's calls. The router's call goes out first on every turn that reaches a model. */
const answers = () => seen.filter((s) => s.path === "chat" && s.schema !== "route");
const ANSWER = { answer_text: "Run it through the tray.", highlight_parts: ["part_cable_tray"], highlight_style: "path", chunk_ids: [], action: null, confidence: 0.9, needs_clarification: false };

const readBody = (req: IncomingMessage) => new Promise<Buffer>((resolve) => { const c: Buffer[] = []; req.on("data", (d) => c.push(d)); req.on("end", () => resolve(Buffer.concat(c))); });

async function openai(req: IncomingMessage, res: ServerResponse) {
  const raw = await readBody(req);
  const send = (body: unknown) => { res.writeHead(200, { "content-type": "application/json" }); res.end(JSON.stringify(body)); };
  if (req.url?.includes("/audio/transcriptions")) {
    seen.push({ path: "transcriptions", model: /name="model"\r\n\r\n([^\r]+)/.exec(raw.toString("latin1"))?.[1], tools: 0, images: 0, toolResults: 0, at: Date.now() });
    return send({ text: stand.heard });
  }
  const b = JSON.parse(raw.toString()) as { model: string; tools?: unknown[]; reasoning_effort?: string; response_format?: { json_schema?: { name?: string } }; messages: { role: string; content: unknown }[] };
  const parts = b.messages.flatMap((m) => (Array.isArray(m.content) ? m.content : [])) as { type: string }[];
  const schema = b.response_format?.json_schema?.name;
  seen.push({ path: "chat", model: b.model, schema, tools: b.tools?.length ?? 0, images: parts.filter((p) => p.type === "image_url").length,
    toolResults: b.messages.filter((m) => m.role === "tool").length, effort: b.reasoning_effort, at: Date.now() });
  // The real gpt-5.6-luna is a reasoning model: chat completions refuse function tools unless reasoning_effort is "none".
  // The stand-in says the same, in the real words, so a request the real model would refuse fails here too.
  if (b.tools?.length && b.reasoning_effort !== "none") {
    res.writeHead(400, { "content-type": "application/json" });
    return res.end(JSON.stringify({ error: { message: `Function tools with reasoning_effort are not supported for ${b.model} in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'.`, type: "invalid_request_error", param: null, code: null } }));
  }
  const reply = (content: string | null, tool_calls?: object[]) => send({ id: "chatcmpl_wire", object: "chat.completion", created: 0, model: b.model,
    choices: [{ index: 0, finish_reason: tool_calls ? "tool_calls" : "stop", message: { role: "assistant", content, ...(tool_calls ? { tool_calls } : {}) } }] });
  if (schema === "route") return reply(JSON.stringify({ flow: stand.flow, confidence: 0.95 }));
  if (schema === "kit_turn") return reply(JSON.stringify({ heard: "(replaced by the transcript)", intent: "question", wish: null, pick: null, answer: "It goes on the cans.", objects: [], confidence: 0.9 }));
  if (schema === "verification") return reply(JSON.stringify({ verdict: "present", confidence: 0.9, evidence: "a tray is there" }));
  if (schema === "g0") return reply(JSON.stringify({ shape: "square", colour: "red", confident: true }));
  if (b.tools && stand.toolRound) return reply(null, ["search_documents", "find_parts", "lookup_material"].map((name, i) =>
    ({ id: `call_${i}`, type: "function", function: { name, arguments: name === "lookup_material" ? '{"text":"tray"}' : '{"query":"cable tray"}' } })));
  return reply(JSON.stringify(ANSWER));
}

type OmniBody = { model: string; stream: boolean; messages: { role: string; content: unknown }[] };
const omniSeen: OmniBody[] = [];
const KIT_OMNI = { heard: "which piece goes first", intent: "question", wish: null, pick: null, answer: "Stand the can up first.", objects: [], confidence: 0.9 };
async function omni(req: IncomingMessage, res: ServerResponse) {
  omniSeen.push(JSON.parse((await readBody(req)).toString()) as OmniBody);
  const chunk = (content: string | null, finish: string | null) =>
    `data: ${JSON.stringify({ id: "c1", object: "chat.completion.chunk", created: 0, model: "qwen", choices: [{ index: 0, delta: content === null ? {} : { content }, finish_reason: finish }] })}\n\n`;
  res.writeHead(200, { "content-type": "text/event-stream" });
  const text = JSON.stringify(KIT_OMNI);
  res.write(chunk(text.slice(0, 20), null)); res.write(chunk(text.slice(20), null)); res.write(chunk(null, "stop"));
  res.end("data: [DONE]\n\n");
}

let oai: Server, omniServer: Server, omniUrl = "", stuck: TcpServer, oaiUrl = "", stuckMcpUrl = "";
const stuckSockets = new Set<Socket>();
beforeAll(async () => {
  oai = createServer((q, s) => void openai(q, s));
  await new Promise<void>((r) => oai.listen(0, "127.0.0.1", r));
  oaiUrl = `http://127.0.0.1:${(oai.address() as AddressInfo).port}/v1`;
  omniServer = createServer((q, s) => void omni(q, s));
  await new Promise<void>((r) => omniServer.listen(0, "127.0.0.1", r));
  omniUrl = `http://127.0.0.1:${(omniServer.address() as AddressInfo).port}/v1`;
  stuck = createTcp((socket) => { stuckSockets.add(socket); }); // accepts and never answers: a Kibana that is up but hung
  await new Promise<void>((r) => stuck.listen(0, "127.0.0.1", r));
  stuckMcpUrl = `http://127.0.0.1:${(stuck.address() as AddressInfo).port}/api/agent_builder/mcp`;
  process.env.OPENAI_BASE_URL = oaiUrl;
});
afterAll(() => { for (const s of stuckSockets) s.destroy(); stuck.close(); oai.close(); omniServer.close(); delete process.env.OPENAI_BASE_URL; });
beforeEach(() => { seen.length = 0; omniSeen.length = 0; stand.toolRound = false; stand.flow = "question"; stand.heard = "where does the power cable run"; delete process.env.OPENAI_ROUTER_MODEL; process.env.COPILOT_ROUTE_MS = "5000"; process.env.COPILOT_CAP_MS = "30000"; delete process.env.OPENAI_COPILOT_MODEL; });
afterEach(() => { delete process.env.COPILOT_ROUTE_MS; delete process.env.COPILOT_CAP_MS; delete process.env.OPENAI_COPILOT_MODEL; });

const FIXTURES = join(REPO_ROOT, "data", "fixtures");
const frame = () => readFileSync(join(FIXTURES, "frame_0001.jpg"));
const fixtureContext = () => Strict.CopilotContext.parse(JSON.parse(readFileSync(join(FIXTURES, "context_packet.json"), "utf8"))) as CopilotContext;

function multipart(parts: { name: string; value: string | Buffer; filename?: string; type?: string }[]) {
  const boundary = "----cutoncewire";
  const chunks: Buffer[] = [];
  for (const p of parts) {
    const disposition = p.filename ? `; filename="${p.filename}"` : "";
    const type = p.type ? `\r\nContent-Type: ${p.type}` : "";
    chunks.push(Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="${p.name}"${disposition}${type}\r\n\r\n`));
    chunks.push(Buffer.isBuffer(p.value) ? p.value : Buffer.from(p.value));
    chunks.push(Buffer.from("\r\n"));
  }
  chunks.push(Buffer.from(`--${boundary}--\r\n`));
  return { payload: Buffer.concat(chunks), headers: { ...auth, "content-type": `multipart/form-data; boundary=${boundary}` } };
}

type App = Awaited<ReturnType<typeof makeApp>>;
function question(t: App, opts: { withFrame?: boolean; mode?: "upload" | "overlay" | "build" } = {}) {
  const aid = t.app.ctx.store.currentAssembly()!.assembly_id;
  const withFrame = opts.withFrame ?? true;
  return t.app.inject({ method: "POST", url: `/v1/assemblies/${aid}/copilot/query`, ...multipart([
    { name: "context", value: JSON.stringify({ ...fixtureContext(), assembly_id: aid, ...(opts.mode ? { mode: opts.mode } : {}), ...(withFrame ? {} : { camera: null, visible_parts: [] }) }) },
    { name: "audio", value: Buffer.from("RIFFfake"), filename: "turn.wav", type: "audio/wav" },
    ...(withFrame ? [{ name: "frame", value: frame(), filename: "frame.jpg", type: "image/jpeg" }] : []),
  ]) });
}

const run = promisify(execFile);
function g0(env: Record<string, string>): Promise<{ code: number; stdout: string; stderr: string }> {
  return run("pnpm", ["exec", "tsx", "src/cli/g0-check.ts"], {
    cwd: new URL("..", import.meta.url).pathname, timeout: 60_000,
    env: { ...process.env, OPENAI_BASE_URL: oaiUrl, OPENAI_COPILOT_MODEL: "", OPENAI_MODEL: "", ...env },
  }).then((o) => ({ code: 0, stdout: o.stdout, stderr: o.stderr }), (e: { code: number; stdout: string; stderr: string }) => e);
}

describe("the real OpenAI calls", () => {
  it("transcribe, route, then one model call with both frames, the five tools and the per-question schema", async () => {
    const t = await makeApp({ openaiKey: "sk-test", copilotMode: "live" });
    try {
      const r = await question(t);
      expect(r.statusCode).toBe(200);
      expect(r.json().answer_text).toBe(ANSWER.answer_text);
      expect(seen.map((s) => s.path)).toEqual(["transcriptions", "chat", "chat"]);
      expect(seen[0]!.model).toBe("gpt-transcribe");
      expect(seen[1]).toMatchObject({ model: "gpt-5.6-luna", schema: "route", tools: 0, images: 0 });   // text only: the router never sees the camera
      expect(seen[1]!.effort).toBe("none");   // at the default effort gpt-5.6-luna routes in ~1.1 s, past its budget every time
      expect(seen[2]).toMatchObject({ model: "gpt-5.6-luna", schema: "copilot_answer", tools: 5, images: 2, effort: "none" });   // tools need it (see the stand-in), and it keeps the answer inside the 6 s budget
    } finally { await t.cleanup(); }
  });

  it("feed the tool results back in a second call without tools", async () => {
    stand.toolRound = true;
    const t = await makeApp({ openaiKey: "sk-test", copilotMode: "live" });
    try {
      expect((await question(t)).statusCode).toBe(200);
      expect(answers().map((c) => [c.tools, c.toolResults])).toEqual([[5, 0], [0, 3]]);
      expect(answers().map((c) => c.effort)).toEqual(["none", "none"]);
    } finally { await t.cleanup(); }
  });

  it("send no images at all when the headset had no camera frame", async () => {
    const t = await makeApp({ openaiKey: "sk-test", copilotMode: "live" });
    try {
      expect((await question(t, { withFrame: false })).statusCode).toBe(200);
      expect(answers().map((c) => c.images)).toEqual([0]);
    } finally { await t.cleanup(); }
  });

  it("outside build mode the router is OpenAI's even with OMNI set for Kit: E7 and the desk route as they did", async () => {
    process.env.OPENAI_ROUTER_MODEL = "tiny-router";
    const t = await makeApp({ openaiKey: "sk-test", omniKey: "q", omniBaseUrl: omniUrl, copilotMode: "live" });
    try {
      expect((await question(t)).statusCode).toBe(200);
      expect(seen.filter((s) => s.path === "chat").map((c) => [c.model, c.schema])[0]).toEqual(["tiny-router", "route"]);
      expect(omniSeen).toEqual([]);
    } finally { await t.cleanup(); }
  });

  it("the router's own model goes on the wire, and its 'build ideas' ends the turn: a scan, and no answer call", async () => {
    process.env.OPENAI_ROUTER_MODEL = "tiny-router";
    stand.flow = "build_ideas";
    const t = await makeApp({ openaiKey: "sk-test", copilotMode: "live" });
    try {
      const r = await question(t);
      expect([r.statusCode, r.json().action]).toEqual([200, { type: "start_scan" }]);
      expect(seen.filter((s) => s.path === "chat").map((c) => [c.model, c.schema])).toEqual([["tiny-router", "route"]]);
    } finally { await t.cleanup(); }
  });
});

describe("Kit's turn (build mode) over the wire", () => {
  it("on OpenAI: transcribes, then one Kit call with the photo and no tools; what was heard is the transcript", async () => {
    const t = await makeApp({ openaiKey: "sk-test", copilotMode: "live" });
    try {
      const r = await question(t, { mode: "build" });
      expect([r.statusCode, r.json().answer_text, r.json().transcript]).toEqual([200, "It goes on the cans.", "where does the power cable run"]);
      expect(seen.map((x) => [x.path, x.schema ?? null])).toEqual([["transcriptions", null], ["chat", "kit_turn"]]);
      expect(seen[1]).toMatchObject({ tools: 0, images: 1 });
      expect(r.json().timings_ms).toMatchObject({ kit_openai: 1 });
    } finally { await t.cleanup(); }
  });

  it("on OpenAI, a voice command said outright is answered from the transcript alone: no Kit call to wait for", async () => {
    stand.heard = "Next.";
    const t = await makeApp({ openaiKey: "sk-test", copilotMode: "live" });
    try {
      const r = await question(t, { mode: "build" });
      expect([r.statusCode, r.json().answer_text, r.json().action]).toEqual([200, "Next step.", { type: "step_nav", direction: "next" }]);
      expect(seen.map((x) => x.path)).toEqual(["transcriptions"]);
      expect(r.json().timings_ms).toMatchObject({ fast_path: 1, kit_openai: 1 });
      expect(r.json().timings_ms.kit).toBeUndefined();
    } finally { await t.cleanup(); }
  });

  it("on OMNI: one streamed call hears the voice clip itself and sees the photo; OpenAI only transcribes alongside, for commands", async () => {
    const t = await makeApp({ openaiKey: "sk-test", omniKey: "q", omniBaseUrl: omniUrl, copilotMode: "live" });
    try {
      const r = await question(t, { mode: "build" });
      expect([r.statusCode, r.json().answer_text, r.json().transcript]).toEqual([200, "Stand the can up first.", "which piece goes first"]);
      expect(seen.map((x) => x.path)).toEqual(["transcriptions"]);        // "where does the power cable run": not a command
      expect(omniSeen).toHaveLength(1);
      const body = omniSeen[0]!;
      expect([body.model, body.stream]).toEqual(["qwen3.5-omni-flash", true]);
      expect(String(body.messages[0]!.content)).toMatch(/^You are Kit, the Kitbash co-pilot/);
      const parts = body.messages[1]!.content as { type: string; text?: string; input_audio?: { data: string; format: string } }[];
      expect(parts.map((x) => x.type)).toEqual(["text", "image_url", "input_audio"]);
      expect(parts[0]!.text).toMatch(/SAID: \(in the audio\)$/);
      expect(parts[2]!.input_audio).toEqual({ data: `data:;base64,${Buffer.from("RIFFfake").toString("base64")}`, format: "wav" });
      expect(r.json().timings_ms).toMatchObject({ kit_omni: 1 });
    } finally { await t.cleanup(); }
  });

  it("on OMNI with no OpenAI key, nothing at all goes to OpenAI", async () => {
    const t = await makeApp({ openaiKey: "", omniKey: "q", omniBaseUrl: omniUrl, copilotMode: "live" });
    try {
      expect((await question(t, { mode: "build" })).json().answer_text).toBe("Stand the can up first.");
      expect([seen, omniSeen.length]).toEqual([[], 1]);
    } finally { await t.cleanup(); }
  });
});

describe("tool calls", () => {
  it("run together, so three stuck Agent Builder calls cost one timeout, not three", async () => {
    stand.toolRound = true;
    const t = await makeApp({ openaiKey: "sk-test", copilotMode: "live", mcpUrl: stuckMcpUrl, esApiKey: "test-key" });
    try {
      const r = await question(t);
      expect(r.json().timings_ms.tool_calls).toBe(3);
      const [first, second] = answers();
      expect(second!.at - first!.at).toBeLessThan(3500); // one 2 s tool timeout, not three in a row (6 s)
    } finally { await t.cleanup(); }
  }, 30_000);
});

describe("one model setting for every image call", () => {
  it("OPENAI_COPILOT_MODEL reaches camera verification too", async () => {
    process.env.OPENAI_COPILOT_MODEL = "vision-backup";
    const t = await makeApp({ openaiKey: "sk-test", copilotMode: "live" });
    try {
      const aid = t.app.ctx.store.currentAssembly()!.assembly_id;
      const v = await t.app.inject({ method: "POST", url: `/v1/assemblies/${aid}/verify`, ...multipart([
        { name: "request", value: JSON.stringify({ verification_id: "ver_wire_0001", part_id: "part_cable_tray", claimed_state: "built", state_version: 0,
          bbox_px: [430, 470, 420, 46], in_frame: 0.97, camera: { width: 1280, height: 960, fx: 870, fy: 870, cx: 640, cy: 480 } }) },
        { name: "frame", value: frame(), filename: "frame.jpg", type: "image/jpeg" }]) });
      expect(v.json().model).toBe("vision-backup");
      expect(seen.find((s) => s.schema === "verification")?.model).toBe("vision-backup");
    } finally { await t.cleanup(); }
  });

  it("pnpm g0 checks the copilot's model with the key it was given", async () => {
    const out = await g0({ OPENAI_API_KEY: "sk-test", OPENAI_COPILOT_MODEL: "vision-backup", ELEVENLABS_API_KEY: "" });
    expect(out.stdout).toMatch(/^PASS {2}G0/m);
    expect(out.stdout).toMatch(/^SKIP {2}TTS/m);
    expect(out.code).toBe(0);
    expect(seen.find((s) => s.schema === "g0")).toMatchObject({ model: "vision-backup", images: 1 });
  }, 70_000);

  it("pnpm g0 with no keys skips every check and does not fail", async () => {
    const out = await g0({ OPENAI_API_KEY: "", ELEVENLABS_API_KEY: "" });
    expect(out.stdout.match(/^SKIP /gm)).toHaveLength(3);
    expect(out.code).toBe(0);
  }, 70_000);
});
