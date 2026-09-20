// Read-only, allowlisted Quest USB diagnostics. Never print or persist raw adb output.
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export const PACKAGE = "com.cutonce.quest";
export const CAMERA_PERMISSION = "horizonos.permission.HEADSET_CAMERA";
const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const MODELS = new Map([
  ["quest", "Quest"], ["quest 2", "Quest 2"], ["quest 3", "Quest 3"],
  ["quest 3s", "Quest 3S"], ["quest pro", "Quest Pro"],
  ["meta quest 2", "Quest 2"], ["meta quest 3", "Quest 3"],
  ["meta quest 3s", "Quest 3S"], ["meta quest pro", "Quest Pro"],
]);

export function editorVersion(text) {
  const version = /^m_EditorVersion:\s*(\d+\.\d+\.\d+[abfp]\d+)\s*$/m.exec(text)?.[1];
  if (!version) throw new Error("Project editor version is missing or unsupported.");
  return version;
}

export function unityAdbPath(version) {
  if (!/^\d+\.\d+\.\d+[abfp]\d+$/.test(version)) throw new Error("Invalid project editor version.");
  // Deliberately ignore PATH, ADB, and UNITY_PATH overrides: use this project's exact Hub module.
  return `/Applications/Unity/Hub/Editor/${version}/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb`;
}

function questModel(value) {
  return MODELS.get(value.trim().replaceAll("_", " ").toLowerCase()) ?? null;
}

/** Serial numbers and USB locations are discarded during parsing, never returned. */
export function parseDevices(text) {
  const devices = [];
  for (const line of text.split(/\r?\n/)) {
    const fields = line.trim().split(/\s+/);
    if (fields.length < 2 || !["device", "offline", "unauthorized", "recovery", "bootloader"].includes(fields[1])) continue;
    const attribute = (name) => fields.slice(2).find((field) => field.startsWith(`${name}:`))?.slice(name.length + 1);
    const transport = attribute("transport_id");
    devices.push({
      state: fields[1], usb: attribute("usb") !== undefined,
      model: questModel(attribute("model") ?? ""),
      transportId: /^\d{1,8}$/.test(transport ?? "") ? transport : null,
    });
  }
  return devices;
}

export function parsePackage(text) {
  const installed = /^\s*Package \[com\.cutonce\.quest\]/m.test(text);
  const versionName = /^\s*versionName=(\d+(?:\.\d+){0,3}(?:[-+][A-Za-z0-9._-]{1,32})?)\s*$/m.exec(text)?.[1] ?? null;
  const versionCode = /^\s*versionCode=(\d+)\b/m.exec(text)?.[1];
  const grants = [...text.matchAll(/^\s*horizonos\.permission\.HEADSET_CAMERA:\s*granted=(true|false)\b/gm)].map((match) => match[1]);
  const permission = grants.length === 0 ? "unknown" : grants.every((value) => value === "true") ? "granted"
    : grants.every((value) => value === "false") ? "denied" : "mixed-across-profiles";
  return {
    installed,
    versionName: installed ? versionName : null,
    versionCode: installed && versionCode && Number.isSafeInteger(Number(versionCode)) ? Number(versionCode) : null,
    cameraPermission: installed ? permission : "not-installed",
    cameraPermissionScope: "Reported package permission entries; mixed user profiles are not treated as a grant.",
  };
}

function numericLine(text, key, minimum, maximum) {
  const match = new RegExp(`^\\s*${key}:\\s*(-?\\d+(?:\\.\\d+)?)\\s*$`, "m").exec(text);
  const value = match ? Number(match[1]) : NaN;
  return Number.isFinite(value) && value >= minimum && value <= maximum ? value : null;
}

export function parseBattery(text) {
  const level = numericLine(text, "level", 0, 1000);
  const scale = numericLine(text, "scale", 1, 1000);
  const temperature = numericLine(text, "temperature", -400, 1500);
  return {
    percent: level !== null && scale !== null && level <= scale ? Math.round(level * 100 / scale) : null,
    temperatureCelsius: temperature === null ? null : temperature / 10,
  };
}

export function parseThermal(text) {
  const code = numericLine(text, "Thermal Status", 0, 6);
  const levels = ["none", "light", "moderate", "severe", "critical", "emergency", "shutdown"];
  return code !== null && Number.isInteger(code) ? { code, level: levels[code] } : { code: null, level: "unknown" };
}

export function parseMemory(text) {
  const number = (pattern) => {
    const value = Number(pattern.exec(text)?.[1]);
    return Number.isSafeInteger(value) && value >= 0 && value <= 1_000_000_000 ? value : null;
  };
  return { totalPssKiB: number(/\bTOTAL PSS:\s*(\d+)/), totalRssKiB: number(/\bTOTAL RSS:\s*(\d+)/) };
}

export function execute(adb, args, { spawn = spawnSync, env = process.env } = {}) {
  const environment = { ...env };
  // Never inherit a remote/custom ADB server or implicit device selection from a runner account.
  for (const key of ["ADB_SERVER_SOCKET", "ANDROID_ADB_SERVER_PORT", "ADB_SERVER_PORT", "ANDROID_SERIAL", "ANDROID_ADB_SERVER_ADDRESS"])
    delete environment[key];
  // ADB recognizes its default empty host as loopback. Some bundled versions classify
  // literal 127.0.0.1 as remote and refuse to start a missing daemon. No -a: loopback only.
  return spawn(adb, ["-P", "5037", ...args], {
    encoding: "utf8", timeout: 10_000, maxBuffer: 2 * 1024 * 1024,
    env: environment, stdio: ["ignore", "pipe", "pipe"], shell: false,
  });
}

/** Only fixed categories escape this function; raw errors may contain account/device data. */
export function classifyAdbFailure(result) {
  switch (result?.error?.code) {
    case "ETIMEDOUT": return "command-timeout";
    case "EACCES": case "EPERM": return "executable-permission-denied";
    case "ENOENT": return "executable-not-found";
    case "ENOBUFS": return "command-output-limit";
  }
  const output = `${result?.stderr ?? ""}\n${result?.stdout ?? ""}`;
  if (/cannot start server on remote host/i.test(output)) return "remote-daemon-autostart-refused";
  if (/no permissions(?:\b|;)/i.test(output)) return "usb-permission-denied";
  if (/permission denied|operation not permitted/i.test(output)) return "command-permission-denied";
  if (/(?:failed to|cannot) start (?:the )?daemon/i.test(output)) return "local-daemon-start-failed";
  if (/(?:cannot|failed to) connect to (?:the )?(?:daemon|server)|connection refused/i.test(output)) return "local-daemon-unreachable";
  return "command-failed";
}

/** The injected runner makes every permitted ADB request testable without touching hardware. */
export function inspectDevice({ version, os = process.platform, exists = existsSync, run = execute }) {
  const report = {
    schemaVersion: 1,
    scope: "Read-only authorized USB Quest diagnostics. No install, app launch, permission change, camera capture, logcat, account data, or network discovery. Does not verify recognition, motion alignment, or Quest frame rate.",
    unityVersion: version,
    adbSource: "The pinned Unity Hub editor's bundled Android SDK",
    status: "unavailable", message: "", connection: null, device: null, app: null, health: null,
    issues: [], failures: [],
  };
  const stop = (status, message) => Object.assign(report, { status, message });
  if (os !== "darwin") return stop("unsupported-host", "This probe is intended for the registered Mac runner.");
  const adb = unityAdbPath(version);
  if (!exists(adb)) return stop("adb-missing", "Install Android Build Support for the project's pinned Unity version.");
  const query = (args, name, allowMissing = false) => {
    let result;
    try { result = run(adb, args); }
    catch (error) { result = { status: null, error }; }
    if (!result.error && (result.status === 0 || (allowMissing && result.status === 1))) return result;
    report.failures.push({ check: name, code: classifyAdbFailure(result) });
    report.issues.push(`${name} could not be read; raw command output was not retained.`);
    return null;
  };
  const listing = query(["devices", "-l"], "USB device list");
  if (!listing) return stop("adb-unavailable", "The bundled ADB could not inspect local USB connections.");
  const devices = parseDevices(listing.stdout ?? "");
  const quests = devices.filter((device) => device.usb && device.state === "device" && device.model);
  report.connection = {
    authorizedUsbQuests: quests.length,
    unauthorizedUsbDevices: devices.filter((device) => device.usb && device.state === "unauthorized").length,
    offlineUsbDevices: devices.filter((device) => device.usb && device.state === "offline").length,
    ignoredOtherConnections: devices.filter((device) => !device.usb || !device.model).length,
  };
  if (quests.length === 0) return stop(report.connection.unauthorizedUsbDevices > 0 ? "usb-authorization-needed" : "no-authorized-usb-quest",
    report.connection.unauthorizedUsbDevices > 0 ? "A USB device needs debugging authorization. If this is the Quest, accept the prompt inside the headset."
      : "No authorized USB Quest is available. Connect the Quest to this Mac and allow USB debugging in the headset.");
  if (quests.length > 1) return stop("multiple-usb-quests", "Multiple authorized USB Quests are connected; no device was selected or queried.");
  const selected = quests[0];
  if (!selected.transportId) return stop("transport-unavailable", "ADB did not provide a transport ID; refusing an ambiguous device selection.");
  const read = (args, name, allowMissing = false) => query(["-t", selected.transportId, ...args], name, allowMissing);
  const state = read(["get-state"], "Device state");
  if (!state || state.stdout?.trim() !== "device") return stop("device-unavailable", "The selected USB Quest is no longer ready.");
  const model = read(["shell", "getprop", "ro.product.model"], "Quest model");
  const confirmedModel = model && questModel(model.stdout ?? "");
  if (!confirmedModel) return stop("device-unconfirmed", "The USB device could not be confirmed as a Quest; no app or health data was queried.");
  const boot = read(["shell", "getprop", "sys.boot_completed"], "Boot state");
  const api = read(["shell", "getprop", "ro.build.version.sdk"], "Android API level");
  const apiText = api?.stdout?.trim() ?? "";
  report.device = { model: confirmedModel, authorized: true, transport: "USB",
    bootComplete: boot?.stdout?.trim() === "1" ? true : boot?.stdout?.trim() === "0" ? false : null,
    androidApiLevel: /^\d{2}$/.test(apiText) ? Number(apiText) : null };
  const packageState = read(["shell", "dumpsys", "package", PACKAGE], "Installed app and camera permission");
  report.app = packageState ? { packageName: PACKAGE, ...parsePackage(packageState.stdout ?? ""), running: null } : null;
  if (report.app?.installed) {
    const running = read(["shell", "pidof", PACKAGE], "App running state", true);
    report.app.running = running ? running.status === 0 && /^\s*\d+(?:\s+\d+)*\s*$/.test(running.stdout ?? "") : null;
  }
  const battery = read(["shell", "dumpsys", "battery"], "Battery health");
  const thermal = read(["shell", "dumpsys", "thermalservice"], "Thermal state");
  const memory = report.app?.running ? read(["shell", "dumpsys", "meminfo", PACKAGE], "App memory use") : null;
  report.health = {
    battery: battery ? parseBattery(battery.stdout ?? "") : null,
    thermal: thermal ? parseThermal(thermal.stdout ?? "") : null,
    appMemory: memory ? parseMemory(memory.stdout ?? "") : null,
    frameRateVerified: false,
    scope: "A single battery/thermal/memory snapshot, not a VR performance or live camera test.",
  };
  return stop("authorized-usb-quest", "An authorized USB Quest was inspected using read-only commands. No app or device state was changed.");
}

export function main(root = ROOT) {
  let report;
  try {
    const version = editorVersion(readFileSync(join(root, "apps/quest/ProjectSettings/ProjectVersion.txt"), "utf8"));
    report = inspectDevice({ version });
  } catch {
    report = { schemaVersion: 1, status: "probe-error", message: "Device diagnostics could not complete; private command output was not retained." };
  }
  const directory = join(root, "apps/quest/Logs/cli/device-status");
  mkdirSync(directory, { recursive: true });
  writeFileSync(join(directory, "report.json"), JSON.stringify(report, null, 2));
  console.log(`Quest device status: ${report.status}. ${report.message}`);
  // No device is an expected diagnostic result, not evidence of a successful headset test.
  return ["probe-error", "adb-unavailable", "unsupported-host", "adb-missing"].includes(report.status) ? 1 : 0;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exitCode = main();
