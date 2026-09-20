/**
 * End-to-end over real HTTP: our server and CLIs talk, through the real SDK clients, to stand-in Kibana (tools API +
 * Agent Builder MCP), Elasticsearch and OpenAI servers. This checks what unit tests with injected functions cannot:
 * the requests actually sent on the wire, and the responses as the SDKs actually parse them.
 */
import { execFile } from "node:child_process";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { z } from "zod";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { CopilotAnswer, copilotAnswerSchema, keepKnown } from "../src/copilot/schema.js";
import { jsonCall } from "../src/llm.js";
import { runEsql } from "../src/search/fallbacks.js";
import { resetMcp } from "../src/search/mcp.js";
import { retrieve } from "../src/search/retrieve.js";
import { callKnowledgeTool } from "../src/search/tools.js";
import { TOKEN, auth, builtEvent, makeApp } from "./helpers.js";

const run = promisify(execFile);
const tsxCli = fileURLToPath(new URL("../../../node_modules/tsx/dist/cli.mjs", import.meta.url));
const readBody = (req: IncomingMessage) => new Promise<string>((resolve) => { let b = ""; req.on("data", (c) => (b += c)); req.on("end", () => resolve(b)); });
async function listen(handler: (req: IncomingMessage, res: ServerResponse) => void | Promise<void>) {
  const server = createServer((req, res) => void handler(req, res));
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  return { server, url: `http://127.0.0.1:${(server.address() as AddressInfo).port}` };
}
const json = (res: ServerResponse, status: number, body: unknown, headers: Record<string, string> = {}) => {
  res.writeHead(status, { "content-type": "application/json", ...headers }); res.end(JSON.stringify(body));
};

// ── stand-in Kibana: Agent Builder tools API + MCP endpoint ─────────────────────────────────────────────────
const kibana = { created: [] as any[], xsrf: [] as string[], mcpAuth: [] as string[], webhook: "", logIssueCalls: 0, webhookReplies: [] as any[], initializes: 0, failNextCall: false, rejectCreate: false, hangSearch: true };
const esqlResult = (columns: { name: string; type: string }[], values: unknown[][]) =>
  ({ content: [{ type: "text" as const, text: JSON.stringify({ results: [{ type: "esql_results", data: { columns, values } }] }) }] });
function fakeAgentBuilder() {
  const s = new McpServer({ name: "fake-agent-builder", version: "1.0.0" });
  s.tool("cutonce_find_parts", { query: z.string() }, async () =>
    esqlResult([{ name: "part_id", type: "keyword" }, { name: "name", type: "text" }], [["part_left_rear_leg", "Left rear leg"], ["part_right_rear_leg", "Right rear leg"]]));
  s.tool("cutonce_lookup_material", { text: z.string() }, async () => esqlResult([{ name: "material_id", type: "keyword" }], [["mat_leg_700"]]));
  s.tool("cutonce_build_history", { assembly_id: z.string() }, async () => esqlResult([{ name: "version", type: "integer" }], [[1], [2], [3]]));
  s.tool("cutonce_search_documents", { query: z.string() }, async () => kibana.hangSearch
    ? new Promise<never>(() => undefined) // hangs, to force the fallback
    : esqlResult([{ name: "chunk_id", type: "keyword" }], [["chunk_desk_drawings_p2_1"]]));
  s.tool("cutonce_log_issue", { issue_id: z.string(), part_id: z.string(), note: z.string() }, async ({ issue_id, part_id, note }) => {
    kibana.logIssueCalls++;
    // Like the real Workflow with "wait for completion" on: it calls our webhook, and answers only after we gave up.
    setTimeout(() => void fetch(kibana.webhook, { method: "POST", headers: { authorization: `Bearer ${TOKEN}`, "content-type": "application/json" },
      body: JSON.stringify({ issue_id, part_id, note }) }).then((r) => r.json()).then((j) => kibana.webhookReplies.push(j)), 300);
    await new Promise((r) => setTimeout(r, 800));
    return { content: [{ type: "text" as const, text: JSON.stringify({ results: [{ type: "other", data: { execution_id: "exec_1" } }] }) }] };
  });
  return s;
}
async function kibanaHandler(req: IncomingMessage, res: ServerResponse) {
  const url = req.url ?? "";
  if (url.startsWith("/api/agent_builder/mcp")) {
    kibana.mcpAuth.push(String(req.headers.authorization ?? ""));
    const body = req.method === "POST" ? await readBody(req) : "";
    const msg = body ? JSON.parse(body) : undefined;
    if (msg?.method === "initialize") kibana.initializes++;
    if (msg?.method === "tools/call" && kibana.failNextCall) { kibana.failNextCall = false; return json(res, 500, { message: "Kibana restarting" }); }
    const server = fakeAgentBuilder();
    const transport = new StreamableHTTPServerTransport({ sessionIdGenerator: undefined }); // stateless, like Kibana's
    res.on("close", () => { void transport.close(); void server.close(); });
    await server.connect(transport);
    await transport.handleRequest(req, res, msg);
    return;
  }
  if (req.method === "GET" && url.startsWith("/api/agent_builder/tools/")) return json(res, 404, { message: "not found" });
  if (req.method === "POST" && url === "/api/agent_builder/tools") {
    kibana.xsrf.push(String(req.headers["kbn-xsrf"] ?? ""));
    if (kibana.rejectCreate) return json(res, 400, { message: "[configuration.params.query.type]: expected string" });
    kibana.created.push(JSON.parse(await readBody(req)));
    return json(res, 200, { ok: true });
  }
  json(res, 404, { message: `unexpected ${req.method} ${url}` });
}

// ── stand-in Elasticsearch ───────────────────────────────────────────────────────────────────────────────────
const es = { searches: [] as any[], esql: [] as any[], indexed: [] as string[], auth: new Set<string>() };
const ES_HEADERS = { "x-elastic-product": "Elasticsearch" }; // the official client refuses servers without it
async function esHandler(req: IncomingMessage, res: ServerResponse) {
  const url = (req.url ?? "").split("?")[0]!;
  es.auth.add(String(req.headers.authorization ?? ""));
  const body = await readBody(req);
  if (url === "/" ) return json(res, 200, { name: "fake", cluster_name: "fake", version: { number: "9.4.0" }, tagline: "You Know, for Search" }, ES_HEADERS);
  if (url.endsWith("/_search")) {
    es.searches.push(JSON.parse(body));
    return json(res, 200, { took: 1, timed_out: false, _shards: { total: 1, successful: 1, skipped: 0, failed: 0 }, hits: { total: { value: 1, relation: "eq" }, max_score: 1.5,
      hits: [{ _index: "cutonce-docs", _id: "chunk_desk_drawings_p2_1", _score: 1.5, _source: { chunk_id: "chunk_desk_drawings_p2_1", document_id: "doc_desk_drawings", page: 2,
        title: "E-1 Wiring", text: "Run the cable through the tray to the right rear leg.", part_ids: ["part_power_cable"], page_image_uri: "/v1/documents/doc_desk_drawings/pages/2.png" } }] } }, ES_HEADERS);
  }
  if (url === "/_query") { es.esql.push(JSON.parse(body)); return json(res, 200, { columns: [{ name: "source", type: "keyword" }, { name: "events", type: "long" }], values: [["seed", 3]] }, ES_HEADERS); }
  if (/\/_doc\//.test(url)) { es.indexed.push(url); return json(res, 201, { _index: url.split("/")[1], _id: url.split("/").pop(), _version: 1, result: "created", _shards: { total: 1, successful: 1, failed: 0 }, _seq_no: 0, _primary_term: 1 }, ES_HEADERS); }
  json(res, 404, { error: `unexpected ${req.method} ${url}` }, ES_HEADERS);
}

// ── stand-in OpenAI ─────────────────────────────────────────────────────────────────────────────────────────
const openai = { requests: [] as any[] };
async function openaiHandler(req: IncomingMessage, res: ServerResponse) {
  const body = JSON.parse(await readBody(req));
  openai.requests.push({ path: req.url, body });
  const answer = { answer_text: "Run it through the tray to the right rear leg.", highlight_parts: ["part_power_cable", "part_cable_tray"], highlight_style: "path",
    chunk_ids: ["chunk_desk_drawings_p2_1"], action: null, confidence: 0.9, needs_clarification: false };
  json(res, 200, { id: "chatcmpl_1", object: "chat.completion", created: 0, model: body.model, choices: [{ index: 0, finish_reason: "stop", message: { role: "assistant", content: JSON.stringify(answer) } }] });
}

let kib: Awaited<ReturnType<typeof listen>>, esSrv: Awaited<ReturnType<typeof listen>>, oai: Awaited<ReturnType<typeof listen>>;
let t: Awaited<ReturnType<typeof makeApp>>;
let apiUrl = "";
const servers: Server[] = [];

beforeAll(async () => {
  [kib, esSrv, oai] = await Promise.all([listen(kibanaHandler), listen(esHandler), listen(openaiHandler)]);
  servers.push(kib.server, esSrv.server, oai.server);
  resetMcp();
  t = await makeApp({ mcpUrl: `${kib.url}/api/agent_builder/mcp`, kibanaUrl: kib.url, esUrl: esSrv.url, esApiKey: "test-api-key",
    jinaEmbedId: ".jina-embeddings-v5-text-small", jinaRerankId: ".jina-reranker-v3", searchMode: "hybrid", openaiKey: "sk-test" });
  await t.app.listen({ port: 0, host: "127.0.0.1" });
  apiUrl = `http://127.0.0.1:${(t.app.server.address() as AddressInfo).port}`;
  kibana.webhook = `${apiUrl}/v1/webhooks/issue`;
});
afterAll(async () => {
  resetMcp();
  await t.cleanup();
  for (const s of servers) { s.closeAllConnections(); await new Promise((r) => s.close(r)); }
});

describe("Agent Builder over real MCP", () => {
  it("sends the ApiKey header, unwraps ES|QL results, and returns the direct twin's shape", async () => {
    const r = await callKnowledgeTool(t.app.ctx, "find_parts", { query: "rear leg" });
    expect(r).toEqual({ ok: true, via: "mcp", data: { parts: [{ part_id: "part_left_rear_leg", name: "Left rear leg", layer: null, step_id: null }, { part_id: "part_right_rear_leg", name: "Right rear leg", layer: null, step_id: null }] } });
    expect(kibana.mcpAuth.length).toBeGreaterThan(0);
    expect(new Set(kibana.mcpAuth)).toEqual(new Set(["ApiKey test-api-key"]));
    const h = await callKnowledgeTool(t.app.ctx, "build_history", {});
    expect(h).toMatchObject({ ok: true, via: "mcp", data: { report: "events" } });
    expect((h as any).data.events.map((e: any) => e.version)).toEqual([1, 2, 3]);
    expect(Object.keys((h as any).data.events[0]).sort()).toEqual(["new_state", "part_id", "previous_state", "seconds_since_prev", "source", "step_id", "timestamp", "version"]);
  });

  it("falls back to the direct twin when a remote tool hangs, which then queries Elasticsearch", async () => {
    const before = es.searches.length;
    const r = await callKnowledgeTool(t.app.ctx, "search_documents", { query: "where does the cable go", part_id: "part_power_cable" }, { timeoutMs: 300 });
    expect(r).toMatchObject({ ok: true, via: "direct" });
    expect((r as any).data.chunks[0]).toMatchObject({ chunk_id: "chunk_desk_drawings_p2_1", part_ids: ["part_power_cable"], page: 2 });
    expect(es.searches.length).toBe(before + 1);
  });

  it("log_issue: recorded locally first, the Workflow still runs, and its late webhook is ignored", async () => {
    const r = await callKnowledgeTool(t.app.ctx, "log_issue", { part_id: "part_left_rear_leg", note: "Thread is damaged" }, { timeoutMs: 150 });
    expect(r).toMatchObject({ ok: true, via: "direct" });
    const issueId = (r as any).data.issue_id as string;
    await new Promise((resolve) => setTimeout(resolve, 900)); // let the stand-in Workflow call our webhook
    expect(kibana.logIssueCalls).toBe(1);
    expect(kibana.webhookReplies).toEqual([{ ok: true, issue_id: issueId, duplicate: true }]); // the webhook really arrived, and was ignored
    const aid = t.app.ctx.store.currentAssembly()!.assembly_id;
    expect(t.app.ctx.store.getEvents(aid).events.filter((e) => e.note?.startsWith(`${issueId}:`))).toHaveLength(1);
    // Written twice on purpose, same id: our local record, then again when the webhook arrives, so ours is the last write.
    expect(es.indexed.filter((p) => p.endsWith(`/cutonce-issues/_doc/${issueId}`))).toHaveLength(2);
  });
});

describe("MCP session recovery", () => {
  it("drops a failed session, so the next call reconnects instead of reusing a broken client", async () => {
    await callKnowledgeTool(t.app.ctx, "find_parts", { query: "leg" }); // warm session
    const before = kibana.initializes;
    kibana.failNextCall = true;
    expect(await callKnowledgeTool(t.app.ctx, "find_parts", { query: "leg" })).toMatchObject({ via: "direct" });
    expect(await callKnowledgeTool(t.app.ctx, "find_parts", { query: "leg" })).toMatchObject({ via: "mcp" });
    expect(kibana.initializes).toBe(before + 1);
  });
});

describe("Elasticsearch on the wire", () => {
  it("hybrid retrieve sends one retriever tree (rerank around RRF) and maps hits", async () => {
    const before = es.searches.length;
    const chunks = await retrieve(t.app.ctx.cfg, { query: "cable route", projectId: "proj_cutonce_demo", partId: "part_power_cable", docTypes: ["electrical"] });
    const sent = es.searches[before];
    expect(sent.query).toBeUndefined();
    const rr = sent.retriever.text_similarity_reranker;
    expect([rr.inference_id, rr.field, rr.inference_text]).toEqual([".jina-reranker-v3", "text", "cable route"]);
    for (const branch of rr.retriever.rrf.retrievers) expect(branch.standard.query.bool.filter).toContainEqual({ terms: { doc_type: ["electrical"] } });
    expect(chunks[0]).toMatchObject({ chunk_id: "chunk_desk_drawings_p2_1", score: 1.5, page_image_uri: "/v1/documents/doc_desk_drawings/pages/2.png" });
    expect(es.auth).toEqual(new Set(["ApiKey test-api-key"]));
  });

  it("ES|QL binds the run id as a named parameter, never pasted into the query", async () => {
    const aid = t.app.ctx.store.currentAssembly()!.assembly_id;
    const out = await runEsql(t.app.ctx, "sources_breakdown", aid);
    const sent = es.esql.at(-1);
    expect(sent.params).toEqual([{ assembly_id: aid }]);
    expect(sent.query).toContain("?assembly_id");
    expect(sent.query).not.toContain(aid);
    expect(out).toEqual({ columns: [{ name: "source", type: "keyword" }, { name: "events", type: "long" }], rows: [["seed", 3]] });
  });

  it("indexes each new build event into cutonce-build-events", async () => {
    const aid = t.app.ctx.store.currentAssembly()!.assembly_id;
    const r = await t.app.inject({ method: "POST", url: `/v1/assemblies/${aid}/events`, headers: auth, payload: builtEvent(aid, "part_cable_tray") });
    const eventId = r.json().event.event_id;
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(es.indexed).toContain(`/cutonce-build-events/_doc/${eventId}`);
  });
});

describe("OpenAI on the wire (copilot schema)", () => {
  it("sends the per-question schema as-is with the image, and the answer survives the second check", async () => {
    process.env.OPENAI_BASE_URL = `${oai.url}/v1`;
    try {
      const parts = ["part_power_cable", "part_cable_tray", "part_right_rear_leg"];
      const schema = copilotAnswerSchema(parts, ["chunk_desk_drawings_p2_1"]);
      const out = await jsonCall(t.app.ctx.cfg, { name: "copilot_answer", schema: CopilotAnswer, jsonSchema: schema, system: "s", text: "where does this cable go?",
        images: [{ data: Buffer.from("fake-jpeg"), mime: "image/jpeg" }] });
      const sent = openai.requests.at(-1);
      expect(sent.path).toBe("/v1/chat/completions");
      expect(sent.body.response_format).toEqual({ type: "json_schema", json_schema: { name: "copilot_answer", strict: true, schema } });
      expect(sent.body.messages[1].content[1].image_url.url).toMatch(/^data:image\/jpeg;base64,/);
      expect(keepKnown(out.highlight_parts, parts)).toEqual(["part_power_cable", "part_cable_tray"]);
    } finally { delete process.env.OPENAI_BASE_URL; }
  });
});

describe("pnpm elastic:setup against stand-in Kibana", () => {
  it("creates the four ES|QL tools with 9.4 types and rendered queries, then smoke-tests each (a hung tool times out and fails the run)", async () => {
    const started = Date.now();
    const err = await run(process.execPath, [tsxCli, "src/cli/elastic-setup.ts"], {
      cwd: fileURLToPath(new URL("..", import.meta.url)), timeout: 60_000,
      env: { ...process.env, KIBANA_URL: kib.url, ES_API_KEY: "test-api-key", AGENT_BUILDER_MCP_URL: "", JINA_EMBED_ID: ".jina-embeddings-v5-text-small",
        JINA_RERANK_ID: ".jina-reranker-v3", SMOKE_TIMEOUT_MS: "1000" },
    }).then(() => null, (e) => e);
    expect(err?.code).toBe(1); // exits by itself (no hard exit), with a failing status
    expect(Date.now() - started).toBeLessThan(30_000);
    const stdout: string = err?.stdout ?? "";
    expect(kibana.created.map((c) => c.id).sort()).toEqual(["cutonce_build_history", "cutonce_find_parts", "cutonce_lookup_material", "cutonce_search_documents"]);
    expect(kibana.xsrf.every((x) => x === "true")).toBe(true);
    for (const tool of kibana.created) {
      for (const p of Object.values<any>(tool.configuration.params)) expect(p.type).toBe("string");
      expect(tool.configuration.query).not.toContain("${");
    }
    expect(kibana.created.find((c) => c.id === "cutonce_search_documents").configuration.query).toContain('"inference_id": ".jina-reranker-v3"');
    expect(stdout.match(/^created /gm)).toHaveLength(4);
    expect(stdout).toContain("smoke ok    cutonce_find_parts: 2 rows");
    expect(stdout).toMatch(/smoke FAIL  cutonce_search_documents: timed out after 1000 ms/);
  }, 70_000);

  it("exits non-zero when Kibana rejects a tool, and treats a blank SMOKE_TIMEOUT_MS as the default", async () => {
    kibana.rejectCreate = true; kibana.hangSearch = false;
    try {
      const err = await run(process.execPath, [tsxCli, "src/cli/elastic-setup.ts"], {
        cwd: fileURLToPath(new URL("..", import.meta.url)), timeout: 60_000,
        env: { ...process.env, KIBANA_URL: kib.url, ES_API_KEY: "test-api-key", AGENT_BUILDER_MCP_URL: "", JINA_EMBED_ID: "", JINA_RERANK_ID: "", SMOKE_TIMEOUT_MS: "" },
      }).then(() => null, (e) => e);
      expect(err?.code).toBe(1);
      expect(err?.stdout).toMatch(/FAILED 400 +cutonce_find_parts/);
      expect(err?.stdout).toContain("up to 15000 ms each");
      expect(err?.stdout).toMatch(/smoke ok    cutonce_search_documents: 1 rows/);
    } finally { kibana.rejectCreate = false; kibana.hangSearch = true; }
  }, 70_000);
});
