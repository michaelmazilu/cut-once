// Gate G0: can our key use the chosen model with an image and strict JSON output? Run: pnpm llm:smoke
import "../env.js";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { z } from "zod";
import { loadConfig } from "../config.js";
import { jsonCall } from "../llm.js";

const cfg = loadConfig();
const image = readFileSync(join(cfg.repoRoot, "data/fixtures/frame_0001.jpg"));
const Answer = z.object({ fill_colour: z.string(), has_title_text: z.boolean() }).strict();
const started = Date.now();
try {
  const out = await jsonCall(cfg, {
    name: "smoke", schema: Answer, system: "Describe the image exactly.",
    text: "What colour is the filled shape, and is there a title at the top?", images: [{ data: image, mime: "image/png" }], timeoutMs: 30_000,
  });
  console.log(`G0 PASS  ${cfg.openaiModel}  ${Date.now() - started} ms  ${JSON.stringify(out)}`);
} catch (err) {
  console.error(`G0 FAIL  ${cfg.openaiModel}: ${(err as Error).message}. If the model is unavailable, try OPENAI_MODEL=gpt-5.6-terra.`);
  process.exit(1);
}
