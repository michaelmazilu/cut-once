import { existsSync, readdirSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { ulid } from "ulid";
import { S, type Assembly, type BuildEvent, type BuildState, type Plan, type Seed, type ValidationIssue } from "@cutonce/schemas";
import { fold, hasErrors, validatePlan } from "@cutonce/project-model";
import { ApiError, notFound } from "../errors.js";
import { Bus } from "./bus.js";
import { appendLine, ensureDir, readJson, readLines, writeJsonAtomic } from "./fs.js";

export class NoOpError extends ApiError {
  constructor(partId: string, state: string) { super(409, "no_op", `${partId} is already ${state}`); }
}

interface Loaded { assembly: Assembly; plan: Plan; events: BuildEvent[]; byId: Map<string, BuildEvent>; head: number; state: BuildState }

/** Files on disk are the record. Everything in memory here can be rebuilt from DATA_DIR. */
export class Store {
  private loaded = new Map<string, Loaded>();
  private locks = new Map<string, Promise<unknown>>();

  constructor(public readonly dataDir: string, public readonly bus: Bus = new Bus()) {
    for (const d of ["plans", "assemblies", "seeds", "documents", "jobs", "audio"]) ensureDir(join(dataDir, d));
  }

  // ── plans ───────────────────────────────────────────────────────────────────
  /** Plan ids come from URLs, so they are checked before they ever touch a path. */
  private planDir(planId: string) {
    if (!/^plan_[a-z0-9_]+$/.test(planId)) throw notFound(`plan ${planId}`);
    return join(this.dataDir, "plans", planId);
  }

  revisions(planId: string): number[] {
    const dir = this.planDir(planId);
    if (!existsSync(dir)) return [];
    return readdirSync(dir).map((f) => /^rev-(\d+)\.json$/.exec(f)?.[1]).filter((n): n is string => !!n).map(Number).sort((a, b) => a - b);
  }

  // ── plan assets (mesh files, kept beside the plan that names them) ──
  private static ASSET_NAME = /^[a-z0-9][a-z0-9_-]*\.(glb|gltf|png|jpg)$/;

  /**
   * Copies a plan's mesh file next to it when missing or when the source changed (a regenerated model),
   * so the served model always matches the committed one. Returns whether it wrote anything.
   */
  syncAsset(planId: string, name: string, sourcePath: string): boolean {
    if (!Store.ASSET_NAME.test(name) || !existsSync(sourcePath)) return false;
    const target = join(this.planDir(planId), "assets", name);
    const source = readFileSync(sourcePath);
    if (existsSync(target) && readFileSync(target).equals(source)) return false;
    ensureDir(dirname(target));
    const tmp = `${target}.${process.pid}.tmp`;
    writeFileSync(tmp, source);
    renameSync(tmp, target);
    return true;
  }

  assetPath(planId: string, name: string): string | null {
    if (!Store.ASSET_NAME.test(name)) return null;
    const p = join(this.planDir(planId), "assets", name);
    return existsSync(p) ? p : null;
  }

  approvedRevision = (planId: string): number | null => readJson<{ revision: number }>(join(this.planDir(planId), "approved.json"))?.revision ?? null;

  listPlans(): { plan_id: string; revisions: number[]; approved: number | null }[] {
    const dir = join(this.dataDir, "plans");
    return readdirSync(dir).filter((d) => /^plan_[a-z0-9_]+$/.test(d)).map((plan_id) => ({ plan_id, revisions: this.revisions(plan_id), approved: this.approvedRevision(plan_id) }))
      .filter((p) => p.revisions.length > 0);
  }

  /** Without a revision: the approved one, else the latest draft. */
  getPlan(planId: string, revision?: number): Plan {
    const rev = revision ?? this.approvedRevision(planId) ?? this.revisions(planId).at(-1);
    const plan = rev ? readJson<Plan>(join(this.planDir(planId), `rev-${rev}.json`)) : null;
    if (!plan) throw notFound(`plan ${planId}${revision ? ` revision ${revision}` : ""}`);
    return plan;
  }

  /** Stores a new draft revision. Validation issues are returned and saved with it, never thrown. */
  putDraft(input: unknown): { plan: Plan; revision: number; validation: ValidationIssue[] } {
    const base = S.Plan.safeParse(input);
    const planId = base.success ? base.data.plan_id : (input as { plan_id?: string })?.plan_id;
    if (!planId || !/^plan_[a-z0-9_]+$/.test(planId)) throw new ApiError(422, "invalid_plan", "plan_id is missing or malformed");
    const revision = (this.revisions(planId).at(-1) ?? 0) + 1;
    const candidate = { ...(input as object), revision, status: "draft" } as Plan;
    const validation = validatePlan({ ...candidate, provenance: { ...candidate.provenance, validation: [] } });
    if (!base.success) throw new ApiError(422, "invalid_plan", "the plan does not match the schema", validation);
    const plan: Plan = { ...base.data, revision, status: "draft", provenance: { ...base.data.provenance, approved_by: undefined, validation } };
    writeJsonAtomic(join(this.planDir(planId), `rev-${revision}.json`), plan);
    return { plan, revision, validation };
  }

  approve(planId: string, revision: number, approvedBy: string): Plan {
    const plan = this.getPlan(planId, revision);
    const validation = validatePlan({ ...plan, provenance: { ...plan.provenance, validation: [] } });
    if (hasErrors(validation)) throw new ApiError(409, "validation_errors", "a plan with validation errors cannot be approved", validation);
    const approved: Plan = { ...plan, status: "approved", provenance: { ...plan.provenance, approved_by: approvedBy, validation } };
    writeJsonAtomic(join(this.planDir(planId), `rev-${revision}.json`), approved);
    writeJsonAtomic(join(this.planDir(planId), "approved.json"), { revision, approved_by: approvedBy, approved_at: new Date().toISOString() });
    this.bus.emit("plan_ready", { plan_id: planId, revision });
    return approved;
  }

  /** Boot-time seeding of a plan that is already approved (the blank plan; for tests, the desk and fixtures). */
  importApproved(plan: Plan): boolean {
    if (this.revisions(plan.plan_id).includes(plan.revision)) return false;
    writeJsonAtomic(join(this.planDir(plan.plan_id), `rev-${plan.revision}.json`), plan);
    if ((this.approvedRevision(plan.plan_id) ?? 0) < plan.revision) {
      writeJsonAtomic(join(this.planDir(plan.plan_id), "approved.json"), { revision: plan.revision, approved_by: plan.provenance.approved_by ?? "import", approved_at: new Date().toISOString() });
    }
    return true;
  }

  // ── seeds ───────────────────────────────────────────────────────────────────
  putSeed = (seed: Seed) => writeJsonAtomic(join(this.dataDir, "seeds", `${seed.seed}.json`), seed);
  getSeed(name: string): Seed {
    const seed = /^[a-z0-9_]+$/.test(name) ? readJson<Seed>(join(this.dataDir, "seeds", `${name}.json`)) : null;
    if (!seed) throw notFound(`seed ${name}`);
    return seed;
  }
  listSeeds = (): string[] => readdirSync(join(this.dataDir, "seeds")).filter((f) => f.endsWith(".json")).map((f) => f.replace(/\.json$/, "")).sort();

  // ── assemblies ──────────────────────────────────────────────────────────────
  private asmDir = (aid: string) => join(this.dataDir, "assemblies", aid);

  private serial<T>(key: string, work: () => T | Promise<T>): Promise<T> {
    const run = (this.locks.get(key) ?? Promise.resolve()).then(work, work);
    this.locks.set(key, run.catch(() => undefined));
    return run;
  }

  private load(aid: string): Loaded {
    const hit = this.loaded.get(aid);
    if (hit) return hit;
    const assembly = /^asm_[a-z0-9_]+$/.test(aid) ? readJson<Assembly>(join(this.asmDir(aid), "assembly.json")) : null;
    if (!assembly) throw notFound(`assembly ${aid}`);
    const plan = this.getPlan(assembly.plan_id, assembly.plan_revision);
    const events = readLines(join(this.asmDir(aid), "events.jsonl")).map((l) => JSON.parse(l) as BuildEvent);
    const entry: Loaded = { assembly, plan, events, byId: new Map(events.map((e) => [e.event_id, e])), head: events.at(-1)?.version ?? 0, state: fold(plan, aid, events) };
    this.loaded.set(aid, entry);
    return entry;
  }

  getAssembly = (aid: string): Assembly => this.load(aid).assembly;
  planOf = (aid: string): Plan => this.load(aid).plan;

  currentAssembly(): Assembly | null {
    const cur = readJson<{ assembly_id: string }>(join(this.dataDir, "assemblies", "current.json"));
    try { return cur ? this.getAssembly(cur.assembly_id) : null; } catch { return null; }
  }

  async createAssembly(input: { plan_id?: string; revision?: number; seed: string; name?: string }): Promise<Assembly> {
    return this.serial("assemblies", async () => {
      const seed = this.getSeed(input.seed);
      const planId = input.plan_id ?? seed.plan_id;
      const revision = input.revision ?? this.approvedRevision(planId);
      if (!revision) throw new ApiError(409, "plan_not_approved", `plan ${planId} has no approved revision`);
      const plan = this.getPlan(planId, revision);
      if (plan.status !== "approved") throw new ApiError(409, "plan_not_approved", `plan ${planId} revision ${revision} is a draft`);
      const unknown = seed.built.filter((id) => !plan.parts.some((p) => p.part_id === id));
      if (unknown.length) throw new ApiError(409, "seed_mismatch", `seed ${seed.seed} names parts the plan does not have: ${unknown.join(", ")}`);

      const n = readdirSync(join(this.dataDir, "assemblies")).filter((d) => d.startsWith("asm_run_")).length + 1;
      const assembly: Assembly = {
        assembly_id: `asm_run_${String(n).padStart(3, "0")}`, plan_id: planId, plan_revision: revision,
        name: input.name ?? `Run ${n} (${seed.seed})`, seed: seed.seed, created_at: new Date().toISOString(), status: "active",
      };
      writeJsonAtomic(join(this.asmDir(assembly.assembly_id), "assembly.json"), assembly);
      const order = new Map(plan.steps.map((s) => [s.step_id, s.index]));
      const built = plan.parts.filter((p) => seed.built.includes(p.part_id)).sort((a, b) => (order.get(a.step_id) ?? 0) - (order.get(b.step_id) ?? 0));
      const now = Date.now();
      built.forEach((part, i) => {
        const ts = new Date(now + i).toISOString();
        const e: BuildEvent = {
          event_id: `evt_${ulid(now + i)}`, assembly_id: assembly.assembly_id, version: i + 1, timestamp: ts, client_timestamp: ts,
          kind: "part_state", part_id: part.part_id, previous_state: "missing", new_state: "built", source: "seed", confidence: 1,
          actor: "seed", step_id: part.step_id, note: `seed ${seed.seed}`,
        };
        appendLine(join(this.asmDir(assembly.assembly_id), "events.jsonl"), JSON.stringify(e));
      });
      writeJsonAtomic(join(this.dataDir, "assemblies", "current.json"), { assembly_id: assembly.assembly_id });
      const entry = this.load(assembly.assembly_id);
      for (const [i, e] of entry.events.entries()) this.bus.emit("event_appended", { assembly, event: e, head: e.version!, previous: entry.events[i - 1] ?? null });
      this.bus.emit("assembly_changed", { assembly });
      return assembly;
    });
  }

  // ── events ──────────────────────────────────────────────────────────────────
  getEvents(aid: string, after = 0): { events: BuildEvent[]; head: number } {
    const e = this.load(aid);
    return { events: e.events.filter((ev) => (ev.version ?? 0) > after), head: e.head };
  }

  getState(aid: string, version?: number): BuildState {
    const e = this.load(aid);
    return version === undefined || version >= e.head ? e.state : fold(e.plan, aid, e.events, version);
  }

  /** Idempotent on event_id. The server owns `version` and `timestamp`; the client's clock is kept as client_timestamp. */
  appendEvent(aid: string, input: unknown): Promise<{ status: "created" | "replayed"; event: BuildEvent; head: number }> {
    return this.serial(aid, () => {
      const entry = this.load(aid);
      const parsed = S.BuildEvent.safeParse({ ...(input as object), assembly_id: aid, version: null });
      if (!parsed.success) throw new ApiError(422, "invalid_event", parsed.error.issues.map((i) => `${i.path.join(".")}: ${i.message}`).join("; "));
      const incoming = parsed.data;

      const seen = entry.byId.get(incoming.event_id);
      if (seen) return { status: "replayed" as const, event: seen, head: entry.head };

      let note = incoming.note;
      if (incoming.part_id) {
        const status = entry.state.parts[incoming.part_id];
        if (!status) throw new ApiError(422, "unknown_part", `${incoming.part_id} is not in plan ${entry.plan.plan_id} revision ${entry.plan.revision}`);
        if (incoming.kind === "part_state") {
          if (incoming.new_state === status.state) throw new NoOpError(incoming.part_id, status.state);
          if (incoming.previous_state !== status.state) note = [note, `stale_previous:${incoming.previous_state}`].filter(Boolean).join(" ");
          incoming.previous_state = status.state; // the log records what was true, not what the client believed
        }
      }
      const part = incoming.part_id ? entry.plan.parts.find((p) => p.part_id === incoming.part_id) : undefined;
      const event: BuildEvent = { ...incoming, version: entry.head + 1, timestamp: new Date().toISOString(), step_id: incoming.step_id ?? part?.step_id, ...(note ? { note } : {}) };

      appendLine(join(this.asmDir(aid), "events.jsonl"), JSON.stringify(event));
      const previous = entry.events.at(-1) ?? null;
      entry.events.push(event); entry.byId.set(event.event_id, event); entry.head = event.version!;
      entry.state = fold(entry.plan, aid, entry.events);
      this.bus.emit("event_appended", { assembly: entry.assembly, event, head: entry.head, previous });
      return { status: "created" as const, event, head: entry.head };
    });
  }

  listAssemblies = (): string[] => readdirSync(join(this.dataDir, "assemblies")).filter((d) => d.startsWith("asm_")).sort();
}
