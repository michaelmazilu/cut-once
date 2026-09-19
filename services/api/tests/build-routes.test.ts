import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Twin, WsMessage } from "@cutonce/schemas";
import { KIT, CAMERA, photoB64, synthScan } from "./build-synth.js";
import { BuildFiles } from "../src/build/files.js";
import { jsonCall } from "../src/llm.js";
import { pickIdea } from "../src/build/session.js";
import { auth, makeApp } from "./helpers.js";

// The two model calls. Labels: name by shape, like the vision model would for the kit. Ideas: none (rules still apply).
const nameTwins = vi.hoisted(() => vi.fn());
vi.mock("../src/build/label.js", async (orig) => ({ ...(await orig<object>()), nameTwins }));
const byShape = async (_d: unknown, _p: unknown, twins: Twin[]) => ({
  by: "vision" as const,
  twins: twins.map((t) => t.shape.type === "cylinder"
    ? { ...t, name: "tall_can", label: "tall can", material: "metal", load_bearing: true, confidence: 0.9 }
    : { ...t, name: "pizza_box", label: "pizza box", material: "cardboard", load_bearing: true, cuttable: true, confidence: 0.9 }),
});
vi.mock("../src/llm.js", async (orig) => ({ ...(await orig<object>()), jsonCall: vi.fn(async () => ({ ideas: [] })) }));

let t: Awaited<ReturnType<typeof makeApp>>;
let seen: WsMessage[];
beforeEach(async () => {
  nameTwins.mockReset(); nameTwins.mockImplementation(byShape);
  t = await makeApp({ openaiKey: "test-key" });
  seen = [];
  t.app.ctx.store.bus.on("broadcast", (m) => seen.push(m));
});
afterEach(async () => { await t.app.ctx.hooks.build!.idle(); await t.cleanup(); vi.restoreAllMocks(); });   // no scan may still be writing when the folder goes

const kitUpload = () => {
  const scan = synthScan(KIT, CAMERA.cam, CAMERA.lookAt);
  return { device_id: "quest", grid: scan.grid, points_mm: scan.points_mm, hit: scan.hit, camera: scan.camera, photo_b64: photoB64() };
};
const post = (url: string, payload: object) => t.app.inject({ method: "POST", url, headers: auth, payload });

describe("a scan of the kit", () => {
  it("streams outlines, then names, then the laptop riser, then the final list", async () => {
    expect((await post("/v1/build/scans", kitUpload())).statusCode).toBe(202);
    await t.app.ctx.hooks.build!.idle();
    const kinds = seen.map((m) => (m.type === "build_inventory" ? `inventory:${m.inventory.labelled}` : m.type === "build_ideas" ? `ideas:${m.final}` : m.type));
    expect(kinds).toEqual(["inventory:false", "inventory:true", "ideas:true"]);
    const last = seen.at(-1)!;
    expect(nameTwins).toHaveBeenCalledOnce();
    const named = seen.find((m) => m.type === "build_inventory" && m.inventory.labelled);
    // Named by the (stand-in) vision model, so no "by size" note: just what it sees and what it is doing.
    expect(named?.type === "build_inventory" && named.inventory.message).toBe("I see three tall cans and a pizza box. Working out what they could become…");
    expect(last.type === "build_ideas" && last.ideas.map((i) => i.title)).toEqual(["Laptop riser"]);
    expect(last.type === "build_ideas" && last.message).toMatch(/You could build a laptop riser/);
  });

  it("starts the picked idea as a normal run with the build area already built", async () => {
    await post("/v1/build/scans", kitUpload());
    await t.app.ctx.hooks.build!.idle();
    const { ideas } = (await t.app.inject({ method: "GET", url: "/v1/build/sessions/current", headers: auth })).json();
    const r = await post(`/v1/build/ideas/${ideas[0].idea_id}/start`, {});
    expect(r.statusCode).toBe(200);
    const { assembly_id, plan_id } = r.json();
    expect(plan_id).toBe(ideas[0].plan.plan_id);
    const state = t.app.ctx.store.getState(assembly_id);
    expect([state.progress.built, state.progress.total, state.current_step_id]).toEqual([1, 5, "step_02"]);
  });

  it("tells Kit what is on the table: the objects, where the camera stood, the designs on show and what is happening", async () => {
    const build = t.app.ctx.hooks.build!;
    expect(build.kitContext()).toMatchObject({ status: "nothing scanned yet", twins: [], ideas: [], camera: null, started: null, tape: false });
    await post("/v1/build/scans", kitUpload());
    await build.idle();
    const ctx = build.kitContext();
    expect(ctx.status).toBe("showing 1 design");
    expect(ctx.twins.map((q) => q.name).sort()).toEqual(["pizza_box", "tall_can", "tall_can", "tall_can"]);
    expect(ctx.camera?.position).toEqual(CAMERA.cam);
    expect(ctx.ideas).toEqual([expect.objectContaining({ title: "Laptop riser", steps: 4 })]);
    expect([...ctx.ideas[0]!.uses].sort()).toEqual(ctx.twins.map((q) => q.twin_id).sort());
  });

  it("once a design is started, Kit sees none on show and cannot rethink, until the next scan", async () => {
    const build = t.app.ctx.hooks.build!;
    await post("/v1/build/scans", kitUpload());
    await build.idle();
    const [idea] = build.kitContext().ideas;
    await build.startIdea(idea!.idea_id);
    expect(build.kitContext()).toMatchObject({ status: "a design is being built", ideas: [], started: idea!.idea_id });
    expect([build.canRethink(), await build.rethink("make it taller", true)]).toEqual([false, false]);
    const { session_id } = (await t.app.inject({ method: "GET", url: "/v1/build/sessions/current", headers: auth })).json();
    await post("/v1/build/scans", { ...kitUpload(), session_id });
    await build.idle();
    expect(build.kitContext()).toMatchObject({ status: "showing 1 design", started: null });
    expect(build.canRethink()).toBe(true);
  });

  it("replays a saved scan into a new session using its saved labels", async () => {
    const { scan_id } = (await post("/v1/build/scans", kitUpload())).json();
    await t.app.ctx.hooks.build!.idle();
    seen = [];
    const r = await post(`/v1/build/scans/${scan_id}/replay`, { labels: "saved" });
    await t.app.ctx.hooks.build!.idle();
    expect(r.json().session_id).toMatch(/^bsess_/);
    expect(seen.some((m) => m.type === "build_ideas" && m.final && m.ideas.length === 1)).toBe(true);
  });

  it("goes quiet for a session that has been replaced: its late names and ideas would pull the headset back to it", async () => {
    let release = () => {};
    nameTwins.mockImplementationOnce(async (d: unknown, ph: unknown, twins: Twin[]) => { await new Promise<void>((r) => { release = r; }); return byShape(d, ph, twins); });
    const { session_id: old } = (await post("/v1/build/scans", kitUpload())).json();
    await vi.waitFor(() => expect(nameTwins).toHaveBeenCalled());
    const fresh = (await post("/v1/build/sessions", {})).json().session_id;          // the Director's "New session" while names are on their way
    seen = [];
    release();
    await t.app.ctx.hooks.build!.idle();
    expect(fresh).not.toBe(old);
    expect(seen.filter((m) => m.type === "build_inventory" || m.type === "build_ideas")).toEqual([]);
  });

  it("does not lose a scan's names and ideas when its labels cannot be saved (a full disk)", async () => {
    const save = vi.spyOn(BuildFiles.prototype, "saveLabels").mockImplementation(() => { throw new Error("ENOSPC: no space left on device"); });
    await post("/v1/build/scans", kitUpload());
    await t.app.ctx.hooks.build!.idle();
    expect(save).toHaveBeenCalled();
    const last = seen.at(-1)!;
    expect(last.type === "build_ideas" && last.final && last.ideas.map((i) => i.title)).toEqual(["Laptop riser"]);
  });

  it("says in a few words when a scan cannot be read, not in a page of JSON", async () => {
    const { scan_id } = (await post("/v1/build/scans", kitUpload())).json();
    await t.app.ctx.hooks.build!.idle();
    // Saved labels from before sides were floored at 5 mm: a zero side, which the Twin schema refuses.
    const file = join(t.dataDir, "build", "scans", scan_id, "labels.json");
    const twins = JSON.parse(readFileSync(file, "utf8")) as { shape: { size?: number[] } }[];
    twins.find((q) => q.shape.size)!.shape.size![2] = 0;
    writeFileSync(file, JSON.stringify(twins));
    seen = [];
    await post(`/v1/build/scans/${scan_id}/replay`, { labels: "saved" });
    await t.app.ctx.hooks.build!.idle();
    const said = seen.flatMap((m) => (m.type === "build_inventory" && m.inventory.message ? [m.inventory.message] : []));
    expect(said).toEqual(["I couldn't read that scan: its saved data is not in the form I expect."]);
  });

  it("lists the vocabulary, and says which objects have a standard size: only those can be added by hand", async () => {
    const { items } = (await t.app.inject({ method: "GET", url: "/v1/build/vocabulary", headers: auth })).json() as { items: { name: string; label: string; standard: boolean }[] };
    expect(items.find((i) => i.name === "tall_can")).toEqual({ name: "tall_can", label: "tall can", standard: true });
    expect(items.find((i) => i.name === "cardboard_box")?.standard).toBe(false);
    for (const i of items) expect([i.name, (await post("/v1/build/objects", { name: i.name })).statusCode]).toEqual([i.name, i.standard ? 200 : 400]);
  });

  it("adds a missed object from the Director", async () => {
    await post("/v1/build/scans", kitUpload());
    await t.app.ctx.hooks.build!.idle();
    const r = await post("/v1/build/objects", { name: "drink_can" });
    expect(r.json()).toMatchObject({ name: "drink_can", snapped: true });
  });
});

describe("pickIdea: which idea a sentence picks", () => {
  const ideas = [{ title: "Laptop riser" }, { title: "Tall laptop riser" }, { title: "Two-tier display stand" }];
  const picked = (said: string) => pickIdea(said, ideas)?.title ?? null;

  it("takes the name alone or with the words people pick with", () => {
    expect(picked("Laptop riser.")).toBe("Laptop riser");
    expect(picked("Let's build the laptop riser, please")).toBe("Laptop riser");
    expect(picked("Can we do the two tier display stand instead?")).toBe("Two-tier display stand");
    expect(picked("I'd like to make the tall laptop riser")).toBe("Tall laptop riser");
  });
  it("leaves questions and remarks about an idea to the copilot", () => {
    expect(picked("How tall is the laptop riser?")).toBeNull();
    expect(picked("Why does the laptop riser have the cans at the front")).toBeNull();
    expect(picked("the laptop riser is wobbly")).toBeNull();
    expect(picked("build the laptop riser not the two tier display stand")).toBeNull();
    expect(picked("build a shelf")).toBeNull();
  });
});

describe("the wish", () => {
  const asked = () => vi.mocked(jsonCall).mock.calls.map((c) => (c[1] as { text: string }).text);
  const current = async () => (await t.app.inject({ method: "GET", url: "/v1/build/sessions/current", headers: auth })).json();
  beforeEach(() => vi.mocked(jsonCall).mockClear());

  it("said before a scan reaches that scan's designs, stays through another view (X), and a plain ask clears it", async () => {
    const build = t.app.ctx.hooks.build!;
    build.expectScan("a birdhouse", false);
    const { session_id } = (await post("/v1/build/scans", kitUpload())).json();
    await build.idle();
    expect(asked().at(-1)).toContain('The builder asked: "a birdhouse"');
    expect((await current()).wish).toBe("a birdhouse");

    await post("/v1/build/scans", { ...kitUpload(), session_id });           // X: another view, nothing said
    await build.idle();
    expect(asked().at(-1)).toContain('The builder asked: "a birdhouse"');

    build.expectScan(null, false);                                                 // "what can I build?"
    await post("/v1/build/scans", { ...kitUpload(), session_id });
    await build.idle();
    expect(asked().at(-1)).not.toContain("The builder asked");
    expect((await current()).wish).toBeNull();
  });

  it("goes with a replayed recording too: the Director's fallback when the headset's own scan fails", async () => {
    const build = t.app.ctx.hooks.build!;
    const { scan_id } = (await post("/v1/build/scans", kitUpload())).json();
    await build.idle();
    build.expectScan("a birdhouse", false);
    await post(`/v1/build/scans/${scan_id}/replay`, { labels: "saved" });
    await build.idle();
    expect(asked().at(-1)).toContain('The builder asked: "a birdhouse"');
    expect((await current()).wish).toBe("a birdhouse");
  });

  it("is dropped when no scan follows within a minute: it belongs to that question, not a later one", async () => {
    const build = t.app.ctx.hooks.build!;
    build.expectScan("a robot", false);
    const later = Date.now() + 61_000;
    vi.spyOn(Date, "now").mockReturnValue(later);
    await post("/v1/build/scans", kitUpload());
    await build.idle();
    vi.mocked(Date.now).mockRestore();
    expect(asked().at(-1)).not.toContain("a robot");
  });

  it("said while the scan is still being named reaches that scan's designs", async () => {
    let release = () => {};
    nameTwins.mockImplementationOnce(async (d: unknown, ph: unknown, twins: Twin[]) => { await new Promise<void>((r) => { release = r; }); return byShape(d, ph, twins); });
    await post("/v1/build/scans", kitUpload());
    await vi.waitFor(() => expect(nameTwins).toHaveBeenCalled());
    t.app.ctx.hooks.build!.expectScan("a robot", false);
    release();
    await t.app.ctx.hooks.build!.idle();
    expect(asked().at(-1)).toContain('The builder asked: "a robot"');
  });

  it("a change keeps what was offered out ('something crazier' brings new ones); a new ask may show it again", async () => {
    const build = t.app.ctx.hooks.build!;
    const { session_id } = (await post("/v1/build/scans", kitUpload())).json();
    await build.idle();
    const shown = (await current()).ideas.map((i: { title: string }) => i.title);
    expect(shown.length).toBeGreaterThan(0);
    await build.rethink("something crazier", true);
    await build.idle();
    expect(asked().at(-1)).toContain(`Already offered, do not repeat: ${shown.join(", ")}.`);
    build.expectScan("something crazier", true);                             // mid-build: the rescan carries the change
    await post("/v1/build/scans", { ...kitUpload(), session_id });
    await build.idle();
    expect(asked().at(-1)).toContain(`Already offered, do not repeat: ${shown.join(", ")}`);
    await build.rethink("a birdhouse", false);                               // a new ask: the birdhouse shown before may be the answer
    await build.idle();
    expect(asked().at(-1)).not.toContain("Already offered");
    build.expectScan("a robot", false);
    await post("/v1/build/scans", { ...kitUpload(), session_id });
    await build.idle();
    expect(asked().at(-1)).not.toContain("Already offered");
  });

  it("a change keeps the ask it changes: the designer hears 'a birdhouse, then something crazier'", async () => {
    const build = t.app.ctx.hooks.build!;
    await post("/v1/build/scans", kitUpload());
    await build.idle();
    await build.rethink("a birdhouse", false);
    await build.rethink("something crazier", true);
    await build.idle();
    expect(asked().at(-1)).toContain('The builder asked: "a birdhouse, then something crazier".');
    expect([(await current()).wish, build.kitContext().wish]).toEqual(["a birdhouse, then something crazier", "a birdhouse, then something crazier"]);
    await build.rethink("make it taller", true);                             // the newest change replaces the last one
    await build.idle();
    expect(asked().at(-1)).toContain('The builder asked: "a birdhouse, then make it taller".');
    await build.rethink("a robot", false);                                   // a new ask replaces both
    await build.idle();
    expect(asked().at(-1)).toContain('The builder asked: "a robot".');
  });

  it("only the newest change can bring back a design shown before, by naming it; the ask it changes cannot", async () => {
    const build = t.app.ctx.hooks.build!;
    await post("/v1/build/scans", kitUpload());
    await build.idle();                                                       // offers the laptop riser
    await build.rethink("make the laptop riser taller", true);
    await build.idle();
    expect(asked().at(-1)).not.toContain("Already offered");
    await build.rethink("a laptop riser", false);
    await build.rethink("something crazier", true);
    await build.idle();
    expect(asked().at(-1)).toContain("Already offered, do not repeat: Laptop riser.");
  });

  it("said while a rescan is being named, goes to that scan's designs once: no rethink on top, no second scan, nothing left waiting", async () => {
    const build = t.app.ctx.hooks.build!;
    const { session_id } = (await post("/v1/build/scans", kitUpload())).json();
    await build.idle();
    let release = () => {};
    nameTwins.mockImplementationOnce(async (d: unknown, ph: unknown, twins: Twin[]) => { await new Promise<void>((r) => { release = r; }); return byShape(d, ph, twins); });
    await post("/v1/build/scans", { ...kitUpload(), session_id });
    await vi.waitFor(() => expect(nameTwins).toHaveBeenCalledTimes(2));
    vi.mocked(jsonCall).mockClear(); seen = [];
    expect(build.canRethink()).toBe(false);                                   // a rethink now would design twice
    expect(build.expectScan("a robot", false)).toBe(true);                   // it rides with the scan being named
    release();
    await build.idle();
    expect(asked()).toEqual([expect.stringContaining('The builder asked: "a robot".')]);
    expect(seen.filter((m) => m.type === "build_ideas" && m.final)).toHaveLength(1);
    await build.rethink("something crazier", true);
    await post("/v1/build/scans", { ...kitUpload(), session_id });           // another look: nothing stale re-applied
    await build.idle();
    expect(asked().at(-1)).toContain('The builder asked: "a robot, then something crazier".');
  });

  it("is replaced by a rethink's request, and tidied (spaces, trailing punctuation)", async () => {
    const build = t.app.ctx.hooks.build!;
    await post("/v1/build/scans", kitUpload());
    await build.idle();
    expect(await build.rethink("  something   for my phone!! ", false)).toBe(true);
    await build.idle();
    expect((await current()).wish).toBe("something for my phone");
    expect(asked().at(-1)).toContain('The builder asked: "something for my phone"');
  });
});
