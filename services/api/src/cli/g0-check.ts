/**
 * G0 and the rest of pillar C's preflight, in one command:
 *
 *   pnpm g0
 *
 * It answers, in order: does the chat model accept an image and return strict JSON (G0 proper),
 * does transcription work, and does ElevenLabs return audio. Every check prints its own timing, so
 * the same command doubles as the latency sanity check before a rehearsal.
 *
 * If G0 fails on images, set OPENAI_COPILOT_MODEL to the vision-capable model and run it again —
 * that is the only change needed anywhere in the repo.
 */
import "../env.js";
import { readFileSync, existsSync } from "node:fs";
import { deflateSync } from "node:zlib";
import { join } from "node:path";
import OpenAI, { toFile } from "openai";
import { z } from "zod";
import { jsonCall } from "../llm.js";
import { loadConfig, REPO_ROOT } from "../config.js";
import { models } from "../copilot/models.js";

const cfg = loadConfig();
const m = models(cfg);

const Shape = z.object({ shape: z.enum(["circle", "square", "triangle", "none"]), colour: z.string(), confident: z.boolean() }).strict();

/** A 256×256 PNG with one solid red square on white. No encoder needed: a PNG is a zlib stream plus headers. */
function testPng(): Buffer {
  const size = 256;
  const raw = Buffer.alloc(size * (size * 3 + 1));
  for (let y = 0; y < size; y++) {
    const row = y * (size * 3 + 1);
    raw[row] = 0;
    for (let x = 0; x < size; x++) {
      const inside = x > 64 && x < 192 && y > 64 && y < 192;
      const o = row + 1 + x * 3;
      raw[o] = inside ? 220 : 255; raw[o + 1] = inside ? 32 : 255; raw[o + 2] = inside ? 32 : 255;
    }
  }
  const chunk = (type: string, data: Buffer) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
    const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
    let crc = 0xffffffff;
    for (const byte of body) crc = crcTable[(crc ^ byte) & 0xff]! ^ (crc >>> 8);
    const crcBuf = Buffer.alloc(4); crcBuf.writeUInt32BE((crc ^ 0xffffffff) >>> 0);
    return Buffer.concat([len, body, crcBuf]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk("IHDR", ihdr), chunk("IDAT", deflateSync(raw)), chunk("IEND", Buffer.alloc(0))]);
}

/** One second of 16 kHz mono speech-shaped tone. Enough to prove the transcription path, not to transcribe. */
function testWav(): Buffer {
  const rate = 16000;
  const samples = rate;
  const data = Buffer.alloc(samples * 2);
  for (let i = 0; i < samples; i++) {
    const t = i / rate;
    const v = Math.sin(2 * Math.PI * 180 * t) * 0.3 + Math.sin(2 * Math.PI * 320 * t) * 0.2;
    data.writeInt16LE(Math.round(v * 20000 * Math.min(1, 4 * Math.min(t, 1 - t))), i * 2);
  }
  const header = Buffer.alloc(44);
  header.write("RIFF", 0); header.writeUInt32LE(36 + data.length, 4); header.write("WAVE", 8);
  header.write("fmt ", 12); header.writeUInt32LE(16, 16); header.writeUInt16LE(1, 20); header.writeUInt16LE(1, 22);
  header.writeUInt32LE(rate, 24); header.writeUInt32LE(rate * 2, 28); header.writeUInt16LE(2, 32); header.writeUInt16LE(16, 34);
  header.write("data", 36); header.writeUInt32LE(data.length, 40);
  return Buffer.concat([header, data]);
}

const results: { gate: string; ok: boolean; skipped?: boolean; ms: number; detail: string }[] = [];
async function check(gate: string, fn: () => Promise<string>) {
  const t0 = Date.now();
  // The detail is awaited before the row is built; putting `await fn()` inside the literal would
  // evaluate `ms` first and every check would report 0 ms.
  try { const detail = await fn(); results.push({ gate, ok: true, ms: Date.now() - t0, detail }); }
  catch (err) { results.push({ gate, ok: false, ms: Date.now() - t0, detail: (err as Error).message }); }
}

console.log(`chat model:   ${m.chat}\ntranscribe:   ${m.stt}\nvoice:        ${m.ttsModel} / ${m.voiceId}\n`);

/** A missing key marks its checks skipped and keeps going: a half-configured laptop should still test the rest. */
const skip = (gate: string, why: string) => results.push({ gate, ok: false, skipped: true, ms: 0, detail: why });

// G0: an image in, strict JSON out. Everything in the copilot depends on this one answering yes.
if (!cfg.openaiKey) skip("G0 · image + strict JSON", "OPENAI_API_KEY is not set in .env.local");
else await check("G0 · image + strict JSON", async () => {
  const frame = join(REPO_ROOT, "data", "fixtures", "frame_0001.jpg");
  const image = existsSync(frame)
    ? { data: readFileSync(frame), mime: "image/jpeg" as const }
    : { data: testPng(), mime: "image/png" as const };
  // The loaded config (with the key) and the copilot's own model: the one voice answers and verification use.
  const out = await jsonCall(cfg, {
    model: m.chat, name: "g0", schema: Shape, timeoutMs: 30_000,
    system: "You answer only from the image you are given, in the JSON schema provided.",
    text: existsSync(frame) ? "What shape is the large light-coloured panel in the middle of this photo? Give its rough colour." : "What shape is the coloured region, and what colour is it?",
    images: [image],
  });
  return `model returned ${JSON.stringify(out)}`;
});

if (!cfg.openaiKey) skip("STT · transcription endpoint", "OPENAI_API_KEY is not set in .env.local");
else await check("STT · transcription endpoint", async () => {
  const client = new OpenAI({ apiKey: cfg.openaiKey, timeout: 30_000, maxRetries: 0 });
  const res = await client.audio.transcriptions.create({ file: await toFile(testWav(), "g0.wav", { type: "audio/wav" }), model: m.stt });
  const text = typeof res === "string" ? res : res.text;
  return `accepted a 1 s WAV, returned ${JSON.stringify(text.slice(0, 60))} (a tone, so the words mean nothing — the call working is the point)`;
});

if (!cfg.elevenKey) skip("TTS · ElevenLabs PCM", "ELEVENLABS_API_KEY is not set in .env.local");
else await check("TTS · ElevenLabs PCM", async () => {
  const res = await fetch(`https://api.elevenlabs.io/v1/text-to-speech/${m.voiceId}/stream?output_format=pcm_${m.sampleRate}`, {
    method: "POST", headers: { "xi-api-key": cfg.elevenKey, "content-type": "application/json" },
    body: JSON.stringify({ text: "Run it through the cable tray to the right rear leg.", model_id: m.ttsModel }),
    signal: AbortSignal.timeout(20_000),
  });
  if (!res.ok) throw new Error(`${res.status}: ${(await res.text().catch(() => "")).slice(0, 200)}`);
  const bytes = (await res.arrayBuffer()).byteLength;
  if (bytes === 0) throw new Error("returned no audio");
  return `${bytes} bytes of PCM at ${m.sampleRate} Hz (${(bytes / 2 / m.sampleRate).toFixed(1)} s)`;
});

console.log(results.map((r) => `${r.skipped ? "SKIP" : r.ok ? "PASS" : "FAIL"}  ${r.gate.padEnd(28)} ${String(r.ms).padStart(6)} ms  ${r.detail}`).join("\n"));
const g0 = results[0]!;
if (!g0.ok && !g0.skipped) {
  console.error(`\nG0 FAILED on ${m.chat}. If the error says the model does not take images, set OPENAI_COPILOT_MODEL in .env.local`);
  console.error("to a vision-capable model and run pnpm g0 again (it checks that same model), and tell the team.");
}
// A missing key skips its checks; only a check that ran and failed fails the command.
// Let SDK keep-alive sockets close naturally. A hard process.exit() can trip libuv's UV_HANDLE_CLOSING assertion
// on Windows after otherwise successful OpenAI requests, causing the preflight to report 0xC0000409.
process.exitCode = results.some((r) => !r.ok && !r.skipped) ? 1 : 0;
