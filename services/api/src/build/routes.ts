import type { FastifyInstance } from "fastify";
import { z } from "zod";
import { S } from "@cutonce/schemas";
import type { Ctx, Plugin } from "../app.js";
import { ApiError, badRequest } from "../errors.js";
import { aiFor } from "../ai.js";
import { loadRules, loadVocab, standardShape } from "./data.js";
import { askedFor, BuildSessions } from "./session.js";

/** Build mode (the Lego Movie). Which provider and model name objects and design: ai.ts (KIT_AI and friends). */
/** A JPEG starts FF D8 FF ("/9j/" in base64). Checked before anything is saved: the labeller cannot read anything else. */
export const isJpeg = (b64: string) => b64.startsWith("/9j/");

export const buildRoutes: Plugin = (app: FastifyInstance, ctx: Ctx) => {
  const vocab = loadVocab(ctx.cfg.repoRoot);
  const rules = loadRules(ctx.cfg.repoRoot, vocab);
  const sessions = new BuildSessions(ctx, { vocab, rules, log: app.log, ai: (job) => aiFor(ctx.cfg, job) });
  ctx.hooks.build = {
    canRethink: () => sessions.canRethink(), rethink: (request, change) => sessions.rethink(request, change), expectScan: (wish, change) => sessions.expectScan(wish, change),
    kitContext: () => sessions.kitContext(), startIdea: (ideaId) => sessions.startIdea(ideaId), idle: () => sessions.idle(),
  };

  app.post("/v1/build/scans", { bodyLimit: 8 * 1024 * 1024 }, async (req, reply) => {
    const body = S.BuildScanUpload.safeParse(req.body);
    if (!body.success) throw badRequest("body must be a BuildScanUpload", body.error.issues);
    const { cols, rows } = body.data.grid;
    if (body.data.points_mm.length !== 3 * cols * rows || body.data.hit.length !== cols * rows) {
      throw badRequest(`a ${cols} × ${rows} grid needs ${3 * cols * rows} numbers and ${cols * rows} hit flags`);
    }
    if (!isJpeg(body.data.photo_b64)) throw badRequest("photo_b64 must be a JPEG");
    return reply.status(202).send(sessions.accept(body.data));
  });
  app.get("/v1/build/scans", async () => ({ scans: sessions.files.listScans() }));
  app.post<{ Params: { scan_id: string } }>("/v1/build/scans/:scan_id/replay", async (req) => {
    const body = z.object({ labels: z.enum(["saved", "live"]).default("saved") }).safeParse(req.body ?? {});
    if (!body.success) throw badRequest("body must be { labels: \"saved\" | \"live\" }");
    return sessions.replay(req.params.scan_id, body.data.labels);
  });
  app.post("/v1/build/sessions", async () => ({ session_id: sessions.newSession().session_id }));
  app.get("/v1/build/sessions/current", async () => {
    const s = sessions.current();
    return { session: s ? { session_id: s.session_id, created_at: s.created_at, scans: s.scans } : null, wish: s ? askedFor(s) : null, surfaces: s?.surfaces ?? [], twins: s?.twins ?? [], ideas: s?.ideas ?? [] };
  });
  app.post("/v1/build/ideas/rethink", async (req) => {
    const body = z.object({ request: z.string().min(1).max(300) }).safeParse(req.body);
    if (!body.success) throw badRequest("body must be { request }");
    // The Director asks for other designs: a change, so the ones on show are not offered again.
    return { accepted: await sessions.rethink(body.data.request, true) };
  });
  app.post<{ Params: { idea_id: string } }>("/v1/build/ideas/:idea_id/start", async (req) => sessions.startIdea(req.params.idea_id));
  app.post("/v1/build/objects", async (req) => {
    const body = z.object({ name: z.string().min(1) }).safeParse(req.body);
    if (!body.success) throw badRequest("body must be { name }");
    return sessions.addObject(body.data.name);
  });
  app.get("/v1/build/vocabulary", async () => ({ items: [...vocab.values()].map((item) => ({ name: item.name, label: item.label, standard: standardShape(item) !== null })) }));
  app.post("/v1/build/say", async (req) => {
    const body = z.object({ text: z.string().min(1).max(400) }).safeParse(req.body);
    if (!body.success) throw badRequest("body must be { text }");
    const said = ctx.hooks.say?.(body.data.text);
    if (!said) throw new ApiError(503, "tts_unavailable", "the copilot's voice is not running");
    return said;
  });
};
