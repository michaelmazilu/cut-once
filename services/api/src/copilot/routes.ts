import type { FastifyInstance } from "fastify";
import { z } from "zod";
import { S, type CopilotContext, type VerificationRequest } from "@cutonce/schemas";
import type { Ctx, Plugin } from "../app.js";
import { ApiError, badRequest, notFound } from "../errors.js";
import { DemoCache } from "./cache.js";
import { DebugCaptures, DEBUG_PAGE } from "./debug.js";
import { models } from "./models.js";
import { filePart, jsonPart, readMultipart } from "./multipart.js";
import { answerQuery } from "./pipeline.js";
import { Speech } from "./tts.js";
import { TurnMemory } from "./turns.js";
import { verifyPart } from "./verify.js";

/**
 * Pillar C, plugged in whole. The query and verify routes register only when COPILOT_MODE=live —
 * in fake mode turns/fake.ts owns the same query path, and in off mode there is no copilot at all.
 * The /debug page and its capture endpoints stay on in every mode: G2 and the speaker test must not
 * depend on keys being present.
 */
export const copilotRoutes: Plugin = (app: FastifyInstance, ctx: Ctx) => {
  const m = models(ctx.cfg);
  const speech = new Speech(ctx.cfg, m, ctx.turns);
  const turns = new TurnMemory(ctx);
  const cache = new DemoCache(ctx, speech, turns);
  const captures = new DebugCaptures(ctx.cfg);
  const deps = { ctx, models: m, speech, turns, cache };

  // The Director page's `promote_cache` command, and the live-PCM side of GET /v1/audio/:turn_id
  // (turns/routes.ts asks this hook before falling back to the finished file).
  ctx.hooks.promoteCache = (turnId, scriptedQueryId) => cache.promote(turnId, scriptedQueryId);
  ctx.hooks.audioStream = (turnId) => {
    const stream = speech.stream(turnId);
    return stream ? { stream, contentType: speech.contentType } : null;
  };
  ctx.hooks.say = (text) => {
    const turnId = turns.newTurnId();
    speech.start(turnId, text);
    return { turn_id: turnId, audio_url: `/v1/audio/${turnId}` };
  };

  const requireAssembly = (aid: string) => { ctx.store.getAssembly(aid); return aid; };

  if (ctx.cfg.copilotMode === "live") {
    app.post<{ Params: { aid: string } }>("/v1/assemblies/:aid/copilot/query", async (req) => {
      const started = Date.now();
      const aid = requireAssembly(req.params.aid);
      const parsed = await readMultipart(req);
      const uploadMs = Date.now() - started;

      const context = S.CopilotContext.safeParse(jsonPart(parsed, "context"));
      if (!context.success) throw badRequest("the `context` field is not a CopilotContext", context.error.issues);
      // A typed question (the web kitchen's text box) stands in for the voice clip: it is what Kit "heard".
      const said = parsed.fields.question?.trim().slice(0, 500) || null;
      const audio = said ? parsed.files.audio ?? null : filePart(parsed, "audio");
      // A frame is optional: the camera may not be ready, and the rehearsed HUD answers must work without one.
      const frame = parsed.files.frame?.buffer.length ? parsed.files.frame : null;
      captures.put({ frame: frame?.buffer ?? null, audio: audio?.buffer ?? null, mime: frame?.mime ?? "image/jpeg", note: `query on ${aid}`, context: context.data });

      try {
        return await answerQuery(deps, { assemblyId: aid, context: context.data as CopilotContext, audio: audio?.buffer ?? Buffer.alloc(0), said, frame: frame?.buffer ?? null, uploadMs }, app.log);
      } catch (err) {
        // Everything recoverable is already handled inside the pipeline; this is a dead key or a dead network.
        app.log.error({ err: (err as Error).message }, "copilot query failed");
        throw new ApiError(503, "copilot_unavailable", (err as Error).message);
      }
    });

    app.post<{ Params: { aid: string } }>("/v1/assemblies/:aid/verify", async (req) => {
      const aid = requireAssembly(req.params.aid);
      const parsed = await readMultipart(req);
      const request = S.VerificationRequest.safeParse(jsonPart(parsed, "request"));
      if (!request.success) throw badRequest("the `request` field is not a VerificationRequest", request.error.issues);
      const frame = filePart(parsed, "frame");
      captures.put({ frame: frame.buffer, audio: null, mime: frame.mime, note: `verify ${request.data.part_id}`, context: null });
      return verifyPart(ctx, m, {
        assemblyId: aid, request: request.data as VerificationRequest, frame: frame.buffer,
        expectedView: parsed.files.expected_view?.buffer ?? null,
      });
    });
  }

  app.get("/v1/copilot/cache", async () => ({ entries: cache.list() }));

  // ── G2's loop: the headset posts a frame and a clip, the /debug page shows them ────────────────
  app.post("/v1/copilot/debug/capture", async (req) => {
    const parsed = await readMultipart(req);
    let context: unknown = null;
    try { context = jsonPart(parsed, "context"); } catch { /* the context is optional here: G2 only needs pixels and sound */ }
    return captures.put({
      frame: parsed.files.frame?.buffer ?? null, audio: parsed.files.audio?.buffer ?? null,
      mime: parsed.files.frame?.mime ?? "application/octet-stream", note: parsed.fields.note ?? "debug capture", context,
    });
  });

  // Speaks any sentence through the real TTS path: the headset speaker, casting audio and the demo
  // voice get tested with no OpenAI key and no full turn. Also how the voice is warmed up before judging.
  app.post("/v1/copilot/debug/say", async (req) => {
    const body = z.object({ text: z.string().min(1).max(500) }).safeParse(req.body);
    if (!body.success) throw badRequest("body must be { text }", body.error.issues);
    const turnId = turns.newTurnId();
    speech.start(turnId, body.data.text);
    const firstByteMs = await speech.firstByteMs(turnId, Date.now(), m.budgets.tts);
    const failure = speech.failure(turnId);
    if (failure && firstByteMs === null) throw new ApiError(503, "tts_unavailable", failure);
    return { turn_id: turnId, audio_url: `/v1/audio/${turnId}`, first_byte_ms: firstByteMs };
  });

  app.get("/v1/copilot/debug/last", async () => captures.meta() ?? {});
  app.get("/v1/copilot/debug/frame.jpg", async (_req, reply) => {
    const frame = captures.frame();
    if (!frame) throw notFound("a captured frame");
    return reply.header("content-type", "image/jpeg").header("cache-control", "no-store").send(frame);
  });
  app.get("/v1/copilot/debug/audio.wav", async (_req, reply) => {
    const audio = captures.audio();
    if (!audio) throw notFound("captured audio");
    return reply.header("content-type", "audio/wav").header("cache-control", "no-store").send(audio);
  });

  // Outside /v1 so it loads without a bearer; every call it makes carries one.
  app.get("/debug", async (_req, reply) => reply.header("content-type", "text/html; charset=utf-8").send(DEBUG_PAGE));
};
