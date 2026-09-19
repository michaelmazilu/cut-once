import { describe, expect, it } from "vitest";
import { loadConfig } from "../src/config.js";

describe("Kit and OMNI settings", () => {
  it("default every build-mode job to OMNI, with the flash model and the budgets from the spec", () => {
    const cfg = loadConfig({});
    expect(cfg.kitAi).toEqual({ turn: "omni", label: "omni", ideas: "omni" });
    expect([cfg.omniKey, cfg.omniBaseUrl, cfg.omniModel, cfg.omniIdeasModel, cfg.omniAudio]).toEqual(["", "", "qwen3.5-omni-flash", "", "dataurl"]);
    expect([cfg.kitTurnMs, cfg.buildLiveMs]).toEqual([6000, 8000]);
  });
  it("lets one setting move every job, and one job be moved on its own", () => {
    expect(loadConfig({ KIT_AI: "openai" }).kitAi).toEqual({ turn: "openai", label: "openai", ideas: "openai" });
    expect(loadConfig({ KIT_AI: "openai", KIT_IDEAS_AI: "omni" }).kitAi).toEqual({ turn: "openai", label: "openai", ideas: "omni" });
    expect(loadConfig({ KIT_TURN_AI: "banana" }).kitAi.turn).toBe("omni");            // a typo keeps the default rather than failing
  });
  it("reads the OMNI connection and the budgets", () => {
    const cfg = loadConfig({ OMNI_API_KEY: "k", OMNI_BASE_URL: "https://omni.example/v1/", OMNI_MODEL: "qwen3.5-omni-plus", OMNI_IDEAS_MODEL: "qwen-x",
      OMNI_AUDIO: "base64", KIT_TURN_MS: "4000", BUILD_LIVE_MS: "5000" });
    expect([cfg.omniKey, cfg.omniBaseUrl, cfg.omniModel, cfg.omniIdeasModel, cfg.omniAudio]).toEqual(["k", "https://omni.example/v1", "qwen3.5-omni-plus", "qwen-x", "base64"]);
    expect([cfg.kitTurnMs, cfg.buildLiveMs]).toEqual([4000, 5000]);
    expect(loadConfig({ KIT_TURN_MS: "soon" }).kitTurnMs).toBe(6000);
  });
});
