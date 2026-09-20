/** Portable laptop → HTTPS tunnel → Quest setup. No Bash, token printing, or APK rebuild required. */
import { spawn, spawnSync, type ChildProcess } from "node:child_process";
import { randomBytes } from "node:crypto";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseEnv } from "node:util";
import { PROJECT, adbPath, editorVersion, unityPath } from "./unity.js";

const ROOT = resolve(PROJECT, "../..");
const ENV = join(ROOT, ".env.local");
const STATE = join(ROOT, "data/runtime/tunnel.json");
const PACKAGE = "com.cutonce.quest";
export type Env = Record<string, string | undefined>;

export function setEnvValue(text: string, key: string, value: string): string {
  const line = `${key}=${JSON.stringify(value)}`;
  const pattern = new RegExp(`^(?:export\\s+)?${key}\\s*=.*$`, "gm");
  return pattern.test(text) ? text.replace(pattern, () => line) : `${text.trimEnd()}\n${line}\n`;
}

export function prepareEnv(text: string, voice = false): string {
  const env = parseEnv(text);
  if (!env.API_TOKEN?.trim() || env.API_TOKEN === "dev-token") text = setEnvValue(text, "API_TOKEN", randomBytes(24).toString("hex"));
  if (voice) {
    text = setEnvValue(text, "COPILOT_MODE", "live");
    if (!env.OMNI_API_KEY) text = setEnvValue(text, "KIT_AI", "openai");
  }
  return text;
}

function settings(): Env {
  return { ...(existsSync(ENV) ? parseEnv(readFileSync(ENV, "utf8")) : {}), ...process.env };
}

function setup(voice: boolean) {
  const before = readFileSync(existsSync(ENV) ? ENV : join(ROOT, ".env.example"), "utf8");
  const after = prepareEnv(before, voice);
  if (!existsSync(ENV) || after !== before) writeFileSync(ENV, after, { mode: 0o600 });
  console.log(voice ? "Live copilot enabled in .env.local. Existing keys and model choices preserved." : "Server configuration ready.");
  if (voice) console.log("Add OPENAI_API_KEY, ELEVENLABS_API_KEY and ELEVENLABS_VOICE_ID to .env.local, then run pnpm copilot:check.");
}

export function serverUrl(raw: string): string {
  const url = new URL(raw);
  if (!["https:", "http:"].includes(url.protocol) || url.username || url.password || url.search || url.hash || url.pathname !== "/")
    throw new Error("Use a server origin such as https://your-tunnel.trycloudflare.com (no path, credentials or query).");
  return url.origin;
}

export function headsetConfig(url: string, token: string, device = "quest-1") {
  if (!token?.trim() || token === "dev-token") throw new Error("Set a non-default API_TOKEN in .env.local first (pnpm copilot:setup).");
  return { server_url: serverUrl(url), api_token: token, device_id: device };
}

export function voiceProblems(env: Env): string[] {
  const issues: string[] = [];
  if (env.COPILOT_MODE !== "live") issues.push("COPILOT_MODE must be live; off disables the route and fake ignores your actual question.");
  for (const key of ["API_TOKEN", "OPENAI_API_KEY", "ELEVENLABS_API_KEY", "ELEVENLABS_VOICE_ID"])
    if (!env[key]?.trim()) issues.push(`${key} is missing in .env.local.`);
  if (env.API_TOKEN === "dev-token") issues.push("Replace the default API_TOKEN (pnpm copilot:setup).");
  return issues;
}

export async function checkServer(url: string, token: string, request: typeof fetch = fetch) {
  const base = serverUrl(url);
  const health = await request(`${base}/health`, { redirect: "error", signal: AbortSignal.timeout(8000) });
  if (!health.ok) throw new Error(`Server health returned HTTP ${health.status}.`);
  const info = await health.json() as { ok?: boolean; copilot?: string; openai?: string; tts?: string; current_assembly?: string };
  if (!info.ok || typeof info.copilot !== "string") throw new Error("This URL is not answering as the Cut Once server.");
  const auth = await request(`${base}/v1/assemblies/current`, {
    headers: { authorization: `Bearer ${token}` }, redirect: "error", signal: AbortSignal.timeout(8000),
  });
  if (auth.status === 401) throw new Error("API_TOKEN does not match the running server. Restart the server after editing .env.local.");
  if (!auth.ok) throw new Error(`Current build returned HTTP ${auth.status}. Start the backend and let its initial run load.`);
  return info;
}

function localUrl(env: Env) { return `http://127.0.0.1:${env.PORT || 8080}`; }
function executable(command: string, args: string[], env = process.env) {
  const result = spawnSync(command, args, { encoding: "utf8", env, windowsHide: true });
  if (result.error || result.status !== 0) throw new Error(`${command} failed: ${result.error?.message || result.stderr || result.stdout}`);
  return result.stdout;
}

export function selectDevice(output: string, serial?: string): string {
  const devices = output.split(/\r?\n/).map(line => line.trim().split(/\s+/)).filter(parts => parts[1] === "device");
  if (serial) {
    if (!devices.some(parts => parts[0] === serial)) throw new Error("Requested Quest is not connected/authorized. Check USB debugging.");
    return serial;
  }
  if (devices.length > 1) throw new Error("Multiple devices connected. Set ANDROID_SERIAL to your Quest's serial and retry.");
  if (!devices.length) throw new Error(/unauthorized/.test(output) ? "Put on the Quest and allow USB debugging." : "No Quest connected. Connect a USB data cable and enable developer mode.");
  return devices[0][0];
}

async function connect(url?: string) {
  const env = settings();
  if (!url && existsSync(STATE)) url = JSON.parse(readFileSync(STATE, "utf8")).url;
  if (!url) throw new Error("Start pnpm tunnel first, or use pnpm quest:connect https://your-server-address.");
  const config = headsetConfig(url, env.API_TOKEN || "", env.QUEST_DEVICE_ID || "quest-1");
  await checkServer(config.server_url, config.api_token);
  const unity = unityPath(editorVersion(readFileSync(join(PROJECT, "ProjectSettings/ProjectVersion.txt"), "utf8")), process.platform, env);
  const adb = adbPath(unity, process.platform, env);
  const serial = selectDevice(executable(adb, ["devices"]), env.ANDROID_SERIAL);
  const command = (...args: string[]) => executable(adb, ["-s", serial, ...args]);
  if (!command("shell", "pm", "path", PACKAGE).includes("package:")) throw new Error("Cut Once is not installed. Run pnpm quest:install first.");
  const temp = mkdtempSync(join(tmpdir(), "cutonce-connect-"));
  try {
    const file = join(temp, "cutonce.config.json");
    writeFileSync(file, JSON.stringify(config), { mode: 0o600 });
    const remote = `/sdcard/Android/data/${PACKAGE}/files`;
    command("shell", "mkdir", "-p", remote);
    command("push", file, `${remote}/cutonce.config.json`);
    command("shell", "am", "force-stop", PACKAGE);
    command("shell", "monkey", "-p", PACKAGE, "-c", "android.intent.category.LAUNCHER", "1");
    console.log(`Quest configured and relaunched → ${config.server_url}\nToken copied privately. No APK rebuild needed. Keep the backend and tunnel running.`);
  } finally { rmSync(temp, { recursive: true, force: true }); }
}

async function doctor(url?: string) {
  const env = settings();
  const issues = voiceProblems(env);
  console.log(`Voice mode: ${env.COPILOT_MODE || "off"}\nAnswer model: ${env.OPENAI_COPILOT_MODEL || env.OPENAI_MODEL || "gpt-5.6-luna"}\nTranscription: ${env.OPENAI_STT_MODEL || "gpt-transcribe"}`);
  for (const issue of issues) console.error(`FIX: ${issue}`);
  try {
    const health = await checkServer(url || localUrl(env), env.API_TOKEN || "");
    console.log(`Server reachable; token accepted; copilot=${health.copilot}; OpenAI=${health.openai}; speech=${health.tts}.`);
    if (health.copilot !== "live" || health.openai !== "set" || health.tts !== "set") issues.push("Restart pnpm serve:local after configuring live voice keys.");
  } catch (error) { issues.push((error as Error).message); }
  if (issues.length) throw new Error(`Voice setup is not ready. ${issues[issues.length - 1]}`);
  console.log("Configuration and server checks passed. This does not validate provider billing/model access. Run pnpm llm:smoke and pnpm tts:smoke, then test A on the Quest.");
}

async function runChild(child: ChildProcess): Promise<boolean> {
  let cancelled = false;
  const stop = () => { cancelled = true; child.kill(); };
  process.once("SIGINT", stop); process.once("SIGTERM", stop);
  try {
    await new Promise<void>((ok, fail) => {
      child.once("error", fail);
      child.once("exit", (code) => code === 0 || cancelled ? ok() : fail(new Error(`Process exited with code ${code}.`)));
    });
    return !cancelled;
  } finally { process.off("SIGINT", stop); process.off("SIGTERM", stop); }
}

async function serve() {
  setup(false);
  const pnpm = process.env.npm_execpath;
  if (!pnpm) throw new Error("Run this through pnpm serve:local.");
  if (!await runChild(spawn(process.execPath, [pnpm, "-F", "@cutonce/web", "build"], { cwd: ROOT, stdio: "inherit" }))) return;
  await runChild(spawn(process.execPath, ["--import", "tsx", "services/api/src/server.ts"], {
    cwd: ROOT, stdio: "inherit", env: { ...settings(), HOST: "0.0.0.0", NODE_ENV: "production" },
  }));
}

export function tunnelAddress(log: string): string | undefined {
  return log.match(/https:\/\/(?!api\.)[a-z0-9-]+\.trycloudflare\.com\b/)?.[0];
}

async function tunnel(name?: string, host?: string) {
  if (Boolean(name) !== Boolean(host)) throw new Error("Use pnpm tunnel, or pnpm tunnel <name> https://your-domain.");
  const env = settings();
  headsetConfig(localUrl(env), env.API_TOKEN || "");
  await checkServer(localUrl(env), env.API_TOKEN!);
  const binary = env.CLOUDFLARED || "cloudflared";
  try { executable(binary, ["--version"]); } catch { throw new Error("Install cloudflared first: Windows: winget install --id Cloudflare.cloudflared --exact; Mac: brew install cloudflared."); }
  const args = name ? ["tunnel", "--no-autoupdate", "run", "--url", localUrl(env), name] : ["tunnel", "--no-autoupdate", "--url", localUrl(env)];
  console.log("Opening tunnel; waiting for Cloudflare and the public server check (up to two minutes)...");
  let log = "", stopped = false, cancelled = false, failure: Error | undefined;
  let url = host ? serverUrl(host) : undefined;
  let healthError = "No registered tunnel connection yet.";
  const child = spawn(binary, args, { stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
  const stop = () => { cancelled = true; stopped = true; child.kill(); };
  process.once("SIGINT", stop); process.once("SIGTERM", stop);
  child.once("error", error => { failure = error; });
  child.once("exit", () => { stopped = true; });
  const append = (data: Buffer) => {
    log = (log + data.toString()).slice(-24_000);
    const discovered = tunnelAddress(log);
    if (!url && discovered) {
      url = discovered;
      console.log(`Address assigned: ${url}; checking connection...`);
    }
  };
  child.stdout.on("data", append); child.stderr.on("data", append);
  let awake: ChildProcess | undefined;
  try {
    const deadline = Date.now() + 120_000;
    let ready = false;
    while (!stopped && !failure && Date.now() < deadline) {
      if (url && log.includes("Registered tunnel connection")) {
        try { await checkServer(url, env.API_TOKEN!); ready = true; break; }
        catch (error) { healthError = `${(error as Error).message}${(error as Error).cause ? `: ${String((error as Error).cause)}` : ""}`; }
      }
      await new Promise(r => setTimeout(r, 1000));
    }
    if (cancelled) return;
    if (!ready || !url) throw new Error(failure?.message || `Tunnel did not become healthy: ${healthError}\nCheck ${url || "the tunnel address"}/health in a browser.\n${log.slice(-2000)}`);
    mkdirSync(dirname(STATE), { recursive: true });
    writeFileSync(STATE, JSON.stringify({ url, pid: process.pid }), { mode: 0o600 });
    if (process.platform === "darwin") { awake = spawn("caffeinate", ["-ims", "-w", String(process.pid)], { stdio: "ignore" }); awake.on("error", () => {}); }
    console.log(`\nTunnel ready: ${url}\nQuest browser check: ${url}/health\nDirector: ${url}/director\nNext, with Quest attached by USB: pnpm quest:connect\n\nKeep this window open and laptop awake. Ctrl-C stops it. Restarting the tunnel changes a quick-tunnel URL; rerun quest:connect afterward.`);
    while (!stopped) await new Promise(r => setTimeout(r, 500));
    if (!cancelled) throw new Error("cloudflared stopped unexpectedly. Restart pnpm tunnel and reconnect the Quest.");
  } finally {
    stop(); awake?.kill(); process.off("SIGINT", stop); process.off("SIGTERM", stop);
    if (existsSync(STATE) && JSON.parse(readFileSync(STATE, "utf8")).pid === process.pid) rmSync(STATE);
  }
}

export async function main(command: string, args: string[]) {
  if (command === "setup") setup(true);
  else if (command === "serve") await serve();
  else if (command === "tunnel") await tunnel(args[0], args[1]);
  else if (command === "connect") await connect(args[0]);
  else if (command === "check") await doctor(args[0]);
  else throw new Error("Expected setup, serve, tunnel, connect or check.");
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url))
  main(process.argv[2], process.argv.slice(3)).catch(error => { console.error((error as Error).message); process.exitCode = 1; });
