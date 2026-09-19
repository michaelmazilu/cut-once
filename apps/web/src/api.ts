import {
  AssemblySchema, BuildStateSchema, JobSchema, PlanSchema, S,
  type Assembly, type BuildEvent, type BuildIdea, type BuildState, type CopilotContext, type CopilotResponse, type DirectorCommand, type Job, type Plan,
  type BuildScanUpload, type RetrievedChunk, type Twin,
} from "@cutonce/schemas";
import type { ZodTypeAny } from "zod";
import { clearToken, getToken } from "./auth";

/** The demo project. Override with VITE_PROJECT_ID at build time if the server uses another id. */
export const PROJECT_ID: string = import.meta.env.VITE_PROJECT_ID ?? "proj_cutonce_demo";

export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly body?: unknown,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/** A short sentence for the UI from anything thrown by these helpers. */
export function describeError(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 0) return "Cannot reach the server.";
    return `${e.message} (${e.status} ${e.code})`;
  }
  if (e instanceof Error) return e.message;
  return String(e);
}

type Query = Record<string, string | number | undefined | null>;

function withQuery(path: string, query?: Query): string {
  if (!query) return path;
  const qs = new URLSearchParams();
  for (const [k, v] of Object.entries(query)) if (v !== undefined && v !== null && v !== "") qs.set(k, String(v));
  const s = qs.toString();
  return s ? `${path}?${s}` : path;
}

export function authHeaders(): Record<string, string> {
  const token = getToken();
  return token ? { Authorization: `Bearer ${token}` } : {};
}

/** Errors arrive as `{ error: { code, message } }`. Anything else becomes a generic error. */
function toApiError(status: number, body: unknown, fallback: string): ApiError {
  if (body && typeof body === "object" && "error" in body) {
    const err = (body as { error?: { code?: unknown; message?: unknown } }).error;
    if (err && typeof err === "object") {
      return new ApiError(status, String(err.code ?? "error"), String(err.message ?? fallback), body);
    }
  }
  return new ApiError(status, "http_error", fallback, body);
}

async function readBody(res: Response): Promise<unknown> {
  const text = await res.text();
  if (!text) return undefined;
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

/**
 * Check a response against the shared schema. A mismatch is logged, not thrown: during the demo a
 * teammate's slightly different field must not blank the Director page.
 */
function checked<T>(schema: ZodTypeAny, data: unknown, label: string): T {
  const r = schema.safeParse(data);
  if (r.success) return r.data as T;
  console.warn(`[api] ${label} did not match the schema`, r.error.issues);
  return data as T;
}

async function request<T>(
  method: string,
  path: string,
  opts: { query?: Query; body?: unknown; schema?: ZodTypeAny; label?: string; auth?: boolean } = {},
): Promise<T> {
  const url = withQuery(path, opts.query);
  const headers: Record<string, string> = { Accept: "application/json", ...(opts.auth === false ? {} : authHeaders()) };
  let body: BodyInit | undefined;
  if (opts.body !== undefined) {
    headers["Content-Type"] = "application/json";
    body = JSON.stringify(opts.body);
  }
  let res: Response;
  try {
    res = await fetch(url, { method, headers, body });
  } catch (e) {
    throw new ApiError(0, "network", e instanceof Error ? e.message : "Network error");
  }
  const data = await readBody(res);
  if (!res.ok) {
    if (res.status === 401 && opts.auth !== false) clearToken(true);
    throw toApiError(res.status, data, `${method} ${path} failed`);
  }
  return opts.schema ? checked<T>(opts.schema, data, opts.label ?? path) : (data as T);
}

const enc = encodeURIComponent;

// ── health ───────────────────────────────────────────────────────────────────
export interface Health {
  ok: boolean;
  version?: string;
  [dependency: string]: unknown;
}
export const getHealth = () => request<Health>("GET", "/health", { auth: false });

// ── assemblies, events, state ────────────────────────────────────────────────
export const getCurrentAssembly = () =>
  request<Assembly>("GET", "/v1/assemblies/current", { schema: AssemblySchema, label: "Assembly" });

export interface EventsPage {
  events: BuildEvent[];
  head: number;
}
const EventsPageSchema = S.BuildEventBase.array();
export async function getEvents(assemblyId: string, after?: number): Promise<EventsPage> {
  const page = await request<EventsPage>("GET", `/v1/assemblies/${enc(assemblyId)}/events`, { query: { after } });
  const events = checked<BuildEvent[]>(EventsPageSchema, page?.events ?? [], "events");
  return { events, head: typeof page?.head === "number" ? page.head : events.length };
}

export const getState = (assemblyId: string, version?: number) =>
  request<BuildState>("GET", `/v1/assemblies/${enc(assemblyId)}/state`, {
    query: { version }, schema: BuildStateSchema, label: "BuildState",
  });

export const createAssembly = (body: { plan_id: string; revision?: number; seed: string }) =>
  request<Assembly>("POST", "/v1/assemblies", { body, schema: AssemblySchema, label: "Assembly" });

// ── plans ────────────────────────────────────────────────────────────────────
export const getPlan = (planId: string, revision?: number) =>
  request<Plan>("GET", `/v1/plans/${enc(planId)}`, { query: { revision }, schema: PlanSchema, label: "Plan" });

/** Where a plan's mesh files (glb models) are served. Needs the bearer header. */
export const planAssetUrl = (planId: string, name: string) => `/v1/plans/${enc(planId)}/assets/${enc(name)}`;

export const approvePlan = (planId: string, body: { revision: number; approved_by: string }) =>
  request<Plan>("POST", `/v1/plans/${enc(planId)}/approve`, { body, schema: PlanSchema, label: "Plan" });

// ── director ─────────────────────────────────────────────────────────────────
export const sendDirectorCommand = (command: DirectorCommand) =>
  request<{ ok?: boolean } & Record<string, unknown>>("POST", "/v1/director/command", { body: command });

// ── documents and jobs ───────────────────────────────────────────────────────
export interface UploadResult {
  /** 201 = new file, a job was started. 200 = the file's hash matched an already processed plan. */
  status: number;
  document_id: string;
  job_id?: string | null;
  known_plan_id?: string;
  revision?: number;
}

/** Multipart upload with real byte progress (fetch cannot report upload progress, so this uses XHR). */
export function uploadDocument(
  projectId: string,
  file: File,
  onProgress?: (fraction: number) => void,
): { promise: Promise<UploadResult>; abort: () => void } {
  const xhr = new XMLHttpRequest();
  const promise = new Promise<UploadResult>((resolve, reject) => {
    xhr.open("POST", `/v1/projects/${enc(projectId)}/documents`);
    const token = getToken();
    if (token) xhr.setRequestHeader("Authorization", `Bearer ${token}`);
    xhr.setRequestHeader("Accept", "application/json");
    xhr.upload.onprogress = (ev) => {
      if (ev.lengthComputable && onProgress) onProgress(ev.loaded / ev.total);
    };
    xhr.onerror = () => reject(new ApiError(0, "network", "Network error during upload"));
    xhr.onabort = () => reject(new ApiError(0, "aborted", "Upload cancelled"));
    xhr.onload = () => {
      let data: unknown;
      try {
        data = xhr.responseText ? JSON.parse(xhr.responseText) : undefined;
      } catch {
        data = xhr.responseText;
      }
      if (xhr.status < 200 || xhr.status >= 300) {
        if (xhr.status === 401) clearToken(true);
        const fallback = xhr.status === 413 ? "File is too large (the limit is 25 MB)" : "Upload failed";
        reject(toApiError(xhr.status, data, fallback));
        return;
      }
      const d = (data ?? {}) as Record<string, unknown>;
      if (typeof d.document_id !== "string") {
        reject(new ApiError(xhr.status, "bad_response", "The server did not return a document_id", data));
        return;
      }
      resolve({
        status: xhr.status,
        document_id: d.document_id,
        job_id: typeof d.job_id === "string" ? d.job_id : null,
        known_plan_id: typeof d.known_plan_id === "string" ? d.known_plan_id : undefined,
        revision: typeof d.revision === "number" ? d.revision : undefined,
      });
    };
    const form = new FormData();
    form.append("file", file, file.name);
    xhr.send(form);
  });
  return { promise, abort: () => xhr.abort() };
}

export const getJob = (jobId: string) =>
  request<Job>("GET", `/v1/jobs/${enc(jobId)}`, { schema: JobSchema, label: "Job" });

export const pageImagePath = (documentId: string, page: number) =>
  `/v1/documents/${enc(documentId)}/pages/${page}.png`;

/**
 * Page images need the bearer token, which an <img src> cannot send. Fetch the PNG with the header
 * and hand back an object URL (the caller revokes it). Returns null when the page does not exist.
 */
export async function fetchPageImage(documentId: string, page: number, signal?: AbortSignal): Promise<string | null> {
  let res: Response;
  try {
    res = await fetch(pageImagePath(documentId, page), { headers: authHeaders(), signal });
  } catch (e) {
    if (e instanceof DOMException && e.name === "AbortError") throw e;
    throw new ApiError(0, "network", e instanceof Error ? e.message : "Network error");
  }
  if (res.status === 404) return null;
  if (!res.ok) {
    if (res.status === 401) clearToken(true);
    throw toApiError(res.status, await readBody(res), "Could not load the page image");
  }
  return URL.createObjectURL(await res.blob());
}

// ── analytics, search, webhooks ──────────────────────────────────────────────
export type AnalyticsName = "step_durations" | "runs_compared" | "sources_breakdown";
export interface AnalyticsTable {
  columns: { name: string; type: string }[];
  rows: unknown[][];
}
export async function getAnalytics(name: AnalyticsName, assemblyId?: string): Promise<AnalyticsTable> {
  const t = await request<Partial<AnalyticsTable>>("GET", `/v1/analytics/${enc(name)}`, { query: { assembly_id: assemblyId } });
  return { columns: Array.isArray(t?.columns) ? t.columns : [], rows: Array.isArray(t?.rows) ? t.rows : [] };
}

const ChunksSchema = S.RetrievedChunk.array();
export async function searchProject(projectId: string, q: string, partId?: string): Promise<{ chunks: RetrievedChunk[] }> {
  const r = await request<{ chunks?: unknown }>("GET", `/v1/projects/${enc(projectId)}/search`, { query: { q, part_id: partId } });
  return { chunks: checked<RetrievedChunk[]>(ChunksSchema, r?.chunks ?? [], "search chunks") };
}

export const postIssueWebhook = (body: { issue_id: string; part_id: string | null; note: string }) =>
  request<Record<string, unknown>>("POST", "/v1/webhooks/issue", { body });

// ── the pretend headset (/sim) ───────────────────────────────────────────────
/** Appends one build event, exactly as the headset does. 201 on success; 409 no_op when the part already has that state. */
export const postEvent = (assemblyId: string, event: Record<string, unknown>) =>
  request<{ version: number; head: number }>("POST", `/v1/assemblies/${enc(assemblyId)}/events`, { body: event });

/** One copilot question: the context packet, the recorded question and one camera frame (blueprint §10). */
/** A spoken question (`audio`), or a typed one (`question`, with `audio` null). */
export async function askCopilot(assemblyId: string, context: CopilotContext, audio: Blob | null, frame: Blob, question?: string): Promise<CopilotResponse> {
  const form = new FormData();
  form.append("context", JSON.stringify(context));
  if (audio) form.append("audio", audio, "question.wav");
  if (question) form.append("question", question);
  form.append("frame", frame, "frame.jpg");
  let res: Response;
  try {
    res = await fetch(`/v1/assemblies/${enc(assemblyId)}/copilot/query`, { method: "POST", headers: authHeaders(), body: form });
  } catch (e) {
    throw new ApiError(0, "network", e instanceof Error ? e.message : "Network error");
  }
  const data = await readBody(res);
  if (!res.ok) throw toApiError(res.status, data, "the copilot request failed");
  return data as CopilotResponse;
}

/** The answer's audio as a playable WAV object URL (the headset gets raw PCM; a browser needs WAV). */
export async function fetchAnswerAudio(audioUrl: string): Promise<string> {
  const res = await fetch(`${audioUrl}${audioUrl.includes("?") ? "&" : "?"}format=wav`, { headers: authHeaders() });
  if (!res.ok) throw new ApiError(res.status, "audio", `audio ${res.status}`);
  return URL.createObjectURL(await res.blob());
}

// ── copilot (owner: Rhythm) ──────────────────────────────────────────────────
export interface LastCapture {
  received_at?: string;
  frame_bytes?: number;
  audio_bytes?: number;
  note?: string;
  /** The CopilotContext the headset sent, so the panel can draw the projected boxes over the frame. */
  context?: unknown;
}
export const getLastCapture = () => request<LastCapture>("GET", "/v1/copilot/debug/last");

export interface CacheEntry { scripted_query_id: string; transcript: string; promoted_at: string; has_audio: boolean }
export const getCopilotCache = async () =>
  (await request<{ entries?: CacheEntry[] }>("GET", "/v1/copilot/cache")).entries ?? [];

/** The last frame the copilot received. Needs the bearer, so it comes back as an object URL. */
export async function fetchLastFrame(signal?: AbortSignal): Promise<string | null> {
  const res = await fetch("/v1/copilot/debug/frame.jpg", { headers: authHeaders(), signal });
  if (res.status === 404) return null;
  if (!res.ok) return null;
  return URL.createObjectURL(await res.blob());
}

// ── build mode ───────────────────────────────────────────────────────────────
export interface BuildCurrent { session: { session_id: string; created_at: string; scans: string[] } | null; wish: string | null; twins: Twin[]; ideas: BuildIdea[] }
export interface BuildScanRow { scan_id: string; session_id: string | null; captured_at: string | null; recording: boolean }
/** `standard`: the object has a standard size, so it can be added by hand. */
export interface BuildVocabItem { name: string; label: string; standard: boolean }
export const getBuildCurrent = () => request<BuildCurrent>("GET", "/v1/build/sessions/current");
/** One scan, exactly as the headset uploads it (the web kitchen's pretend headset uses this). */
export const postBuildScan = (upload: BuildScanUpload) => request<{ scan_id: string; session_id: string }>("POST", "/v1/build/scans", { body: upload });
export const listBuildScans = () => request<{ scans: BuildScanRow[] }>("GET", "/v1/build/scans");
export const replayBuildScan = (scanId: string, labels: "saved" | "live") => request<{ session_id: string }>("POST", `/v1/build/scans/${enc(scanId)}/replay`, { body: { labels } });
export const startBuildIdea = (ideaId: string) => request<{ assembly_id: string; plan_id: string; revision: number }>("POST", `/v1/build/ideas/${enc(ideaId)}/start`, { body: {} });
export const addBuildObject = (name: string) => request<Twin>("POST", "/v1/build/objects", { body: { name } });
export const newBuildSession = () => request<{ session_id: string }>("POST", "/v1/build/sessions", { body: {} });
export const getBuildVocabulary = () => request<{ items: BuildVocabItem[] }>("GET", "/v1/build/vocabulary");
