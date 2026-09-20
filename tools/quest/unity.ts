/**
 * Runs the Unity project in apps/quest from the command line, the same way on macOS and Windows.
 *
 *   pnpm quest:setup     apply the Quest 3 settings (ours, then Meta's Project Setup Tool fixes)
 *   pnpm quest:check     compile for the Quest, check settings and budgets, run the EditMode tests
 *   pnpm quest:sim       run the baseline scene in Meta XR Simulator and report what it provides
 *   pnpm quest:play      open the app in Meta XR Simulator (Play mode) and leave it running for you
 *   pnpm quest:build     build the APK the headset installs (apps/quest/Builds/CutOnce.apk)
 *   pnpm quest:surface-proof render and verify the surface shader against synthetic measured depth
 *   pnpm quest:recognition-proof run the real detector on pinned photos and check names/box overlap
 *   pnpm quest:convert-model export a pinned full-precision candidate for explicit model comparison
 *   pnpm quest:install   install that APK on a Quest plugged in by USB-C and start it
 *
 * The Editor must be closed: Unity allows one instance per project. With it open, the Cut Once menu runs the
 * same code. Logs land in apps/quest/Logs/cli/.
 */
import { spawn, spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, rmSync, statSync } from "node:fs";
import { homedir, platform } from "node:os";
import { dirname, join, posix, resolve, win32 } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
export const PROJECT = join(ROOT, "apps", "quest");
const LOGS = join(PROJECT, "Logs", "cli");
const APK = join(PROJECT, "Builds", "CutOnce.apk");
const PACKAGE = "com.cutonce.quest";

export function editorVersion(projectVersionTxt: string): string {
  const m = /^m_EditorVersion:\s*(\S+)/m.exec(projectVersionTxt);
  if (!m) throw new Error("ProjectVersion.txt has no m_EditorVersion");
  return m[1];
}

/** Unity Hub's default install folder for each OS. UNITY_PATH overrides it. */
export function unityPath(version: string, os: NodeJS.Platform = platform(), env: NodeJS.ProcessEnv = process.env): string {
  if (env.UNITY_PATH) return env.UNITY_PATH;
  if (os === "darwin") return `/Applications/Unity/Hub/Editor/${version}/Unity.app/Contents/MacOS/Unity`;
  if (os === "win32") return `C:\\Program Files\\Unity\\Hub\\Editor\\${version}\\Editor\\Unity.exe`;
  return join(homedir(), "Unity", "Hub", "Editor", version, "Editor", "Unity");
}

/** The adb that ships inside Unity's Android module, so nobody installs Android Studio. ADB overrides it. */
export function adbPath(unity: string, os: NodeJS.Platform = platform(), env: NodeJS.ProcessEnv = process.env): string {
  if (env.ADB) return env.ADB;
  const p = os === "win32" ? win32 : posix;
  // macOS: <v>/Unity.app/Contents/MacOS/Unity and <v>/PlaybackEngines. Windows and Linux: <v>/Editor/Unity(.exe)
  // and <v>/Editor/Data/PlaybackEngines.
  const engines = os === "darwin"
    ? p.join(p.dirname(p.dirname(p.dirname(p.dirname(unity)))), "PlaybackEngines")
    : p.join(p.dirname(unity), "Data", "PlaybackEngines");
  return p.join(engines, "AndroidPlayer", "SDK", "platform-tools", os === "win32" ? "adb.exe" : "adb");
}

/** Unity can exit successfully after silently omitting scripts/assets whose metadata GUID is invalid. */
export function invalidAssetMetadata(log: string): string[] {
  const messages = log.match(/The \.meta file[^\r\n]*does not have a valid GUID[^\r\n]*/gi) ?? [];
  return [...new Set(messages.map((message) => message.trim()))];
}

/** Preserve process failures and reject successful runs that did not import all their assets. */
export function unityExitCode(status: number | null, log: string): number {
  if (status === 0 && invalidAssetMetadata(log).length > 0) return 1;
  return status ?? 1;
}

/** Turns a failed Unity log into the one sentence that says what to do. */
export function explain(log: string): string[] {
  const out: string[] = [];
  if (/another Unity instance is running|It looks like another Unity instance/i.test(log))
    out.push("The Unity Editor has apps/quest open. Close it, or use the Cut Once menu inside it.");
  if (/AndroidExternalToolsSettings/.test(log))
    out.push("Unity's Android module is missing or half installed. Unity Hub > Installs > the gear > Add modules > Android Build Support, with OpenJDK and Android SDK & NDK Tools.");
  if (/\bNDK\b[^\n]*(not found|missing|not installed|is not set)|(not find|cannot find|missing)[^\n]*\bNDK\b/i.test(log))
    out.push("APK builds need the Android NDK: Unity Hub > Installs > the gear > Add modules > Android SDK & NDK Tools. The simulator does not need it.");
  if (/\bJDK\b[^\n]*(not found|missing|not installed|is not set)|(not find|cannot find|missing)[^\n]*\bJDK\b/i.test(log))
    out.push("Unity cannot find Java: Unity Hub > Installs > the gear > Add modules > OpenJDK.");
  if (/No valid Unity Editor license|License is not active|com\.unity\.editor\.headless/i.test(log))
    out.push("Unity has no licence on this machine. Open Unity Hub and sign in once.");
  const errors = [...new Set(log.split("\n").filter((l) => /error CS\d+/.test(l)).map((l) => l.trim()))];
  if (errors.length) out.push(`${errors.length} compile error(s):`, ...errors.slice(0, 15).map((e) => "  " + e));
  const invalidMetadata = invalidAssetMetadata(log);
  if (invalidMetadata.length) out.push(
    `Unity ignored ${invalidMetadata.length} asset(s) with invalid .meta GUIDs. Restore or correct the GUIDs in those existing .meta files and rerun; omitted scripts and tests can otherwise produce a false pass.`,
    ...invalidMetadata.slice(0, 15).map((message) => "  " + message),
  );
  return out;
}

export type TestSummary = { total: number; passed: number; failed: number; failures: { name: string; message: string }[] };

/** Reads the NUnit XML that Unity's -runTests writes. */
export function readTestResults(xml: string): TestSummary {
  const run = /<test-run[^>]*\btotal="(\d+)"[^>]*\bpassed="(\d+)"[^>]*\bfailed="(\d+)"/.exec(xml);
  const failures: TestSummary["failures"] = [];
  const caseRe = /<test-case\b[^>]*\bfullname="([^"]+)"[^>]*\bresult="Failed"[^>]*>([\s\S]*?)<\/test-case>/g;
  for (let m; (m = caseRe.exec(xml)); ) {
    const msg = /<message>\s*<!\[CDATA\[([\s\S]*?)\]\]>/.exec(m[2]);
    failures.push({ name: m[1], message: (msg?.[1] ?? "").trim() });
  }
  return { total: Number(run?.[1] ?? 0), passed: Number(run?.[2] ?? 0), failed: Number(run?.[3] ?? failures.length), failures };
}

export type Finding = { level: "Error" | "Warning"; area: string; message: string };

/** Where Meta XR Simulator keeps its OpenXR runtime. XRSIM_DIR overrides it. */
export function simulatorDir(os: NodeJS.Platform = platform(), env: NodeJS.ProcessEnv = process.env, regQuery: () => string = queryRegistry): string | null {
  if (env.XRSIM_DIR) return env.XRSIM_DIR;
  if (os === "darwin") return "/Applications/MetaXRSimulator.app/Contents/Resources/MetaXRSimulator";
  if (os !== "win32") return null;
  // Windows: the installer registers an xrsim:// handler whose command is the simulator's executable.
  const m = /REG_SZ\s+"?([^"\r\n]+?\.exe)"?(\s|$)/i.exec(regQuery());
  return m ? win32.dirname(m[1]) : null;
}

function queryRegistry(): string {
  for (const hive of ["HKCU", "HKLM"]) {
    const r = spawnSync("reg", ["query", `${hive}\\SOFTWARE\\Classes\\xrsim\\shell\\open\\command`, "/ve"], { encoding: "utf8" });
    if (r.status === 0 && r.stdout) return r.stdout;
  }
  return "";
}

/** The rooms that ship with the Mac simulator: [server app inside the runtime folder, room id]. */
export const ROOMS: Record<string, [string, string]> = {
  office: ["xrooms_ses/synth_env_server_extra_rooms.app", "XRoom1_2"],
  furnished: ["xrooms_ses/synth_env_server_extra_rooms.app", "XRoom2_4"],
  living: ["synthetic_env_server/synth_env_server.app", "LivingRoom"],
  game: ["synthetic_env_server/synth_env_server.app", "GameRoom"],
  bedroom: ["synthetic_env_server/synth_env_server.app", "Bedroom"],
};
const SES_PORT = 33792; // where a room server listens; the simulator shows a checkerboard without one

/**
 * Starts the simulator window and a room (macOS). A session only reaches FOCUSED with the window running, and
 * passthrough shows the room only while a room server listens (a checkerboard otherwise).
 */
function startSimulator(dir: string, room: string): boolean {
  if (platform() !== "darwin") {
    console.log("  Windows: open Meta XR Simulator and pick a room (Choose environment) before running this.");
    return true;
  }
  const entry = ROOMS[room];
  if (!entry) { console.error(`Unknown room "${room}". Rooms: ${Object.keys(ROOMS).join(", ")}`); return false; }
  const listening = () => spawnSync("lsof", ["-nP", `-iTCP:${SES_PORT}`, "-sTCP:LISTEN"]).status === 0;
  spawnSync("pkill", ["-f", "SyntheticEnvironmentServer"]); // a room left over from an earlier run, or started without a room id
  spawnSync("open", ["-g", "-a", "/Applications/MetaXRSimulator.app"]);
  spawnSync("open", ["-g", "-n", join(dir, entry[0]), "--args", entry[1]]);
  const until = Date.now() + 60_000;
  while (!listening() && Date.now() < until) spawnSync("sleep", ["1"]);
  if (!listening()) { console.error(`The ${room} room did not start (nothing on port ${SES_PORT}).`); return false; }
  console.log(`  room: ${room}`);
  return true;
}

/** What Meta's "Activate" menu item sets. (Meta's CI configuration does not start a session on macOS.) */
export function simulatorEnv(dir: string, os: NodeJS.Platform = platform()): Record<string, string> {
  const p = os === "win32" ? win32 : posix;
  const json = p.join(dir, "meta_openxr_simulator.json");
  return { XR_RUNTIME_JSON: json, XR_SELECTED_RUNTIME_JSON: json, META_XRSIM_CONFIG_JSON: p.join(dir, "config", "sim_core_configuration.json") };
}

const MINUTE = 60_000;

/**
 * Runs Unity once. Every run has a time limit, so a hung Editor ends the command instead of the night; the limit is
 * reported, and so is the reason Unity gave for any other failure.
 */
function unity(label: string, args: string[], opts: { graphics?: boolean; window?: boolean; env?: Record<string, string>; minutes?: number } = {}): { code: number; log: string } {
  mkdirSync(LOGS, { recursive: true });
  const logFile = join(LOGS, `${label}.log`);
  const exe = unityPath(editorVersion(readFileSync(join(PROJECT, "ProjectSettings", "ProjectVersion.txt"), "utf8")));
  if (!existsSync(exe)) {
    console.error(`Unity not found at ${exe}. Install the version in apps/quest/ProjectSettings/ProjectVersion.txt with Unity Hub, or set UNITY_PATH.`);
    return { code: 1, log: "" };
  }
  console.log(`▸ ${label}: Unity ${args.find((a) => a.startsWith("CutOnce.")) ?? args.join(" ")} (log: ${logFile})`);
  const mode = opts.window ? [] : ["-batchmode", ...(opts.graphics ? [] : ["-nographics"])];
  const base = [...mode, "-projectPath", PROJECT, "-buildTarget", "Android", "-logFile", logFile];
  const minutes = opts.minutes ?? 30;
  rmSync(logFile, { force: true }); // a run that never starts must not be explained by the last run's log
  const r = spawnSync(exe, [...base, ...args], { stdio: "inherit", env: { ...process.env, ...opts.env }, timeout: minutes * MINUTE, killSignal: "SIGKILL" });
  const log = existsSync(logFile) ? readFileSync(logFile, "utf8") : "";
  if (r.error && (r.error as NodeJS.ErrnoException).code === "ETIMEDOUT") console.error(`Unity did not finish within ${minutes} minutes and was stopped.`);
  const code = unityExitCode(r.status, log);
  if (code !== 0) explain(log).forEach((l) => console.error(l));
  return { code, log };
}

/** Deletes results from an earlier run, so a failed run can never print them as its own. */
function fresh(...paths: string[]) {
  for (const p of paths) rmSync(p, { force: true });
}

/** The check's findings, or none when the file is missing or was cut short by a stopped run. */
export function readFindings(path: string): Finding[] {
  if (!existsSync(path)) return [];
  try { return (JSON.parse(readFileSync(path, "utf8")) as { findings: Finding[] }).findings ?? []; }
  catch { return []; }
}

function syncFixtures() {
  spawnSync(process.execPath, [join(ROOT, "scripts", "sync-fixtures.mjs")], { stdio: "inherit" });
}

function setup(): number {
  return unity("setup", ["-executeMethod", "CutOnce.QuestTools.Batch.Setup"]).code;
}

function check(): number {
  syncFixtures();
  const reportPath = join(LOGS, "quest-check.json");
  const metaReport = join(LOGS, "meta-setup-report.json");
  const xml = join(LOGS, "editmode-results.xml");
  fresh(reportPath, metaReport, xml);

  const c = unity("check", ["-executeMethod", "CutOnce.QuestTools.Batch.Check", "-cutonceReport", reportPath, "-reportFile", metaReport]);
  const findings = readFindings(reportPath);
  const errors = findings.filter((f) => f.level === "Error");
  const warnings = findings.filter((f) => f.level === "Warning");
  for (const f of errors) console.log(`  ✗ ${f.area}: ${f.message}`);
  for (const f of warnings) console.log(`  ! ${f.area}: ${f.message}`);

  const t = unity("tests", ["-runTests", "-testPlatform", "EditMode", "-testResults", xml]);
  const summary = existsSync(xml) ? readTestResults(readFileSync(xml, "utf8")) : null;
  if (summary) {
    console.log(`  EditMode tests: ${summary.passed}/${summary.total} passed`);
    for (const f of summary.failures) console.log(`  ✗ ${f.name}\n      ${f.message.split("\n")[0]}`);
  }

  const ok = c.code === 0 && t.code === 0 && summary !== null && summary.failed === 0;
  console.log(ok
    ? `✓ Ready for the Quest 3 (${warnings.length} warning(s)).`
    : `✗ Not ready: ${errors.length} error(s), ${summary ? `${summary.failed} failing test(s)` : "the tests did not run"}. See above and Logs/cli/.`);
  return ok ? 0 : 1;
}

/**
 * Runs the baseline scene in Meta XR Simulator (Quest 3 profile) and reports what the simulator provides on this
 * machine: eye buffer, field of view, refresh rate, passthrough, the passthrough camera's lens and pixels, and the
 * draw calls and triangles against the Quest 3 budget. The session only starts from the Editor window (not batch
 * mode) with the simulator app running, so this opens both.
 */
function sim(): number {
  const dir = simulatorDir();
  const env = dir ? simulatorEnv(dir) : null;
  if (!dir || !env || !existsSync(env.XR_RUNTIME_JSON)) {
    console.error("Meta XR Simulator is not installed. Mac: https://developers.meta.com/horizon/downloads/package/meta-xr-simulator-mac-arm/ "
      + "(it must end up in /Applications/MetaXRSimulator.app). Windows: https://developers.meta.com/horizon/downloads/package/meta-xr-simulator-windows/");
    return 1;
  }
  const roomArg = process.argv.indexOf("--room");
  if (!startSimulator(dir, roomArg > 0 ? process.argv[roomArg + 1] : "office")) return 1;
  const xml = join(LOGS, "sim-results.xml");
  const reportPath = join(LOGS, "sim", "sim-report.json");
  fresh(xml, reportPath, join(LOGS, "sim", "camera-frame.jpg"));

  const r = unity("sim", ["-runTests", "-testPlatform", "PlayMode", "-testFilter", "CutOnce.Sim.Tests", "-testResults", xml], { window: true, env, minutes: 15 });
  if (existsSync(reportPath)) {
    const s = JSON.parse(readFileSync(reportPath, "utf8"));
    console.log(`  headset profile: ${s.headset}, eye buffer ${s.eyeWidth}x${s.eyeHeight}, ${s.refreshHz} Hz`);
    console.log(`  field of view (left eye): ${s.fovLeftDeg.toFixed(0)}° out, ${s.fovRightDeg.toFixed(0)}° in, ${s.fovUpDeg.toFixed(0)}° up, ${s.fovDownDeg.toFixed(0)}° down`);
    console.log(`  passthrough: ${s.passthrough ? "running" : "NOT running"}`);
    const lens = s.cameraLensOffset;
    console.log(s.cameraFocalLength.x > 0
      ? `  camera: ${s.cameraResolution.x}x${s.cameraResolution.y}, focal ${s.cameraFocalLength.x.toFixed(1)} px, lens ${(lens.x * 100).toFixed(1)}/${(lens.y * 100).toFixed(1)}/${(lens.z * 100).toFixed(1)} cm from the eyes`
      : "  camera: did not start");
    console.log(`  camera pixels: ${s.cameraFrame ? s.cameraPhoto : "none (the simulator sends none on macOS while a room is connected)"}`);
    console.log(`  budget: ${s.drawCalls} draw calls, ${s.triangles.toLocaleString()} triangles`);
  }
  const summary = existsSync(xml) ? readTestResults(readFileSync(xml, "utf8")) : null;
  if (summary) for (const f of summary.failures) console.log(`  ✗ ${f.message.split("\n").slice(0, 6).join("\n    ")}`);
  const ok = r.code === 0 && summary !== null && summary.failed === 0 && summary.passed > 0;
  console.log(ok ? "✓ The simulator stands in for the Quest 3 on this machine." : "✗ The simulator run failed; see above and Logs/cli/sim.log.");
  return ok ? 0 : 1;
}

/**
 * Opens the simulator and a room, then the Editor on a scene in Play mode, and leaves them running for you to use:
 * pnpm quest:play [--scene Main|QuestBaseline] [--room office]. The headset view is the simulator's window.
 */
function play(): number {
  const dir = simulatorDir();
  const env = dir ? simulatorEnv(dir) : null;
  if (!dir || !env || !existsSync(env.XR_RUNTIME_JSON)) { console.error("Meta XR Simulator is not installed; see apps/quest/README.md."); return 1; }
  const roomArg = process.argv.indexOf("--room");
  if (!startSimulator(dir, roomArg > 0 ? process.argv[roomArg + 1] : "office")) return 1;
  const sceneArg = process.argv.indexOf("--scene");
  const scene = `Assets/CutOnce/Scenes/${sceneArg > 0 ? process.argv[sceneArg + 1] : "Main"}.unity`;
  if (!existsSync(join(PROJECT, scene))) { console.error(`No scene at apps/quest/${scene}.`); return 1; }
  const exe = unityPath(editorVersion(readFileSync(join(PROJECT, "ProjectSettings", "ProjectVersion.txt"), "utf8")));
  mkdirSync(LOGS, { recursive: true });
  const child = spawn(exe, ["-projectPath", PROJECT, "-buildTarget", "Android", "-logFile", join(LOGS, "play.log"),
    "-executeMethod", "CutOnce.QuestTools.Batch.Play", "-cutonceScene", scene], { detached: true, stdio: "ignore", env: { ...process.env, ...env } });
  child.unref();
  console.log(`✓ Unity is opening ${scene} in Play mode with Meta XR Simulator. The headset view is the simulator's window;`);
  console.log("  its keyboard and mouse controls are listed there. Stop Play or quit Unity when you are done.");
  return 0;
}

/** Optional: the simulator does not need it. The APK build needs the NDK (Unity Hub: Android SDK & NDK Tools). */
function build(): number {
  syncFixtures();
  fresh(APK);
  const args = ["-executeMethod", "CutOnce.QuestTools.Batch.Build", "-cutonceOutput", APK];
  if (process.argv.includes("--release")) args.push("-cutonceRelease");
  const r = unity("build", args, { minutes: 60 });
  if (r.code !== 0) return r.code;
  if (!existsSync(APK)) { console.error(`Unity finished but wrote no APK at ${APK}; see Logs/cli/build.log.`); return 1; }
  console.log(`✓ ${APK} (${(statSync(APK).size / 1048576).toFixed(1)} MB). Next: pnpm quest:install`);
  return 0;
}

/** Uses the real production shader and known geometry; this is not a live-headset capture. */
function surfaceProof(): number {
  const report = join(LOGS, "surface-proof", "report.json");
  fresh(report);
  const r = unity("surface-proof", ["-executeMethod", "CutOnce.Vision.Editor.SurfacePaintProof.Run"], { graphics: true, minutes: 15 });
  if (r.code !== 0) return r.code;
  if (!existsSync(report)) { console.error("Surface render proof wrote no report."); return 1; }
  console.log(`Surface render evidence: ${join(LOGS, "surface-proof")}`);
  return 0;
}

/** Explicit candidate generation; ordinary builds never download or replace the bundled model. */
function convertModel(): number {
  const download = spawnSync(process.execPath, [join(ROOT, "tools/quest/download-recognition-model.mjs")], { stdio: "inherit" });
  if (download.status !== 0) return download.status ?? 1;
  const report = join(LOGS, "model-conversion", "report.json");
  fresh(report);
  const run = unity("model-conversion", ["-executeMethod", "CutOnce.Vision.Editor.ExportRecognitionModel.Run", "-quit"], { minutes: 15 });
  if (run.code !== 0) return run.code;
  if (!existsSync(report) || JSON.parse(readFileSync(report, "utf8")).passed !== true) {
    console.error("Full-precision model conversion did not produce a passing report.");
    return 1;
  }
  console.log(`Candidate model exported: ${report}. Recognition and Quest performance are not verified by conversion.`);
  return 0;
}

/** The production model/preprocessor on recorded photos; deliberately not claimed as a headset test. */
function recognitionProof(): number {
  const download = spawnSync(process.execPath, [join(ROOT, "tools/quest/download-recognition-fixtures.mjs")], { stdio: "inherit" });
  if (download.status !== 0) return download.status ?? 1;
  const manifestPath = join(ROOT, "tools/quest/fixtures/recognition-coco.json");
  const manifest = JSON.parse(readFileSync(manifestPath, "utf8")) as {
    fixtures: { fileName: string; expectations: { className: string }[] }[];
  };
  const photos = manifest.fixtures.map((f) => join(LOGS, "recognition-fixtures", f.fileName)).join(";");
  const expected = manifest.fixtures.map((f) => f.expectations.map((e) => e.className).join(",")).join(";");
  const report = join(LOGS, "recognition-proof", "report.json");
  fresh(report);
  const run = unity("recognition-proof", ["-executeMethod", "CutOnce.Vision.Editor.RecognitionProof.Run",
    "-recognitionPhoto", photos, "-recognitionExpected", expected, "-recognitionGroundTruth", manifestPath],
  { graphics: true, minutes: 15 });
  if (run.code !== 0) return run.code;
  if (!existsSync(report) || JSON.parse(readFileSync(report, "utf8")).passed !== true) {
    console.error("Recorded-photo recognition proof did not produce a passing report.");
    return 1;
  }
  console.log(`Verified recorded-photo recognition: ${report} (not a live-headset test).`);
  return 0;
}

/** Installs without pre-granting permissions, so the headset asks for the camera and microphone as it will at the demo. */
function install(): number {
  if (!existsSync(APK)) { console.error("No APK yet. Run pnpm quest:build first."); return 1; }
  const adb = adbPath(unityPath(editorVersion(readFileSync(join(PROJECT, "ProjectSettings", "ProjectVersion.txt"), "utf8"))));
  if (!existsSync(adb)) { console.error(`adb not found at ${adb}. Install Unity's Android module (with Android SDK & NDK Tools), or set ADB.`); return 1; }
  const devices = spawnSync(adb, ["devices"], { encoding: "utf8" }).stdout ?? "";
  const lines = devices.split("\n").slice(1).filter((l) => l.trim());
  if (lines.some((l) => /\bunauthorized\b/.test(l))) { console.error("Put the Quest on and allow USB debugging, then run this again."); return 1; }
  if (!lines.some((l) => /\bdevice\b/.test(l))) { console.error("No Quest found. Plug it in with USB-C, and turn on developer mode in the Meta Horizon phone app."); return 1; }
  const r = spawnSync(adb, ["install", "-r", APK], { stdio: "inherit" });
  if (r.status !== 0) return r.status ?? 1;
  const launch = spawnSync(adb, ["shell", "monkey", "-p", PACKAGE, "-c", "android.intent.category.LAUNCHER", "1"], { encoding: "utf8" });
  if (launch.status !== 0) { console.error(`Installed, but the app did not start: ${(launch.stdout ?? "") + (launch.stderr ?? "")}`.trim()); return 1; }
  console.log(`✓ Installed and started. Budget lines: "${adb}" logcat -s Unity   (look for [Budget])`);
  return 0;
}

const commands: Record<string, () => number> = { setup, check, sim, play, build, install, "surface-proof": surfaceProof, "recognition-proof": recognitionProof, "convert-model": convertModel };
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const name = process.argv[2] ?? "";
  const cmd = commands[name];
  if (!cmd) { console.error(`usage: tsx tools/quest/unity.ts ${Object.keys(commands).join("|")}`); process.exit(2); }
  process.exit(cmd());
}
