// The Qwen client over real HTTP against a stand-in OpenAI-compatible server that streams, as yibuapi's does.
import { execFile } from "node:child_process";
import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { promisify } from "node:util";
import { afterAll, beforeAll, beforeEach, describe, expect, it } from "vitest";
import { z } from "zod";
import { loadConfig } from "../src/config.js";
import { jsonCall } from "../src/llm.js";
import { jsonObjects, omniJsonCall } from "../src/omni.js";

type Body = { model: string; stream: boolean; modalities?: string[]; messages: { role: string; content: unknown }[] };
const seen: Body[] = [];
/** What the stand-in says next: text chunks to stream, or "hang" to stream one chunk and never finish. */
let replies: (string[] | "hang")[] = [];
const hanging = new Set<ServerResponse>();

const chunk = (content: string | null, finish: string | null = null) =>
  `data: ${JSON.stringify({ id: "c1", object: "chat.completion.chunk", created: 0, model: "stand-in", choices: [{ index: 0, delta: content === null ? {} : { content }, finish_reason: finish }] })}\n\n`;

async function handle(req: IncomingMessage, res: ServerResponse) {
  const raw = await new Promise<string>((resolve) => { let b = ""; req.on("data", (d) => (b += d)); req.on("end", () => resolve(b)); });
  const body = JSON.parse(raw) as Body;
  seen.push(body);
  const system = String(body.messages[0]?.content ?? "");
  // The probe's checks, answered by what their prompts ask for.
  const next = replies.shift() ?? (system.startsWith("You are Kit") ? ['{"heard":"","intent":"unclear","wish":null,"pick":null,"answer":"Say that again?","objects":[],"confidence":0.2}']
    : system.includes("\"hello\"") ? ['{"hello":"hi","model":"stand-in"}']
    : system.includes("\"objects\"") ? ['{"objects":["table","box"]}'] : system.includes("\"heard\"") ? ['{"heard":""}'] : ["{}"]);
  res.writeHead(200, { "content-type": "text/event-stream" });
  if (next === "hang") { res.write(chunk("{\"a\":")); hanging.add(res); return; }
  for (const c of next) res.write(chunk(c));
  res.write(chunk(null, "stop"));
  res.end("data: [DONE]\n\n");
}

let server: Server, baseUrl = "";
beforeAll(async () => {
  server = createServer((q, s) => void handle(q, s));
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  baseUrl = `http://127.0.0.1:${(server.address() as AddressInfo).port}/v1`;
});
afterAll(() => { for (const r of hanging) r.destroy(); server.close(); });
beforeEach(() => { seen.length = 0; replies = []; });

const cfg = (over: object = {}) => loadConfig({}, { omniKey: "k", omniBaseUrl: baseUrl, ...over });
const A = z.object({ a: z.number() });
const call = (over: object = {}) => ({ name: "t", system: "Be brief.", text: "Give a.", schema: A, timeoutMs: 5000, ...over });

describe("jsonObjects", () => {
  it("finds the object inside fences or prose", () => {
    expect(jsonObjects('```json\n{"a": 1}\n```')).toEqual([{ a: 1 }]);
    expect(jsonObjects('Sure! {"a": 2} Hope that helps.')).toEqual([{ a: 2 }]);
  });
  it("finds each object among thinking, braces in prose and in strings, last first; none when there is none", () => {
    expect(jsonObjects('<think>maybe {"a": 0}? a {set} of {"b": "}"}</think>\n{"a": 3, "s": "x{y}"}')).toEqual([{ a: 3, s: "x{y}" }, { b: "}" }, { a: 0 }]);
    expect(jsonObjects('{"a": 1}{"a": 2}')).toEqual([{ a: 2 }, { a: 1 }]);
    expect([jsonObjects("no idea"), jsonObjects('{"a": }')]).toEqual([[], []]);
  });
});

describe("omniJsonCall", () => {
  it("streams the reply, pulls the JSON out of fences split across chunks, and checks it", async () => {
    replies = [["```json\n{\"a\":", " 7}", "\n```"]];
    expect(await omniJsonCall(cfg(), call())).toEqual({ a: 7 });
    expect(seen[0]).toMatchObject({ model: "qwen3.5-omni-flash", stream: true, modalities: ["text"] });
    const system = String(seen[0]!.messages[0]!.content);
    expect(system).toMatch(/^Be brief\.\n\nReply with ONE JSON object/);
    expect(system).toContain('"a":{"type":"number"}');                     // the schema travels in the prompt
  });

  it("sends photos as data URLs and the voice clip as input_audio: a data URL by default, bare base64 when told", async () => {
    const photo = Buffer.from([0xff, 0xd8, 0xff]), voice = Buffer.from("RIFFxxxxWAVE");
    replies = [['{"a":1}'], ['{"a":1}']];
    await omniJsonCall(cfg(), call({ model: "qwen3.5-omni-plus", images: [{ data: photo, mime: "image/jpeg" }], audio: { data: voice, format: "wav" } }));
    await omniJsonCall(cfg({ omniAudio: "base64" }), call({ audio: { data: voice, format: "wav" } }));
    const parts = seen[0]!.messages[1]!.content as { type: string; image_url?: { url: string }; input_audio?: { data: string; format: string } }[];
    expect(seen[0]!.model).toBe("qwen3.5-omni-plus");
    expect(parts.map((p) => p.type)).toEqual(["text", "image_url", "input_audio"]);
    expect(parts[1]!.image_url!.url).toBe(`data:image/jpeg;base64,${photo.toString("base64")}`);
    expect(parts[2]!.input_audio).toEqual({ data: `data:;base64,${voice.toString("base64")}`, format: "wav" });
    const bare = (seen[1]!.messages[1]!.content as { input_audio?: { data: string } }[]).find((p) => p.input_audio)!;
    expect(bare.input_audio!.data).toBe(voice.toString("base64"));
  });

  it("asks once more, with the reason, when the reply is not valid", async () => {
    replies = [["I think a is seven"], ['{"a":7}']];
    expect(await omniJsonCall(cfg(), call())).toEqual({ a: 7 });
    expect(seen).toHaveLength(2);
    const retry = seen[1]!.messages;
    expect(retry.slice(-2)).toEqual([
      { role: "assistant", content: "I think a is seven" },
      { role: "user", content: "That reply was not valid (no JSON object in the reply). Reply again with the JSON object only." },
    ]);
  });

  it("gives up after the second invalid reply, saying what was wrong", async () => {
    replies = [['{"a":"x"}'], ['{"a":"y"}']];
    await expect(omniJsonCall(cfg(), call())).rejects.toThrow(/did not return valid JSON: a: Expected number, received string/);
    expect(seen).toHaveLength(2);
  });

  it("stops at its deadline even while the reply is still streaming, and says it ran out of time", async () => {
    replies = ["hang"];
    const t0 = Date.now();
    await expect(omniJsonCall(cfg(), call({ timeoutMs: 300 }))).rejects.toThrow(/^the OMNI model ran out of time \(300 ms\)$/);
    expect(Date.now() - t0).toBeLessThan(2000);
    expect(seen).toHaveLength(1);
  });

  it("takes the answer from a reply with thinking or an example before it, with no second call", async () => {
    replies = [['<think>They want {"a": "some number"}, say ', '{"a": 1}.</think>\n', '{"a": 5}']];
    expect(await omniJsonCall(cfg(), call())).toEqual({ a: 5 });
    expect(seen).toHaveLength(1);
  });

  it("refuses without a key or a base URL, and calls nothing", async () => {
    await expect(omniJsonCall(cfg({ omniKey: "" }), call())).rejects.toThrow(/OMNI_API_KEY and OMNI_BASE_URL/);
    await expect(omniJsonCall(cfg({ omniBaseUrl: "" }), call())).rejects.toThrow(/OMNI_API_KEY and OMNI_BASE_URL/);
    expect(seen).toHaveLength(0);
  });
});

describe("jsonCall (OpenAI)", () => {
  it("refuses a voice clip: OpenAI's path transcribes first", async () =>
    await expect(jsonCall(loadConfig({}, { openaiKey: "k" }), call({ audio: { data: Buffer.from("x"), format: "wav" } }))).rejects.toThrow(/transcribe/));
});

describe("pnpm omni:probe", () => {
  const run = promisify(execFile);
  const probe = (env: Record<string, string>) => run("pnpm", ["exec", "tsx", "src/cli/omni-probe.ts"], {
    cwd: new URL("..", import.meta.url).pathname, timeout: 60_000, env: { ...process.env, OMNI_API_KEY: "", OMNI_BASE_URL: "", ...env },
  }).then((o) => ({ code: 0, stdout: o.stdout }), (e: { code: number; stdout: string }) => ({ code: e.code, stdout: e.stdout }));

  it("checks text, a photo and a voice clip in both encodings, and passes against a working server", async () => {
    const out = await probe({ OMNI_API_KEY: "k", OMNI_BASE_URL: baseUrl });
    expect(out.stdout).toMatch(/^PASS {2}text/m);
    expect(out.stdout).toMatch(/^PASS {2}photo .*table, box/m);
    expect(out.stdout).toMatch(/^PASS {2}voice \(dataurl\)/m);
    expect(out.stdout).toMatch(/^PASS {2}voice \(base64\)/m);
    expect(out.stdout).toMatch(/^PASS {2}kit .*intent unclear \(0\.2\)/m);
    expect(out.code).toBe(0);
    const voices = seen.filter((b) => JSON.stringify(b.messages).includes("input_audio"));
    expect(voices).toHaveLength(3);                                  // two voice checks and the Kit turn
  }, 60_000);

  it("skips everything without a key, and does not fail", async () => {
    const out = await probe({});
    expect(out.stdout.match(/^SKIP /gm)).toHaveLength(5);
    expect(out.code).toBe(0);
  }, 60_000);
});
