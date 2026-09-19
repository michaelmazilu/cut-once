import Fastify, { type FastifyInstance } from "fastify";
import cors from "@fastify/cors";
import multipart from "@fastify/multipart";
import websocket from "@fastify/websocket";
import { registerAuth } from "./auth.js";
import { boot } from "./boot.js";
import type { KitBuildContext } from "./build/session.js";
import type { Config } from "./config.js";
import { ApiError, sendError } from "./errors.js";
import { coreRoutes } from "./routes/core.js";
import { directorRoutes } from "./routes/director.js";
import { streamRoutes } from "./routes/stream.js";
import { DocumentStore } from "./store/documents.js";
import { Store } from "./store/store.js";
import { TurnLog } from "./turns/turns.js";
import { Hub } from "./ws/hub.js";

/** Other modules (the copilot) plug optional behaviour in here without the core importing them. */
export interface Hooks {
  promoteCache?: (turnId: string, scriptedQueryId: string) => Promise<void>;
  /** Live PCM for a turn still being spoken; turns/routes.ts asks here before falling back to the finished file. */
  audioStream?: (turnId: string) => { stream: NodeJS.ReadableStream; contentType: string } | null;
  /** Speaks a sentence in the copilot's voice; the audio is at GET /v1/audio/:turn_id. Set by the copilot. */
  say?: (text: string) => { turn_id: string; audio_url: string };
  /** Build mode, for the copilot (set by build/routes.ts). */
  build?: {
    /** Is there a design on show that could be changed? (Objects are known and no build is under way.) */
    canRethink: () => boolean;
    /**
     * change: the request changes the designs on show ("something crazier"), so they are not offered again. Otherwise
     * it is a new ask, and anything may be offered, even a design shown before.
     */
    rethink: (request: string, change: boolean) => Promise<boolean>;
    /**
     * The copilot is about to start a scan: what the builder asked for goes with it (null: a plain ask, forget the last
     * wish). True when a scan being read or named took it instead: then no scan is asked for.
     */
    expectScan: (wish: string | null, change: boolean) => boolean;
    /** What Kit is told about build mode on every turn (build/session.ts). */
    kitContext: () => KitBuildContext;
    /** Start a design on show as a normal run, as the trigger or the Director does. */
    startIdea: (ideaId: string) => Promise<unknown>;
    /** Resolves once every queued scan has been processed (tests, the eval CLI). */
    idle: () => Promise<void>;
  };
}
export interface Ctx { cfg: Config; store: Store; docs: DocumentStore; hub: Hub; hooks: Hooks; turns: TurnLog }
export type Plugin = (app: FastifyInstance, ctx: Ctx) => void | Promise<void>;

declare module "fastify" {
  interface FastifyInstance { ctx: Ctx }
}

export async function buildApp(cfg: Config, plugins: Plugin[] = []): Promise<FastifyInstance> {
  const app = Fastify({ logger: cfg.logLevel === "silent" ? false : { level: cfg.logLevel }, bodyLimit: 2 * 1024 * 1024 });
  const store = new Store(cfg.dataDir);
  const ctx: Ctx = { cfg, store, docs: new DocumentStore(cfg.dataDir), hub: new Hub(store), hooks: {}, turns: new TurnLog(cfg.dataDir, store.bus) };
  app.decorate("ctx", ctx);

  await app.register(cors, { origin: true });
  // Fields are bounded too: a question is a sentence and a context is a packet, and both are read before anything
  // validates them.
  await app.register(multipart, { limits: { fileSize: 25 * 1024 * 1024, files: 4, fields: 12, fieldSize: 256 * 1024 } });
  await app.register(websocket);
  registerAuth(app, cfg);

  app.setErrorHandler((err, _req, reply) => {
    if (err instanceof ApiError) return sendError(reply, err);
    const e = err as { code?: string; statusCode?: number; message: string };
    if (e.code === "FST_REQ_FILE_TOO_LARGE") return reply.status(413).send({ error: { code: "too_large", message: "files are limited to 25 MB" } });
    if (e.statusCode && e.statusCode < 500) return reply.status(e.statusCode).send({ error: { code: "bad_request", message: e.message } });
    app.log.error(err);
    return reply.status(500).send({ error: { code: "internal", message: "unexpected server error" } });
  });

  coreRoutes(app, ctx);
  directorRoutes(app, ctx);
  streamRoutes(app, ctx);
  for (const plugin of plugins) await plugin(app, ctx);

  await boot(store, ctx.docs, cfg, app.log);
  app.addHook("onClose", async () => ctx.hub.close());
  return app;
}
