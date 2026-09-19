import { existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export interface Config {
  port: number; host: string; dataDir: string; apiToken: string; publicBaseUrl: string;
  esUrl: string; esApiKey: string; kibanaUrl: string; mcpUrl: string; jinaEmbedId: string; jinaRerankId: string;
  searchMode: "bm25" | "hybrid"; openaiKey: string; openaiModel: string; elevenKey: string; elevenVoiceId: string; reconstruction: boolean;
  repoRoot: string; webDist: string; projectId: string; logLevel: string;
  /** The seed of the run a fresh server starts with (DEFAULT_SEED). "blank" by default: no hologram until Kit builds one. */
  defaultSeed: string;
  /** Load the test plans (the desk and fixtures) at boot. Off in the app; tests and simulations turn it on (TEST_FIXTURES=on). */
  testFixtures: boolean;
  /** off: no copilot route. fake: canned answers, no keys (turns/fake.ts). live: Rhythm's real copilot. */
  copilotMode: "off" | "fake" | "live"; fakeCopilotDelayMs: number;
  /**
   * Build mode's models. Each job (the spoken turn, naming objects, designing) runs on OMNI (Qwen3.5-Omni through
   * yibuapi's OpenAI-compatible API) or OpenAI: KIT_AI for all, KIT_TURN_AI / KIT_LABEL_AI / KIT_IDEAS_AI for one.
   * A job whose provider has no key uses the other one (ai.ts).
   */
  kitAi: Record<AiJob, AiProvider>;
  omniKey: string; omniBaseUrl: string; omniModel: string; omniIdeasModel: string;
  /** How a voice clip is sent: a data URL (Alibaba's examples) or bare base64 (OpenAI's shape). `pnpm omni:probe` says which works. */
  omniAudio: "dataurl" | "base64";
  /** The spoken turn's model budget; the build designs' live deadline before the rehearsal cache; the router's budget on OMNI. */
  kitTurnMs: number; buildLiveMs: number; omniRouteMs: number;
}

export type AiProvider = "omni" | "openai";
export type AiJob = "turn" | "label" | "ideas";

const provider = (v: string | undefined): AiProvider | undefined => (v === "omni" || v === "openai" ? v : undefined);
const ms = (v: string | undefined, fallback: number) => (v && Number.isFinite(Number(v)) && Number(v) > 0 ? Number(v) : fallback);

const here = dirname(fileURLToPath(import.meta.url));
export const REPO_ROOT = resolve(here, "..", "..", "..");

export function loadConfig(env: Record<string, string | undefined> = process.env, overrides: Partial<Config> = {}): Config {
  const kibanaUrl = (env.KIBANA_URL ?? "").replace(/\/$/, "");
  const cfg: Config = {
    port: Number(env.PORT ?? 8080), host: env.HOST ?? "127.0.0.1",
    dataDir: resolve(REPO_ROOT, env.DATA_DIR ?? "./data/runtime"),
    apiToken: env.API_TOKEN || "dev-token", publicBaseUrl: (env.PUBLIC_BASE_URL ?? "").replace(/\/$/, ""),
    esUrl: env.ES_URL ?? "", esApiKey: env.ES_API_KEY ?? "", kibanaUrl,
    mcpUrl: env.AGENT_BUILDER_MCP_URL || (kibanaUrl ? `${kibanaUrl}/api/agent_builder/mcp` : ""),
    jinaEmbedId: env.JINA_EMBED_ID ?? "", jinaRerankId: env.JINA_RERANK_ID ?? "",
    searchMode: env.SEARCH_MODE === "hybrid" ? "hybrid" : "bm25",
    openaiKey: env.OPENAI_API_KEY ?? "", openaiModel: env.OPENAI_MODEL || "gpt-5.6-luna", elevenKey: env.ELEVENLABS_API_KEY ?? "", elevenVoiceId: env.ELEVENLABS_VOICE_ID ?? "",
    reconstruction: env.RECONSTRUCTION === "on", repoRoot: REPO_ROOT, webDist: join(REPO_ROOT, "apps", "web", "dist"),
    projectId: env.PROJECT_ID || "proj_cutonce_demo", logLevel: env.LOG_LEVEL ?? "info", defaultSeed: env.DEFAULT_SEED || "blank", testFixtures: env.TEST_FIXTURES === "on",
    copilotMode: env.COPILOT_MODE === "fake" || env.COPILOT_MODE === "live" ? env.COPILOT_MODE : "off",
    fakeCopilotDelayMs: Number(env.FAKE_COPILOT_DELAY_MS ?? 1200),
    kitAi: {
      turn: provider(env.KIT_TURN_AI) ?? provider(env.KIT_AI) ?? "omni",
      label: provider(env.KIT_LABEL_AI) ?? provider(env.KIT_AI) ?? "omni",
      ideas: provider(env.KIT_IDEAS_AI) ?? provider(env.KIT_AI) ?? "omni",
    },
    omniKey: env.OMNI_API_KEY ?? "", omniBaseUrl: (env.OMNI_BASE_URL ?? "").replace(/\/$/, ""),
    omniModel: env.OMNI_MODEL || "qwen3.5-omni-flash", omniIdeasModel: env.OMNI_IDEAS_MODEL ?? "",
    omniAudio: env.OMNI_AUDIO === "base64" ? "base64" : "dataurl",
    kitTurnMs: ms(env.KIT_TURN_MS, 6000), buildLiveMs: ms(env.BUILD_LIVE_MS, 8000), omniRouteMs: ms(env.OMNI_ROUTE_MS, 1500),
    ...overrides,
  };
  if (!env.API_TOKEN && !overrides.apiToken && env.NODE_ENV === "production") throw new Error("API_TOKEN must be set in production");
  return cfg;
}

export const hasWebBuild = (cfg: Config) => existsSync(join(cfg.webDist, "index.html"));
