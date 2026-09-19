import { existsSync, readFileSync, rmSync } from "node:fs";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Strict, type CopilotContext, type WsMessage } from "@cutonce/schemas";
import { REPO_ROOT } from "../src/config.js";
import type { KitBuildContext } from "../src/build/session.js";
import type { KitTurn } from "../src/copilot/kit.js";
import { twin } from "./build-synth.js";
import { auth, makeApp } from "./helpers.js";

// The two calls that need a key and a network. Everything between them is the real pipeline.
const transcribe = vi.hoisted(() => vi.fn());
const ask = vi.hoisted(() => vi.fn());
vi.mock("../src/copilot/stt.js", () => ({ transcribe }));
vi.mock("../src/copilot/answer.js", async (importOriginal) => ({ ...(await importOriginal<object>()), ask }));
const routeTurn = vi.hoisted(() => vi.fn());
vi.mock("../src/copilot/router.js", async (importOriginal) => ({ ...(await importOriginal<object>()), routeTurn }));
const runKitTurn = vi.hoisted(() => vi.fn());
vi.mock("../src/copilot/kit.js", async (importOriginal) => ({ ...(await importOriginal<object>()), runKitTurn }));

const FIXTURES = join(REPO_ROOT, "data", "fixtures");
const frame = () => readFileSync(join(FIXTURES, "frame_0001.jpg"));
const baseContext = () => Strict.CopilotContext.parse(JSON.parse(readFileSync(join(FIXTURES, "context_packet.json"), "utf8"))) as CopilotContext;

let t: Awaited<ReturnType<typeof makeApp>>;
beforeEach(async () => {
  // The real cap is 9 s. Shortening it here keeps the two hard-cap tests honest without making the suite slow.
  process.env.COPILOT_CAP_MS = "1500";
  t = await makeApp({ elevenKey: "", openaiKey: "test-key", copilotMode: "live" });
  transcribe.mockReset();
  ask.mockReset();
  routeTurn.mockReset(); routeTurn.mockResolvedValue(null);
  runKitTurn.mockReset();
});
afterEach(async () => { await t.cleanup(); });

const aid = () => t.app.ctx.store.currentAssembly()!.assembly_id;

function multipart(parts: { name: string; value: string | Buffer; filename?: string; type?: string }[]) {
  const boundary = "----cutoncepipeline";
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

const query = (over: Partial<CopilotContext> = {}) => t.app.inject({
  method: "POST", url: `/v1/assemblies/${aid()}/copilot/query`,
  ...multipart([
    { name: "context", value: JSON.stringify({ ...baseContext(), assembly_id: aid(), ...over }) },
    { name: "audio", value: Buffer.from("RIFFfake"), filename: "a.wav", type: "audio/wav" },
    { name: "frame", value: frame(), filename: "f.jpg", type: "image/jpeg" },
  ]),
});

const draft = (over: object = {}) => ({
  draft: {
    answer_text: "Run it through the cable tray to the right rear leg.", highlight_parts: ["part_cable_tray", "part_right_rear_leg"],
    highlight_style: "path", chunk_ids: [], action: null, confidence: 0.9, needs_clarification: false, ...over,
  },
  toolCalls: [],
});

describe("a spoken command", () => {
  it("skips the model, writes the event itself and reports the action", async () => {
    transcribe.mockResolvedValue("done");
    const before = t.app.ctx.store.getState(aid()).parts.part_cable_tray!.state;
    expect(before).toBe("missing");

    const r = await query({ selected_part_id: "part_cable_tray" });
    const body = r.json();

    expect(ask).not.toHaveBeenCalled();
    expect(body.action).toEqual({ type: "mark_state", part_ids: ["part_cable_tray"], new_state: "built", source: "voice" });
    expect(body.highlight_parts).toEqual(["part_cable_tray"]);
    expect(body.timings_ms.fast_path).toBe(1);
    // The server owns the log, so the part is already built by the time the headset hears the answer.
    expect(t.app.ctx.store.getState(aid()).parts.part_cable_tray!.state).toBe("built");
  });

  it("writes an event with source `voice`, so the history shows how it was marked", async () => {
    transcribe.mockResolvedValue("mark the left rear leg built");
    await query();
    const { events } = t.app.ctx.store.getEvents(aid());
    expect(events.at(-1)).toMatchObject({ part_id: "part_left_rear_leg", new_state: "built", source: "voice" });
  });

  it("never double-writes: saying it twice is a no-op the second time", async () => {
    transcribe.mockResolvedValue("mark the left rear leg built");
    await query();
    const head = t.app.ctx.store.getEvents(aid()).head;
    await query();
    expect(t.app.ctx.store.getEvents(aid()).head).toBe(head);
  });
});

describe("a question", () => {
  it("runs the full pipeline, grounds the answer and times every stage", async () => {
    transcribe.mockResolvedValue("where does this cable go");
    ask.mockResolvedValue(draft());

    const body = (await query({ selected_part_id: "part_power_cable" })).json();

    expect(body.transcript).toBe("where does this cable go");
    expect(body.answer_text).toContain("cable tray");
    expect(body.highlight_parts).toEqual(["part_cable_tray", "part_right_rear_leg"]);
    expect(body.highlight_style).toBe("path");
    expect(body.turn_id).toMatch(/^turn_[a-z0-9_]+$/);
    expect(body.audio_url).toBe(`/v1/audio/${body.turn_id}`);
    expect(body.cached).toBe(false);
    expect(Object.keys(body.timings_ms)).toEqual(expect.arrayContaining(["upload", "stt", "retrieve", "annotate", "llm", "total_to_response"]));
  });

  it("sends the model an annotated frame and a raw one, with the legend in the prompt", async () => {
    transcribe.mockResolvedValue("what goes here");
    ask.mockResolvedValue(draft());
    await query();

    const call = ask.mock.calls[0]![2] as { frames: { annotated: Buffer | null; raw: Buffer }; legend: { part_id: string }[] };
    expect(call.frames.annotated).toBeInstanceOf(Buffer);
    expect(call.frames.raw.length).toBe(frame().length);
    expect(call.legend.map((l) => l.part_id)).toContain("part_cable_tray");
  });

  it("puts the turn on the Director page's stream", async () => {
    transcribe.mockResolvedValue("what goes here");
    ask.mockResolvedValue(draft());
    const seen: WsMessage[] = [];
    t.app.ctx.store.bus.on("broadcast", (m) => seen.push(m));

    await query();
    const turn = seen.find((m) => m.type === "copilot_turn");
    expect(turn).toBeTruthy();
    expect((turn as { turn: Record<string, unknown> }).turn.answer_text).toContain("cable tray");
  });

  it("keeps the last turns so a follow-up has context", async () => {
    transcribe.mockResolvedValue("where does this cable go");
    ask.mockResolvedValue(draft());
    await query();
    transcribe.mockResolvedValue("and after that");
    await query();

    const second = ask.mock.calls[1]![2] as { turns: { transcript: string }[] };
    expect(second.turns.map((h) => h.transcript)).toEqual(["where does this cable go"]);
  });

  it("drops an invented part id before the headset ever sees it", async () => {
    transcribe.mockResolvedValue("what goes here");
    ask.mockResolvedValue(draft({ highlight_parts: ["part_cable_tray", "part_does_not_exist"] }));
    expect((await query()).json().highlight_parts).toEqual(["part_cable_tray"]);
  });

  it("answers out loud when transcription fails, instead of an error", async () => {
    transcribe.mockRejectedValue(new Error("Request timed out."));
    const r = await query();
    expect(r.statusCode).toBe(200);
    expect(r.json()).toMatchObject({ needs_clarification: true, answer_text: "I couldn't hear that. Hold A and ask again." });
    expect(r.json().audio_url).toMatch(/^\/v1\/audio\/turn_/);
    expect(ask).not.toHaveBeenCalled();
  });

  it("answers out loud when nothing was said", async () => {
    transcribe.mockResolvedValue("");
    expect((await query()).json()).toMatchObject({ needs_clarification: true, answer_text: "I didn't catch that. Hold A and ask again." });
  });

  it("still 503s with a readable reason when the OpenAI key is missing: a set-up problem to find at rehearsal", async () => {
    t.app.ctx.cfg.openaiKey = "";
    const r = await query();
    expect(r.statusCode).toBe(503);
    expect(r.json().error.message).toContain("OPENAI_API_KEY");
  });
});

describe("the safety net", () => {
  /** Answers one question for real, then promotes it the way the Director page does during rehearsal. */
  async function promote(scriptedQueryId: string) {
    transcribe.mockResolvedValue("where does the power cable run");
    ask.mockResolvedValue(draft());
    const turnId = (await query()).json().turn_id;
    const r = await t.app.inject({ method: "POST", url: "/v1/director/command", headers: auth, payload: { type: "promote_cache", turn_id: turnId, scripted_query_id: scriptedQueryId } });
    expect(r.statusCode).toBe(200);
  }

  it("a HUD query button replays the rehearsed answer without touching the model", async () => {
    await promote("q_cable");
    ask.mockReset();
    transcribe.mockReset();

    const body = (await query({ scripted_query_id: "q_cable" })).json();
    expect(body.cached).toBe(true);
    expect(body.answer_text).toContain("cable tray");
    expect(transcribe).not.toHaveBeenCalled();
    expect(ask).not.toHaveBeenCalled();
  });

  it("past the hard cap, the same question falls back to its cached answer", async () => {
    await promote("q_cable");
    t.app.ctx.store.bus.removeAllListeners("broadcast");

    transcribe.mockResolvedValue("where does the power cable run");
    ask.mockImplementation(() => new Promise(() => { /* never settles: this is a dead network */ }));

    const body = (await query()).json();
    expect(body.cached).toBe(true);
    expect(body.answer_text).toContain("cable tray");
    expect(body.timings_ms.total_to_response).toBeLessThan(12_000);
  }, 20_000);

  it("with nothing cached, it says so instead of hanging", async () => {
    transcribe.mockResolvedValue("something we never rehearsed");
    ask.mockImplementation(() => new Promise(() => {}));
    const body = (await query()).json();
    expect(body.cached).toBe(false);
    expect(body.timings_ms.capped).toBe(1);
    expect(body.answer_text).toContain("too long");
  }, 20_000);

  it("lists what is in the cache for the Director page", async () => {
    await promote("q_cable");
    const r = await t.app.inject({ method: "GET", url: "/v1/copilot/cache", headers: auth });
    expect(r.json().entries).toMatchObject([{ scripted_query_id: "q_cable", transcript: "where does the power cable run" }]);
  });
});

describe("a blank answer", () => {
  it("is replaced by a short spoken line, so the headset never shows and plays nothing", async () => {
    transcribe.mockResolvedValue("hmm"); ask.mockResolvedValue(draft({ answer_text: "   " }));
    const body = (await query()).json();
    expect(body.answer_text).toBe("I don't have an answer for that. Try asking another way.");
    expect(body.needs_clarification).toBe(true);
  });
});

describe("a question with no camera frame", () => {
  const noFrame = (over: Partial<CopilotContext> = {}, frameBytes: Buffer | null = null) => t.app.inject({
    method: "POST", url: `/v1/assemblies/${aid()}/copilot/query`,
    ...multipart([
      { name: "context", value: JSON.stringify({ ...baseContext(), assembly_id: aid(), visible_parts: [], camera: null, ...over }) },
      { name: "audio", value: Buffer.from("RIFFfake"), filename: "a.wav", type: "audio/wav" },
      ...(frameBytes ? [{ name: "frame", value: frameBytes, filename: "f.jpg", type: "image/jpeg" }] : []),
    ]),
  });

  it("is answered without images when the headset sends no frame at all", async () => {
    transcribe.mockResolvedValue("where does the power cable run"); ask.mockResolvedValue(draft());
    const r = await noFrame();
    expect(r.statusCode).toBe(200);
    expect(ask.mock.calls[0]![2].frames).toEqual({ annotated: null, raw: null });
  });

  it("treats a zero-byte frame the same way", async () => {
    transcribe.mockResolvedValue("where does the power cable run"); ask.mockResolvedValue(draft());
    expect((await noFrame({}, Buffer.alloc(0))).statusCode).toBe(200);
    expect(ask.mock.calls[0]![2].frames).toEqual({ annotated: null, raw: null });
  });

  it("still replays a rehearsed answer from the HUD", async () => {
    transcribe.mockResolvedValue("where does the power cable run"); ask.mockResolvedValue(draft());
    const turnId = (await query()).json().turn_id;
    await t.app.inject({ method: "POST", url: "/v1/director/command", headers: auth, payload: { type: "promote_cache", turn_id: turnId, scripted_query_id: "q_cable" } });
    const body = (await noFrame({ scripted_query_id: "q_cable" })).json();
    expect(body.cached).toBe(true);
    expect(body.answer_text).toContain("cable tray");
  });
});

describe("what may change the build", () => {
  const legBuilt = { type: "mark_state", part_ids: ["part_left_rear_leg"], new_state: "built" };
  const leg = () => t.app.ctx.store.getState(aid()).parts.part_left_rear_leg!.state;

  it("a question never does, whatever the model proposes", async () => {
    transcribe.mockResolvedValue("is the left rear leg in yet?");
    ask.mockResolvedValue(draft({ action: legBuilt, confidence: 0.95 }));
    expect((await query()).json().action).toBeNull();
    expect(leg()).toBe("missing");
  });

  it("an unsure statement does not either", async () => {
    transcribe.mockResolvedValue("I think the left rear leg is on");
    ask.mockResolvedValue(draft({ action: legBuilt, confidence: 0.2, needs_clarification: true }));
    expect((await query()).json().action).toBeNull();
    expect(leg()).toBe("missing");
  });

  it("a sure statement does, and the log records it as the model's call with its confidence", async () => {
    transcribe.mockResolvedValue("the left rear leg is on");
    ask.mockResolvedValue(draft({ action: legBuilt, confidence: 0.9 }));
    expect((await query()).json().action).toMatchObject({ type: "mark_state", part_ids: ["part_left_rear_leg"], new_state: "built" });
    expect(leg()).toBe("built");
    expect(t.app.ctx.store.getEvents(aid()).events.at(-1)).toMatchObject({ source: "voice", confidence: 0.9, note: 'model, from: "the left rear leg is on"' });
  });

  it("a replayed cached answer carries no action, because nothing re-applies it", async () => {
    transcribe.mockResolvedValue("mark the left rear leg built");
    const turnId = (await query()).json().turn_id;
    await t.app.inject({ method: "POST", url: "/v1/director/command", headers: auth, payload: { type: "promote_cache", turn_id: turnId, scripted_query_id: "q_mark" } });
    const body = (await query({ scripted_query_id: "q_mark" })).json();
    expect(body.cached).toBe(true);
    expect(body.action).toBeNull();
  });
});

describe("spoken commands", () => {
  const leg = () => t.app.ctx.store.getState(aid()).parts.part_left_rear_leg!.state;

  it("'undo' twice after one change leaves it undone and says there is nothing more", async () => {
    transcribe.mockResolvedValue("mark the left rear leg built"); await query();
    transcribe.mockResolvedValue("undo");
    await query();
    expect(leg()).toBe("missing");
    expect(t.app.ctx.store.getEvents(aid()).events.at(-1)!.note).toMatch(/^undo of evt_/);
    const second = (await query()).json();
    expect(second.answer_text).toBe("There's nothing to undo.");
    expect(second.action).toBeNull();
    expect(leg()).toBe("missing");
  });

  it("'done' on a part from another plan revision answers instead of failing the turn", async () => {
    transcribe.mockResolvedValue("done");
    const r = await query({ selected_part_id: "part_from_another_revision" });
    expect(r.statusCode).toBe(200);
    expect(r.json()).toMatchObject({ action: null, answer_text: expect.stringContaining("isn't in this plan") });
  });
});

describe("the demo cache", () => {
  it("refuses a scripted id that is not a plain name, so promote cannot write outside the cache folder", async () => {
    transcribe.mockResolvedValue("where does the power cable run"); ask.mockResolvedValue(draft());
    const turnId = (await query()).json().turn_id;
    const escaped = join(t.dataDir, "..", "cutonce_escape.json");
    rmSync(escaped, { force: true }); // a leftover from a run before the fix must not decide this test
    const r = await t.app.inject({ method: "POST", url: "/v1/director/command", headers: auth, payload: { type: "promote_cache", turn_id: turnId, scripted_query_id: "../../cutonce_escape" } });
    expect(r.statusCode).toBe(400);
    expect(existsSync(escaped)).toBe(false);
  });
});


describe("the wish, from the first ask", () => {
  it("'build me a birdhouse' scans at once with the wish, and no model is asked", async () => {
    transcribe.mockResolvedValue("Can you build me a birdhouse?");
    const expect_ = vi.spyOn(t.app.ctx.hooks.build!, "expectScan");
    const body = (await query()).json();
    expect(body.action).toEqual({ type: "start_scan" });
    expect(body.answer_text).toBe("Let me see how to make a birdhouse from what's here.");
    expect(expect_).toHaveBeenCalledWith("a birdhouse", false);
    expect([routeTurn.mock.calls.length, ask.mock.calls.length]).toEqual([0, 0]);
  });
  it("a plain 'what can I build?' clears the wish, and 'scan again' leaves it alone", async () => {
    const expect_ = vi.spyOn(t.app.ctx.hooks.build!, "expectScan");
    transcribe.mockResolvedValue("what can I build");
    await query();
    expect(expect_).toHaveBeenLastCalledWith(null, false);
    expect_.mockClear();
    transcribe.mockResolvedValue("scan again");
    await query();
    expect(expect_).not.toHaveBeenCalled();
  });
  it("the router's 'build ideas' carries its wish, a new ask, and says what it will look for", async () => {
    transcribe.mockResolvedValue("could you come up with something to hold my phone");
    routeTurn.mockResolvedValue({ flow: "build_ideas", confidence: 0.9, wish: "something to hold my phone." });
    const expect_ = vi.spyOn(t.app.ctx.hooks.build!, "expectScan");
    const body = (await query()).json();
    expect([body.action, body.answer_text]).toEqual([{ type: "start_scan" }, "Let me see how to make something to hold my phone from what's here."]);
    expect(expect_).toHaveBeenCalledWith("something to hold my phone", false);
  });
});

describe("outside build mode: the router", () => {
  it("'what can I build' is instant: start_scan, no router and no answer model", async () => {
    transcribe.mockResolvedValue("what can I build");
    const body = (await query()).json();
    expect(body.action).toEqual({ type: "start_scan" });
    expect(ask).not.toHaveBeenCalled();
    expect(routeTurn).not.toHaveBeenCalled();
  });
  it("the router's build_ideas starts a scan without the answer model", async () => {
    transcribe.mockResolvedValue("could I make something useful out of all this junk");
    routeTurn.mockResolvedValue({ flow: "build_ideas", confidence: 0.9 });
    const body = (await query()).json();
    expect(body.action).toEqual({ type: "start_scan" });
    expect(ask).not.toHaveBeenCalled();
  });
  it("an unsure router changes nothing: a question is answered as it always was", async () => {
    transcribe.mockResolvedValue("can we move this bracket up");
    routeTurn.mockResolvedValue({ flow: "build_ideas", confidence: 0.6 });
    ask.mockResolvedValue(draft());
    const body = (await query({ mode: "overlay" })).json();
    expect([body.answer_text, body.needs_clarification]).toEqual(["Run it through the cable tray to the right rear leg.", false]);
  });
  it("a question still goes to the answer model, and Kit's turn is never used", async () => {
    transcribe.mockResolvedValue("where does this cable go");
    routeTurn.mockResolvedValue({ flow: "question", confidence: 0.95 });
    ask.mockResolvedValue(draft());
    expect((await query()).json().answer_text).toMatch(/cable tray/);
    expect(runKitTurn).not.toHaveBeenCalled();
  });
  it("a router that failed or ran out of time is a question: the copilot never goes quiet because the small model did", async () => {
    transcribe.mockResolvedValue("where does this cable go");
    routeTurn.mockResolvedValue(null);
    ask.mockResolvedValue(draft());
    const body = (await query()).json();
    expect(body.answer_text).toMatch(/cable tray/);
    expect(body.timings_ms.route).toBeGreaterThanOrEqual(0);
  });
});

describe("build mode: Kit's turn", () => {
  const ideas = [
    { idea_id: "idea_a", title: "Birdhouse", why: "Birds need homes.", uses: ["o1", "o2"], steps: 2 },
    { idea_id: "idea_b", title: "Robot", why: "Beep.", uses: ["o1"], steps: 1 },
    { idea_id: "idea_c", title: "Can tower", why: "Tall.", uses: ["o1"], steps: 1 },
  ];
  const table = (over: Partial<KitBuildContext> = {}): KitBuildContext => ({
    status: "showing 3 designs", surfaces: [], camera: null, wish: null, started: null, tape: false, ideas,
    twins: [twin({ twin_id: "o1", name: "tall_can", label: "tall can" }), twin({ twin_id: "o2", name: "pizza_box", label: "pizza box" })], ...over,
  });
  // These tests run on OpenAI (no OMNI key): the pipeline transcribes first, and what Kit heard is the transcript.
  const kitHears = (over: Partial<KitTurn> = {}) => {
    transcribe.mockResolvedValue(over.heard ?? "something");
    return runKitTurn.mockResolvedValue({
      kit: { heard: "something", intent: "question", wish: null, pick: null, answer: "It holds weight.", objects: [], confidence: 0.9, ...over }, sttMs: null, modelMs: 34,
    });
  };
  const build = () => t.app.ctx.hooks.build!;
  const onTable = (over: Partial<KitBuildContext> = {}) => vi.spyOn(build(), "kitContext").mockReturnValue(table(over));

  it("answers a question with the objects it is about, and neither the router nor the answer model is asked", async () => {
    onTable();
    kitHears({ heard: "can the box hold my laptop", answer: "Yes, the pizza box on your left can.", objects: ["o2", "o9"] });
    const body = (await query({ mode: "build" })).json();
    expect([body.answer_text, body.highlight_twins, body.action, body.needs_clarification]).toEqual(["Yes, the pizza box on your left can.", ["o2"], null, false]);
    expect(body.transcript).toBe("can the box hold my laptop");
    expect(body.timings_ms).toMatchObject({ kit: 34, stt: expect.any(Number), kit_openai: 1 });
    expect([transcribe.mock.calls.length, routeTurn.mock.calls.length, ask.mock.calls.length]).toEqual([1, 0, 0]);
    const call = runKitTurn.mock.calls[0]![0];
    expect([call.ai.provider, call.context.status, call.building, call.audio.length > 0, call.transcript]).toEqual(["openai", "showing 3 designs", null, true, "can the box hold my laptop"]);
  });

  it("on OpenAI, a voice command said outright is answered from the transcript: Kit's model is not called", async () => {
    onTable();
    transcribe.mockResolvedValue("Next step");
    const body = (await query({ mode: "build" })).json();
    expect([body.action, body.answer_text]).toEqual([{ type: "step_nav", direction: "next" }, "Next step."]);
    expect(body.timings_ms).toMatchObject({ fast_path: 1, kit_openai: 1 });
    expect(runKitTurn).not.toHaveBeenCalled();
  });

  it("on OpenAI, a transcription that fails or hears nothing is said out loud, with no Kit call", async () => {
    onTable();
    transcribe.mockRejectedValue(new Error("Request timed out."));
    expect((await query({ mode: "build" })).json().answer_text).toBe("I couldn't hear that. Hold A and ask again.");
    transcribe.mockResolvedValue("");
    expect((await query({ mode: "build" })).json().answer_text).toBe("I didn't catch that. Hold A and ask again.");
    expect(runKitTurn).not.toHaveBeenCalled();
  });

  it("a typed question needs no voice clip: Kit gets the words as what was said", async () => {
    onTable();
    kitHears({ heard: "will the box hold a mug", answer: "Yes, the pizza box can." });
    const res = await t.app.inject({
      method: "POST", url: `/v1/assemblies/${aid()}/copilot/query`,
      ...multipart([
        { name: "context", value: JSON.stringify({ ...baseContext(), assembly_id: aid(), mode: "build" }) },
        { name: "question", value: "  will the box hold a mug " },
        { name: "frame", value: frame(), filename: "f.jpg", type: "image/jpeg" },
      ]),
    });
    expect([res.statusCode, res.json().answer_text]).toEqual([200, "Yes, the pizza box can."]);
    const call = runKitTurn.mock.calls[0]![0];
    expect([call.transcript, call.audio.length, transcribe.mock.calls.length]).toEqual(["will the box hold a mug", 0, 0]);
  });

  it("a wish with nothing known yet starts a scan that carries it", async () => {
    onTable({ twins: [], ideas: [], status: "nothing scanned yet" });
    const expect_ = vi.spyOn(build(), "expectScan");
    kitHears({ heard: "I would love something birds could live in", intent: "ideas", wish: "a birdhouse", answer: "" });
    const body = (await query({ mode: "build" })).json();
    expect([body.action, body.answer_text]).toEqual([{ type: "start_scan" }, "Let me see how to make a birdhouse from what's here."]);
    expect(expect_).toHaveBeenCalledWith("a birdhouse", false);
  });

  it("a wish with the objects already known rethinks them, with no new scan to wait for", async () => {
    onTable();
    vi.spyOn(build(), "canRethink").mockReturnValue(true);
    const rethink = vi.spyOn(build(), "rethink").mockResolvedValue(true);
    kitHears({ heard: "what about a little house for birds", intent: "ideas", wish: "a birdhouse", answer: "Ooh, a birdhouse. Let me think." });
    const body = (await query({ mode: "build" })).json();
    expect([body.action, body.answer_text]).toEqual([null, "Ooh, a birdhouse. Let me think."]);
    expect(rethink).toHaveBeenCalledWith("a birdhouse", false);
  });

  it("a wish said while a scan is being read goes with that scan: no second scan is asked for", async () => {
    onTable({ status: "scanning: finding and naming the objects" });
    vi.spyOn(build(), "canRethink").mockReturnValue(false);
    const expect_ = vi.spyOn(build(), "expectScan").mockReturnValue(true);
    kitHears({ heard: "I'd love a birdhouse", intent: "ideas", wish: "a birdhouse", answer: "" });
    expect((await query({ mode: "build" })).json()).toMatchObject({ action: null, answer_text: "Let me see how to make a birdhouse from what's here." });
    kitHears({ heard: "Build me a boat" });
    expect((await query({ mode: "build" })).json()).toMatchObject({ action: null, answer_text: "Let me see how to make a boat from what's here." });
    expect(expect_.mock.calls).toEqual([["a birdhouse", false], ["a boat", false]]);
  });

  it("'make me something crazier' said outright changes the designs on show: those shown are not offered again", async () => {
    onTable();
    vi.spyOn(build(), "canRethink").mockReturnValue(true);
    const rethink = vi.spyOn(build(), "rethink").mockResolvedValue(true);
    kitHears({ heard: "Make me something crazier", intent: "ideas", wish: "something crazier", answer: "" });
    const body = (await query({ mode: "build" })).json();
    expect([body.action, body.answer_text]).toEqual([null, "Let me see how to make something crazier from what's here."]);
    expect(rethink).toHaveBeenCalledWith("something crazier", true);
  });

  it("mid-build, 'something crazier' looks at the table again with that wish (the pieces have moved)", async () => {
    onTable({ started: "idea_a", ideas: [], status: "a design is being built" });
    vi.spyOn(build(), "canRethink").mockReturnValue(false);
    const expect_ = vi.spyOn(build(), "expectScan");
    kitHears({ heard: "now something crazier", intent: "change", wish: "something crazier", answer: "" });
    const body = (await query({ mode: "build" })).json();
    expect([body.action, body.answer_text]).toEqual([{ type: "start_scan" }, "Let me look again with that in mind."]);
    expect(expect_).toHaveBeenCalledWith("something crazier", true);
  });

  it("picks a design on show by where it stands, as the trigger would", async () => {
    onTable();
    const start = vi.spyOn(build(), "startIdea").mockResolvedValue({});
    kitHears({ heard: "the one on the right", intent: "pick", pick: null });
    expect((await query({ mode: "build" })).json().answer_text).toBe("Building the can tower. Watch the pieces.");
    expect(start).toHaveBeenCalledWith("idea_c");
  });

  it("a design on show named outright is picked, even in the words of a wish ('let's build a birdhouse'), with no model call", async () => {
    onTable();
    const start = vi.spyOn(build(), "startIdea").mockResolvedValue({});
    const rethink = vi.spyOn(build(), "rethink");
    kitHears({ heard: "Let's build a birdhouse", intent: "ideas", wish: "a birdhouse" });
    expect((await query({ mode: "build" })).json().answer_text).toBe("Building the birdhouse. Watch the pieces.");
    expect([start.mock.calls, rethink.mock.calls.length, runKitTurn.mock.calls.length]).toEqual([[["idea_a"]], 0, 0]);
  });

  it("on OMNI too, a design on show named in the words of a wish is picked, not designed again", async () => {
    const old = t;
    t = await makeApp({ elevenKey: "", openaiKey: "", omniKey: "q", omniBaseUrl: "http://127.0.0.1:9/v1", copilotMode: "live" });
    try {
      onTable();
      const start = vi.spyOn(build(), "startIdea").mockResolvedValue({});
      runKitTurn.mockResolvedValue({ kit: { heard: "Build me a robot", intent: "ideas", wish: "a robot", pick: null, answer: "", objects: [], confidence: 0.9 }, sttMs: null, modelMs: 40 });
      expect((await query({ mode: "build" })).json().answer_text).toBe("Building the robot. Watch the pieces.");
      expect(start).toHaveBeenCalledWith("idea_b");
    } finally { await t.cleanup(); t = old; }
  });

  it("a picked build that cannot be started is said out loud, not a failed turn", async () => {
    onTable();
    vi.spyOn(build(), "startIdea").mockRejectedValue(new Error("ENOSPC: no space left on device"));
    kitHears({ heard: "the birdhouse please", intent: "pick", pick: "idea_a" });
    const r = await query({ mode: "build" });
    expect([r.statusCode, r.json().answer_text, r.json().needs_clarification]).toEqual([200, "I couldn't start that build. Try again.", true]);
  });

  it("voice commands said outright are exact, whatever the model made of them", async () => {
    onTable();
    const expect_ = vi.spyOn(build(), "expectScan");
    kitHears({ heard: "What can I build?", intent: "question", answer: "Lots of things!" });
    const body = (await query({ mode: "build" })).json();
    expect([body.action, body.answer_text]).toEqual([{ type: "start_scan" }, "Let me see what you've got."]);
    expect(expect_).toHaveBeenCalledWith(null, false);
  });

  it("'done' is the step command; with no build under way there is no step, and Kit says so", async () => {
    onTable();
    kitHears({ heard: "I'm finished with that", intent: "done", answer: "Marked it done!", confidence: 0.95 });
    // Nothing is pointed at: the hologram is hidden while designs are chosen, so the headset sends no selection.
    // The model's "Marked it done!" is never said: nothing was marked.
    const body = (await query({ mode: "build", selected_part_id: null, selection_source: "none" })).json();
    expect([body.action, body.answer_text, body.needs_clarification]).toEqual([null, "There's no step to do that to yet.", true]);
  });

  it("says 'I didn't catch that' when nothing was heard", async () => {
    onTable();
    kitHears({ heard: "  ", intent: "unclear", answer: "" });
    expect((await query({ mode: "build" })).json().answer_text).toBe("I didn't catch that. Hold A and ask again.");
  });

  it("tries OMNI first and, when it fails fast, OpenAI once, inside the hard cap", async () => {
    const old = t;
    process.env.COPILOT_CAP_MS = "9000";                                   // the real cap: this file shortens it for its other tests
    t = await makeApp({ elevenKey: "", openaiKey: "test-key", omniKey: "q", omniBaseUrl: "http://127.0.0.1:9/v1", copilotMode: "live" });
    try {
      onTable();
      transcribe.mockResolvedValue("hi");                                        // heard alongside, and reused by the fallback
      runKitTurn.mockRejectedValueOnce(new Error("401 Unauthorized"))
        .mockResolvedValueOnce({ kit: { heard: "hi", intent: "question", wish: null, pick: null, answer: "Hello!", objects: [], confidence: 0.9 }, sttMs: 20, modelMs: 40 });
      const body = (await query({ mode: "build" })).json();
      expect(body.answer_text).toBe("Hello!");
      expect(runKitTurn.mock.calls.map((c) => c[0].ai.provider)).toEqual(["omni", "openai"]);
      expect(body.timings_ms).toMatchObject({ kit_openai: 1 });
    } finally { await t.cleanup(); t = old; }
  });

  const omniToo = (over: object = {}) => makeApp({ elevenKey: "", openaiKey: "test-key", omniKey: "q", omniBaseUrl: "http://127.0.0.1:9/v1", copilotMode: "live", ...over });
  const answered = (answer: string) => ({ kit: { heard: "which piece goes first", intent: "question", wish: null, pick: null, answer, objects: [], confidence: 0.9 }, sttMs: null, modelMs: 30 });

  it("on OMNI with OpenAI too, a command said outright answers from the transcript: a stalled OMNI call cannot hold 'next' up", async () => {
    const old = t;
    t = await omniToo();
    try {
      onTable();
      transcribe.mockResolvedValue("next");
      runKitTurn.mockReturnValue(new Promise(() => {}));                      // OMNI never answers
      const started = Date.now();
      const body = (await query({ mode: "build" })).json();
      expect([body.action, body.answer_text]).toEqual([{ type: "step_nav", direction: "next" }, "Next step."]);
      expect(Date.now() - started).toBeLessThan(1000);
      expect(runKitTurn.mock.calls.map((c) => c[0].ai.provider)).toEqual(["omni"]);
    } finally { await t.cleanup(); t = old; }
  });

  it("on OMNI, anything but a command waits for what OMNI heard; with no OpenAI key nothing is transcribed alongside", async () => {
    const old = t;
    t = await omniToo();
    try {
      onTable();
      transcribe.mockResolvedValue("which piece goes first");
      runKitTurn.mockResolvedValue(answered("The can goes first."));
      expect((await query({ mode: "build" })).json()).toMatchObject({ answer_text: "The can goes first.", timings_ms: { kit_omni: 1 } });
    } finally { await t.cleanup(); t = old; }
    t = await omniToo({ openaiKey: "" });
    try {
      onTable();
      transcribe.mockClear();
      runKitTurn.mockResolvedValue(answered("The can goes first."));
      expect((await query({ mode: "build" })).json().answer_text).toBe("The can goes first.");
      expect(transcribe).not.toHaveBeenCalled();
    } finally { await t.cleanup(); t = old; }
  });

  it("a stalled OMNI call with too little of the cap left says 'ask me again', and tries nothing else", async () => {
    const old = t;
    t = await omniToo({ kitTurnMs: 200 });                                    // this file's cap is 1.5 s: no room for a fallback
    try {
      onTable();
      transcribe.mockResolvedValue("which piece goes first");
      runKitTurn.mockReturnValue(new Promise(() => {}));
      const started = Date.now();
      const body = (await query({ mode: "build" })).json();
      expect([body.answer_text, body.needs_clarification]).toEqual(["That took too long. Ask me again.", true]);
      expect(Date.now() - started).toBeLessThan(3000);                     // the model's 1 s floor, not a fallback's wait
      expect(runKitTurn.mock.calls.map((c) => c[0].ai.provider)).toEqual(["omni"]);
    } finally { await t.cleanup(); t = old; }
  });

  it("a stalled OMNI call with time left falls back to OpenAI once, reusing the transcript", async () => {
    const old = t;
    process.env.COPILOT_CAP_MS = "9000";
    t = await omniToo({ kitTurnMs: 200 });
    try {
      onTable();
      transcribe.mockResolvedValue("which piece goes first");
      runKitTurn.mockReturnValueOnce(new Promise(() => {})).mockResolvedValueOnce(answered("The can goes first."));
      const body = (await query({ mode: "build" })).json();
      expect([body.answer_text, body.timings_ms.kit_openai]).toEqual(["The can goes first.", 1]);
      expect(runKitTurn.mock.calls.map((c) => [c[0].ai.provider, c[0].transcript])).toEqual([["omni", undefined], ["openai", "which piece goes first"]]);
      expect(transcribe).toHaveBeenCalledTimes(1);
    } finally { await t.cleanup(); t = old; }
  });

  it("never gives Kit's model more than the hard cap has left, whatever KIT_TURN_MS says", async () => {
    const old = t;
    t = await omniToo({ openaiKey: "", kitTurnMs: 20_000 });                  // this file's cap is 1.5 s
    try {
      onTable();
      runKitTurn.mockReturnValue(new Promise(() => {}));
      const started = Date.now();
      expect((await query({ mode: "build" })).json().answer_text).toBe("That took too long. Ask me again.");
      expect(Date.now() - started).toBeLessThan(2500);
      expect(runKitTurn.mock.calls[0]![0].timeoutMs).toBeLessThanOrEqual(1500);
    } finally { await t.cleanup(); t = old; }
  });

  it("with no provider at all, build mode answers 503 like everything else: a set-up problem", async () => {
    const old = t;
    t = await makeApp({ elevenKey: "", openaiKey: "", copilotMode: "live" });
    try {
      expect((await query({ mode: "build" })).statusCode).toBe(503);
      expect(runKitTurn).not.toHaveBeenCalled();
    } finally { await t.cleanup(); t = old; }
  });
});
