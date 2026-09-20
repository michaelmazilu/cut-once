// Speech check: time to first audio, and whether the PCM plays cleanly (byte order is unverified). Run: pnpm tts:smoke
import "../env.js";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { models } from "../copilot/models.js";
import { streamSpeech } from "../copilot/speech.js";
import { loadConfig } from "../config.js";

const cfg = loadConfig();
const speechConfig = { ...cfg, elevenVoiceId: cfg.elevenVoiceId || models(cfg).voiceId };
const out = join(cfg.dataDir, "tts-smoke.pcm");
const started = Date.now();
try {
  const stream = await streamSpeech(speechConfig, "Run the cable through the tray to the right rear leg, then clip it down the leg every 20 centimetres.");
  const chunks: Uint8Array[] = [];
  let firstMs = 0;
  for await (const chunk of stream) { if (!firstMs) firstMs = Date.now() - started; chunks.push(chunk); }
  const bytes = Buffer.concat(chunks);
  mkdirSync(cfg.dataDir, { recursive: true });
  writeFileSync(out, bytes);
  console.log(`TTS PASS  first audio ${firstMs} ms, total ${Date.now() - started} ms, ${bytes.length} bytes (${(bytes.length / 2 / 22050).toFixed(1)} s of audio)`);
  console.log(`Listen: ffplay -autoexit -f s16le -ar 22050 -ch_layout mono "${out}"   (static? try -f s16be and tell A2)`);
} catch (err) {
  console.error(`TTS FAIL  ${(err as Error).message}`);
  process.exit(1);
}
