import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { auth, makeApp } from "./helpers.js";

/**
 * Ten chunks, each arriving AFTER a delay, so the headset starts reading long before generation has finished.
 * That is the real shape of the thing: ElevenLabs streams, and the whole point of the live path is that playback
 * begins on the first chunk. A reader that races generation is where a dropped tail would hide.
 *
 * Each chunk is filled with its own number, so a missing or reordered one is visible in the bytes, not just in
 * the total.
 */
const CHUNKS = 10;
const CHUNK_BYTES = 4_410;          // 100 ms of 22.05 kHz 16-bit mono
const TOTAL = CHUNKS * CHUNK_BYTES;

vi.mock("../src/copilot/speech.js", () => ({
  streamSpeech: vi.fn(async () => (async function* () {
    for (let i = 0; i < CHUNKS; i++) {
      await new Promise((r) => setTimeout(r, 15));
      yield new Uint8Array(CHUNK_BYTES).fill(i + 1);
    }
  })()),
}));

let t: Awaited<ReturnType<typeof makeApp>>;
beforeEach(async () => { t = await makeApp({ copilotMode: "live" }); });
afterEach(async () => { await t.cleanup(); });

const say = async (text: string) =>
  (await t.app.inject({ method: "POST", url: "/v1/copilot/debug/say", headers: auth, payload: { text } })).json().turn_id as string;

describe("answer audio arrives whole", () => {
  it("the live stream delivers every chunk, in order, when read while it is still being generated", async () => {
    const turnId = await say("Run the cable through the tray to the right rear leg.");

    // Read immediately: generation has barely started, so the reader overtakes the generator repeatedly.
    const audio = await t.app.inject({ method: "GET", url: `/v1/audio/${turnId}`, headers: auth });
    expect(audio.statusCode).toBe(200);
    expect(audio.rawPayload.length).toBe(TOTAL);

    for (let i = 0; i < CHUNKS; i++) {
      const at = i * CHUNK_BYTES;
      expect(audio.rawPayload[at]).toBe(i + 1);
      expect(audio.rawPayload[at + CHUNK_BYTES - 1]).toBe(i + 1);
    }
  });

  it("the saved file is the same audio as the live stream, to the byte", async () => {
    const turnId = await say("Clip it down the leg every twenty centimetres.");
    const live = await t.app.inject({ method: "GET", url: `/v1/audio/${turnId}`, headers: auth });

    // …and again once generation has finished, which is served from the file instead of memory.
    await new Promise((r) => setTimeout(r, 400));
    const saved = await t.app.inject({ method: "GET", url: `/v1/audio/${turnId}`, headers: auth });

    expect(saved.statusCode).toBe(200);
    expect(saved.rawPayload.length).toBe(live.rawPayload.length);
    expect(Buffer.compare(saved.rawPayload, live.rawPayload)).toBe(0);
  });

  it("a reader that arrives late still gets the whole clip, not just what is left", async () => {
    const turnId = await say("Stand the panel against the wall.");
    await new Promise((r) => setTimeout(r, 80));       // join a third of the way in
    const audio = await t.app.inject({ method: "GET", url: `/v1/audio/${turnId}`, headers: auth });
    expect(audio.rawPayload.length).toBe(TOTAL);
    expect(audio.rawPayload[0]).toBe(1);               // from the beginning, not from where generation had reached
  });
});
