import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { ZodError } from "zod";
import type { BuildIdea, BuildScan, BuildScanUpload, Surface, Twin, Vec3 } from "@cutonce/schemas";
import type { AiCall } from "../ai.js";
import type { Ctx } from "../app.js";
import { normalise } from "../copilot/fastpath.js";
import { badRequest, notFound } from "../errors.js";
import { standardShape, type Rule, type Vocab } from "./data.js";
import { BuildFiles, newId } from "./files.js";
import { computeIdeas, describeFound, hasTape, summary } from "./ideas.js";
import { nameTwins } from "./label.js";
import { appendTwin, mergeSurfaces, mergeTwins } from "./merge.js";
import { flatSize, heightOf } from "./shape.js";
import { fixSizes } from "./sizes.js";
import { buildTwins, decodeScan, type Cloud } from "./twins.js";

export interface BuildDeps {
  vocab: Vocab; rules: Rule[];
  /** The model for naming or designing, looked up per call (ai.ts), or null when no provider has a key. */
  ai: (job: "label" | "ideas") => AiCall | null;
  log: { warn: (o: object, m: string) => void; error: (o: object, m: string) => void };
}
export interface Session {
  session_id: string; created_at: string; scans: string[]; surfaces: Surface[]; twins: Twin[]; ideas: BuildIdea[];
  camera: Vec3 | null; photo: string | null;
  /** Which way the camera faced for the last scan, so "the can on your left" can be said. */
  forward: Vec3 | null;
  /** The idea being built, from its start until the next scan. The headset shows no new ideas mid-build, and drops out of build mode if another run appears. */
  started: string | null;
  /** What the builder asked for ("a birdhouse"), for every design asked for in this session until a new wish or a plain ask. */
  wish: string | null;
  /** The newest change to the designs on show ("something crazier"), on top of the wish, until the next ask. */
  change: string | null;
  /** Titles already offered in this session, so "something crazier" brings new ones. A new or plain ask starts afresh. */
  offered: string[];
}

/** All the builder asked for, as the designer, Kit and the Director are told it: "a birdhouse, then something crazier". */
export const askedFor = (s: Pick<Session, "wish" | "change">): string | null => (s.wish && s.change ? `${s.wish}, then ${s.change}` : s.change ?? s.wish);

/** What Kit is told about build mode on every turn: what is happening, the objects, the designs on show, the wish. */
export interface KitBuildContext {
  status: string;
  twins: Twin[]; surfaces: Surface[];
  /** Where the camera stood and faced for the last scan: "the can on your left" is said from there. */
  camera: { position: Vec3; forward: Vec3 } | null;
  wish: string | null;
  /** The designs on show, in the order the headset lays them out (left to right). Empty once one is being built. */
  ideas: { idea_id: string; title: string; why: string; uses: string[]; steps: number }[];
  /** The idea being built, from its start until the next scan. */
  started: string | null;
  /** A roll of tape is on the table, so designs may tape pieces together. */
  tape: boolean;
}

/** How long a wish said with "start a scan" waits for that scan. A scan much later is someone else's question. */
export const WISH_TTL_MS = 60_000;

/** A wish as it is kept: one line, no trailing punctuation, at most 120 characters. Empty is no wish. */
export function cleanWish(wish: string | null): string | null {
  const w = (wish ?? "").replace(/\s+/g, " ").trim().replace(/[.!?,;:\s]+$/, "").slice(0, 120);
  return w || null;
}

/** What may surround an idea's name when it is being picked: "let's build the laptop riser, please". */
const PICKING = new Set("lets let us build make start do try pick choose show me i id we want would like to go with for the a an that this one please instead ok okay yes yeah sure can could you now then".split(" "));

/**
 * The idea a sentence picks, or null. Only the name plus picking words counts. "How tall is the laptop riser?" names an
 * idea too, but asks about it, and starting a run for it would throw the current build away.
 */
export function pickIdea<T extends { title: string }>(transcript: string, ideas: T[]): T | null {
  const said = ` ${normalise(transcript)} `;
  for (const idea of [...ideas].sort((a, b) => b.title.length - a.title.length)) {   // "tall laptop riser" before "laptop riser"
    const name = ` ${normalise(idea.title)} `;
    const at = said.indexOf(name);
    if (at < 0) continue;
    const around = `${said.slice(0, at)} ${said.slice(at + name.length)}`.split(" ").filter(Boolean);
    if (around.every((w) => PICKING.has(w))) return idea;
  }
  return null;
}

/**
 * One build session at a time (one headset). A scan is saved, answered at once (202), then processed in order:
 * outlines → names → sizes → designs (live, then the rehearsal cache, then the rules). Every step is broadcast, so the
 * headset and /director update live.
 */
export class BuildSessions {
  readonly files: BuildFiles;
  private session: Session | null = null;
  private queue: Promise<void> = Promise.resolve();
  /** A wish the copilot sent with a scan it asked the headset for, until that scan arrives. */
  private expected: { wish: string | null; change: boolean; at: number } | null = null;
  /** What the queue is doing right now: reading a scan, naming its objects, or designing. */
  private busy: "reading" | "naming" | "designing" | null = null;
  /** Work that outlives its queue step: a late live design answer refreshing the cache. */
  private readonly background = new Set<Promise<unknown>>();

  constructor(private readonly ctx: Ctx, private readonly deps: BuildDeps) { this.files = new BuildFiles(ctx.cfg.dataDir, ctx.cfg.repoRoot); }

  current = () => this.session;
  /** Resolves once every queued scan has been processed and nothing is still running behind it. */
  idle = async (): Promise<void> => {
    await this.queue;
    await Promise.all([...this.background]);
  };

  /** Build mode as Kit is told it on every turn. */
  kitContext(): KitBuildContext {
    const s = this.session;
    const status = this.busy === "reading" || this.busy === "naming" ? "scanning: finding and naming the objects"
      : this.busy === "designing" ? `designing${s && askedFor(s) ? ` ${askedFor(s)}` : ""}`
      : !s || s.scans.length === 0 ? "nothing scanned yet"
      : s.started ? "a design is being built"
      : s.ideas.length ? `showing ${s.ideas.length} design${s.ideas.length === 1 ? "" : "s"}` : "no designs on show";
    return {
      status, twins: s?.twins ?? [], surfaces: s?.surfaces ?? [],
      camera: s?.camera && s.forward ? { position: s.camera, forward: s.forward } : null,
      wish: s ? askedFor(s) : null,
      ideas: s && !s.started ? s.ideas.map((i) => ({ idea_id: i.idea_id, title: i.title, why: i.why, uses: [...new Set(Object.values(i.twin_of))], steps: i.plan.steps.length - 1 })) : [],
      started: s?.started ?? null,
      tape: hasTape(s?.twins ?? []),
    };
  }

  newSession(): Session {
    this.session = {
      session_id: newId("bsess"), created_at: new Date().toISOString(), scans: [], surfaces: [], twins: [], ideas: [],
      camera: null, forward: null, photo: null, started: null, wish: null, change: null, offered: [],
    };
    return this.session;
  }

  accept(upload: BuildScanUpload): { scan_id: string; session_id: string } {
    const session = upload.session_id && this.session?.session_id === upload.session_id ? this.session : this.newSession();
    session.started = null;                                          // scanning again puts the headset back to picking
    this.takeWish(session);
    const scan = this.files.saveScan(upload, session.session_id);
    const photo = Buffer.from(upload.photo_b64, "base64");
    this.enqueue(() => this.process(session, scan, photo, "live"));
    return { scan_id: scan.scan_id, session_id: session.session_id };
  }

  replay(scanId: string, labels: "saved" | "live"): { session_id: string } {
    const { scan, photo } = this.files.readScan(scanId);
    const session = this.newSession();
    this.takeWish(session);                                          // the Director's fallback for a failed scan: same wish
    this.enqueue(() => this.process(session, { ...scan, session_id: session.session_id }, photo, labels));
    return { session_id: session.session_id };
  }

  /** The wish waiting for a scan goes with this one (the headset's, or a replay), if it is still fresh. */
  private takeWish(session: Session): void {
    const said = this.expected;
    this.expected = null;
    if (said && Date.now() - said.at <= WISH_TTL_MS) this.setWish(session, said.wish, said.change);
  }

  /** Objects known, nothing being built, and no scan being read or named (it designs next: a rethink would design twice). */
  canRethink = (): boolean => Boolean(this.session && this.session.twins.length > 0 && !this.session.started && this.busy !== "reading" && this.busy !== "naming");

  /**
   * The copilot is asking the headset for a scan, and the builder said what they want (null: a plain "what can I
   * build?"). The wish goes with the next scan to arrive; or, when a scan is being read or named now (its designs not
   * yet asked for), straight to that scan. Then it answers true: no other scan is needed.
   */
  expectScan(wish: string | null, change: boolean): boolean {
    if (this.session && (this.busy === "reading" || this.busy === "naming")) {
      this.setWish(this.session, cleanWish(wish), change);
      this.expected = null;
      return true;
    }
    this.expected = { wish: cleanWish(wish), change, at: Date.now() };
    return false;
  }

  /**
   * A change ("something crazier") goes on top of the wish, and keeps what was offered out of the next designs. A new
   * ask, or a plain one, starts afresh: "build me a birdhouse" after "what can I build?" may well be answered by the
   * birdhouse already shown.
   */
  private setWish(session: Session, wish: string | null, change: boolean): void {
    if (change) { if (wish) session.change = wish; return; }
    session.wish = wish; session.change = null; session.offered = [];
  }

  rethink(request: string, change: boolean): Promise<boolean> {
    const s = this.session;
    if (!s || !this.canRethink()) return Promise.resolve(false);
    const text = cleanWish(request);
    if (text) this.setWish(s, text, change);
    const photo = s.photo && existsSync(s.photo) ? readFileSync(s.photo) : null;
    this.enqueue(() => { const asked = askedFor(s); this.broadcastInventory(s, s.twins, null, true, asked ? `Designing ${asked}…` : "Thinking again…"); return this.ideas(s, photo); });
    return Promise.resolve(true);
  }

  async startIdea(ideaId: string): Promise<{ assembly_id: string; plan_id: string; revision: number }> {
    const session = this.session;                                      // a replay may swap this.session while the run is being made
    const idea = session?.ideas.find((i) => i.idea_id === ideaId);
    if (!session || !idea) throw notFound(`build idea ${ideaId}`);
    const { store } = this.ctx;
    const { revision } = store.putDraft(idea.plan);
    store.approve(idea.plan.plan_id, revision, "build mode");
    store.putSeed({ seed: "build_start", plan_id: idea.plan.plan_id, built: ["part_surface"] });
    const assembly = await store.createAssembly({ plan_id: idea.plan.plan_id, revision, seed: "build_start", name: `Build: ${idea.title}` });
    session.started = idea.idea_id;
    return { assembly_id: assembly.assembly_id, plan_id: idea.plan.plan_id, revision };
  }

  /** The Director's "add a missed object": its standard size, standing in the middle of the main surface. */
  addObject(name: string): Twin {
    const item = this.deps.vocab.get(name);
    const std = item ? standardShape(item) : null;
    if (!item || !std) throw badRequest(`${name} is not a vocabulary object with a standard size`);
    const s = this.session ?? this.newSession();
    const surface = s.surfaces.find((x) => x.kind === "table") ?? s.surfaces[0] ?? null;
    const shape: Twin["shape"] = std.type === "cylinder" ? std : { type: "box", size: flatSize(std.size) };
    const [x, z] = surface ? [(surface.min[0] + surface.max[0]) / 2, (surface.min[1] + surface.max[1]) / 2] : [0, 0.5];
    const y = surface?.y ?? 0.74;
    s.twins = appendTwin(s.twins, {
      twin_id: "o0", name, label: item.label, shape, position: [x, y + heightOf(shape) / 2, z], yaw_deg: 0, sits_on: surface?.surface_id ?? null,
      material: item.material, load_bearing: item.load_bearing, cuttable: item.cuttable, confidence: 1, error_m: 0.003, points: 0,
      distance_m: 0, bbox_px: null, snapped: true, scan_ids: [],
    });
    this.broadcastInventory(s, s.twins, null, true, null);
    const photo = s.photo && existsSync(s.photo) ? readFileSync(s.photo) : null;
    this.enqueue(() => this.ideas(s, photo));
    return s.twins.at(-1)!;
  }

  private enqueue(work: () => Promise<void>): void {
    this.queue = this.queue.then(work).catch((err) => this.deps.log.error({ err: (err as Error).message }, "build mode step failed"));
  }

  /**
   * A session that has been replaced (the Director's New session or Replay, a scan with no session id) goes quiet and
   * stops: the headset adopts the session of whatever inventory it hears, so late names from the old one would pull it
   * back, and every pick of an old idea then answers 404.
   */
  private replaced = (session: Session) => session !== this.session;

  private async process(session: Session, scan: BuildScan, photo: Buffer, labels: "saved" | "live"): Promise<void> {
    if (this.replaced(session)) return;
    this.busy = "reading";
    try {
      const cloud = decodeScan(scan);
      const built = buildTwins(cloud, scan.scan_id);
      const { surfaces, idMap } = mergeSurfaces(session.surfaces, built.surfaces);
      const incoming = built.twins.map((t) => ({ ...t, sits_on: t.sits_on ? idMap.get(t.sits_on) ?? t.sits_on : null }));
      session.surfaces = surfaces; session.camera = scan.camera.position; session.forward = scan.camera.forward; session.scans.push(scan.scan_id);
      session.photo = join(this.files.scanDir(scan.scan_id), "photo.jpg");
      this.broadcastInventory(session, mergeTwins(session.twins, incoming), scan.scan_id, false,
        incoming.length ? null : "I couldn't see any objects. Try looking at them from a little closer.");
      this.busy = "naming";
      const named = await this.label(scan, photo, incoming, surfaces, cloud, labels);
      if (this.replaced(session)) return;
      session.twins = fixSizes(mergeTwins(session.twins, named.twins), this.deps.vocab);
      this.broadcastInventory(session, session.twins, scan.scan_id, true, [named.note, this.seeing(session)].filter(Boolean).join(" ") || null);
      await this.ideas(session, photo);
    } catch (err) {
      // A schema error's message is a page of JSON, and this sentence is read on the HUD.
      const why = err instanceof ZodError ? "its saved data is not in the form I expect." : (err as Error).message;
      this.broadcastInventory(session, session.twins, scan.scan_id, true, `I couldn't read that scan: ${why}`);
      throw err;
    } finally {
      this.busy = null;
      this.files.saveSession(session);
    }
  }

  /** Names for this scan's twins: its saved labels on a replay, else nameTwins (the vision model, or sizes alone). */
  private async label(scan: BuildScan, photo: Buffer, incoming: Twin[], surfaces: Surface[], cloud: Cloud, mode: "saved" | "live"): Promise<{ twins: Twin[]; note: string | null }> {
    if (mode === "saved") { const saved = this.files.readLabels(scan.scan_id); if (saved) return { twins: saved, note: null }; }
    if (incoming.length === 0) return { twins: incoming, note: null };
    const { twins, by } = await nameTwins(
      { cfg: this.ctx.cfg, ai: this.deps.ai("label"), vocab: this.deps.vocab, timeoutMs: 15_000, log: this.deps.log },
      photo, incoming, surfaces, cloud);
    if (by === "vision") {
      // Saved for replays only: a full disk must not cost this scan its names.
      try { this.files.saveLabels(scan.scan_id, twins); } catch (err) { this.deps.log.warn({ err: (err as Error).message }, "could not save the scan's labels"); }
      return { twins, note: null };
    }
    const named = twins.filter((t) => t.name !== "unknown").length;
    return { twins, note: named ? `I named ${named} of ${twins.length} objects by their size alone.` : "I can see objects but couldn't name them. Add them from the laptop, or ask again." };
  }

  private async ideas(session: Session, photo: Buffer | null): Promise<void> {
    const ai = this.deps.ai("ideas");
    const busyBefore = this.busy;
    this.busy = "designing";
    try {
      await this.design(session, photo, ai);
    } finally { this.busy = busyBefore; }
  }

  /** "I see three tall cans and a pizza box. Designing a birdhouse…": what the HUD says once the objects have names. */
  private seeing(session: Session): string | null {
    const found = describeFound(session.twins);
    const asked = askedFor(session);
    return found ? `I see ${found}. ${asked ? `Designing ${asked}…` : "Working out what they could become…"}` : null;
  }

  private track(work: Promise<unknown>): void {
    const done = work.catch(() => {}).finally(() => this.background.delete(done));
    this.background.add(done);
  }

  private async design(session: Session, photo: Buffer | null, ai: AiCall | null): Promise<void> {
    await computeIdeas(
      { cfg: this.ctx.cfg, vocab: this.deps.vocab, rules: this.deps.rules, call: ai?.call ?? null, model: ai?.model ?? "none",
        cacheDir: join(this.files.root, "idea-cache"), timeoutMs: 20_000, liveMs: this.ctx.cfg.buildLiveMs, log: this.deps.log,
        background: (work) => this.track(work),
        note: (text) => this.broadcastInventory(session, session.twins, null, true, text) },
      { sessionId: session.session_id, twins: session.twins, surfaces: session.surfaces, camera: session.camera ?? [0, 1.6, 0], photo, request: askedFor(session),
        // The newest change may name a design shown before ("make the laptop riser taller"): that one may come back.
        offered: session.offered.filter((title) => !(session.change && normalise(session.change).includes(normalise(title)))) },
      (ideas, final) => {
        if (this.replaced(session)) return;
        session.ideas = ideas;
        if (final) session.offered = [...new Set([...session.offered, ...ideas.map((i) => i.title)])];
        const message = final ? summary(session.twins, ideas) : null;
        const audio = message ? this.ctx.hooks.say?.(message) ?? null : null;
        this.ctx.store.bus.emit("broadcast", { type: "build_ideas", session_id: session.session_id, ideas, final, audio_url: audio?.audio_url ?? null, message });
      },
    );
  }

  private broadcastInventory(session: Session, twins: Twin[], scanId: string | null, labelled: boolean, message: string | null): void {
    if (this.replaced(session)) return;
    this.ctx.store.bus.emit("broadcast", { type: "build_inventory", inventory: { session_id: session.session_id, scan_id: scanId, labelled, surfaces: session.surfaces, twins, message } });
  }
}
