import { describe, expect, it, vi } from "vitest";
import type { AiCall } from "../src/ai.js";
import { loadConfig } from "../src/config.js";
import { models } from "../src/copilot/models.js";
import { routeOutcome, routeTurn } from "../src/copilot/router.js";

const cfg = loadConfig({}, { openaiKey: "k" });
const base = models(cfg, { OPENAI_ROUTER_MODEL: "small-router" });
const m = { ...base, budgets: { ...base.budgets, route: 50 } };
const input = { transcript: "what could we make with these", mode: "overlay" };
const on = (provider: "openai" | "omni", call: AiCall["call"]): AiCall => ({ provider, model: "turn-model", call });

describe("routeTurn", () => {
  it("returns the model's flow, and what they want built", async () =>
    expect(await routeTurn(cfg, m, input, on("openai", vi.fn(async () => ({ flow: "build_ideas", confidence: 0.93, wish: "a birdhouse" })))))
      .toEqual({ flow: "build_ideas", confidence: 0.93, wish: "a birdhouse" }));
  it("reads a missing wish as none", async () =>
    expect(await routeTurn(cfg, m, input, on("openai", vi.fn(async () => ({ flow: "question", confidence: 0.9 }))))).toEqual({ flow: "question", confidence: 0.9, wish: null }));
  it("gives up at the budget, so the turn is treated as a question", async () =>
    expect(await routeTurn(cfg, m, input, on("openai", vi.fn(() => new Promise<unknown>(() => {}))))).toBeNull());
  it("treats an error, a malformed answer, or a build-mode flow (Kit's job now) as a question", async () => {
    expect(await routeTurn(cfg, m, input, on("openai", vi.fn(async () => { throw new Error("down"); })))).toBeNull();
    expect(await routeTurn(cfg, m, input, on("openai", vi.fn(async () => ({ flow: "dance", confidence: 1 }))))).toBeNull();
    expect(await routeTurn(cfg, m, input, on("openai", vi.fn(async () => ({ flow: "modify_design", confidence: 0.9 }))))).toBeNull();
  });
  it("asks OpenAI's small router model within its budget, and tells it the mode and what was said", async () => {
    const call = vi.fn(async (_cfg: unknown, _req: unknown) => ({ flow: "question", confidence: 0.9 }));
    await routeTurn(cfg, m, { transcript: "make me a birdhouse", mode: "overlay" }, on("openai", call));
    expect(call.mock.calls[0]![1]).toMatchObject({ name: "route", model: "small-router", timeoutMs: 50, text: 'MODE: overlay\nSAID: "make me a birdhouse"' });
  });
  it("calls nothing when no provider has a key", async () => expect(await routeTurn(cfg, m, input, null)).toBeNull());
});

describe("routeOutcome", () => {
  it("scans only for a sure 'build ideas'; anything unsure, or a question, is answered as a question", () => {
    expect(routeOutcome({ flow: "build_ideas", confidence: 0.9 })).toBe("scan");
    expect(routeOutcome({ flow: "build_ideas", confidence: 0.6 })).toBe("question");
    expect(routeOutcome({ flow: "question", confidence: 0.99 })).toBe("question");
    expect(routeOutcome(null)).toBe("question");
  });
});
