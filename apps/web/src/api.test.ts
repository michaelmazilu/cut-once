import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { fetchAnswerAudio } from "./api";

// The server sends an answer's text before its spoken clip is finished (the clip is made in the background), and the
// browser asks for the clip the moment the text arrives. Measured against the live server: 404 at +0.02 s, ready by +0.36 s.
const reply = (status: number) =>
  new Response(status === 200 ? new Blob(["RIFF"]) : JSON.stringify({ error: { code: "not_found", message: "audio for turn_1 not found" } }), { status });

describe("fetchAnswerAudio", () => {
  let asked: string[];
  beforeEach(() => { vi.useFakeTimers(); asked = []; });
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });
  /** A server that answers with these statuses in turn, then keeps giving the last one. */
  const serve = (...statuses: number[]) => vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    asked.push(url);
    return reply(statuses[Math.min(asked.length - 1, statuses.length - 1)]!);
  }));

  it("asks for the browser's WAV, and waits while the clip is still being spoken", async () => {
    serve(404, 404, 200);
    const url = fetchAnswerAudio("/v1/audio/turn_1");
    await vi.advanceTimersByTimeAsync(1000);
    await expect(url).resolves.toMatch(/^blob:/);
    expect(asked).toEqual(Array(3).fill("/v1/audio/turn_1?format=wav"));
  });

  it("gives up with the server's 404 once the wait is over: a clip that never comes is an error, not a hang", async () => {
    serve(404);
    const failed = expect(fetchAnswerAudio("/v1/audio/turn_1", 2000)).rejects.toMatchObject({ status: 404 });
    await vi.advanceTimersByTimeAsync(3000);
    await failed;
  });

  it("does not wait on any other error", async () => {
    serve(500);
    await expect(fetchAnswerAudio("/v1/audio/turn_1")).rejects.toMatchObject({ status: 500 });
    expect(asked).toHaveLength(1);
  });
});
