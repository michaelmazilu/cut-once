import assert from "node:assert/strict";
import { test } from "node:test";
import { CAMERA_PERMISSION, PACKAGE, editorVersion, inspectDevice, parseBattery, parseDevices, parseMemory, parsePackage, parseThermal, unityAdbPath } from "../device-status.mjs";

const version = "6000.6.2f1";
const serial = "DO_NOT_UPLOAD_USB_SERIAL";
const listing = `List of devices attached\n${serial} device usb:DO_NOT_UPLOAD_USB_LOCATION product:eureka model:Quest_3 device:eureka transport_id:7\n`;
const packageDump = `Packages:\n  Package [com.cutonce.quest] (private-token):\n    userId=10123\n    codePath=/private/account/path\n    versionCode=8 minSdk=32 targetSdk=32\n    versionName=0.1.0\n    requested permissions:\n      ${CAMERA_PERMISSION}\n    User 0: installed=true\n      runtime permissions:\n        ${CAMERA_PERMISSION}: granted=true, flags=[ USER_SET ]\n`;

function mocked(responses = {}) {
  const calls = [];
  const standard = {
    "devices -l": listing,
    "get-state": "device\n",
    "shell getprop ro.product.model": "Quest 3\n",
    "shell getprop sys.boot_completed": "1\n",
    "shell getprop ro.build.version.sdk": "32\n",
    [`shell dumpsys package ${PACKAGE}`]: packageDump,
    [`shell pidof ${PACKAGE}`]: "12345\n",
    "shell dumpsys battery": "Current Battery Service state:\n  level: 83\n  scale: 100\n  temperature: 317\n  technology: PRIVATE_BATTERY_IDENTIFIER\n",
    "shell dumpsys thermalservice": "Thermal Status: 1\nprivate registered listener/account\n",
    [`shell dumpsys meminfo ${PACKAGE}`]: " TOTAL PSS: 234567 TOTAL RSS: 456789\nprivate process paths\n",
    ...responses,
  };
  const run = (path, args) => {
    calls.push({ path, args });
    const key = (args[0] === "-t" ? args.slice(2) : args).join(" ");
    if (!(key in standard)) throw new Error(`Unexpected test command ${key}`);
    const value = standard[key];
    return typeof value === "string" ? { status: 0, stdout: value } : value;
  };
  return { calls, run };
}

function probe(mock, extra = {}) {
  return inspectDevice({ version, os: "darwin", exists: () => true, run: mock.run, ...extra });
}

test("uses the pinned Hub ADB without environment or PATH substitutions", () => {
  assert.equal(editorVersion(`m_EditorVersion: ${version}\r\nm_EditorVersionWithRevision: ignored`), version);
  assert.equal(unityAdbPath(version), "/Applications/Unity/Hub/Editor/6000.6.2f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb");
  assert.throws(() => unityAdbPath("../../private"));
  assert.throws(() => editorVersion("m_EditorVersion: /private/version"));
});

test("drops serial, USB path, network address and arbitrary model names while parsing", () => {
  const devices = parseDevices(listing + "192.168.1.200:5555 device product:eureka model:Quest_3 transport_id:8\nPHONE device usb:private model:PRIVATE_PHONE_NAME transport_id:9\n");
  assert.equal(devices.length, 3);
  const output = JSON.stringify(devices);
  for (const secret of [serial, "DO_NOT_UPLOAD_USB_LOCATION", "192.168.1.200", "PRIVATE_PHONE_NAME"]) assert.ok(!output.includes(secret));
  assert.deepEqual(devices[0], { state: "device", usb: true, model: "Quest 3", transportId: "7" });
});

test("no connected Quest stops after enumeration and reports a physical next step", () => {
  const mock = mocked({ "devices -l": "List of devices attached\n" });
  const report = probe(mock);
  assert.equal(report.status, "no-authorized-usb-quest");
  assert.equal(mock.calls.length, 1);
  assert.match(report.message, /Connect the Quest/);
});

test("unauthorized USB never executes a shell command", () => {
  const mock = mocked({ "devices -l": "PRIVATE unauthorized usb:private transport_id:1\n" });
  assert.equal(probe(mock).status, "usb-authorization-needed");
  assert.equal(mock.calls.length, 1);
});

test("offline devices are reported without probing them", () => {
  const mock = mocked({ "devices -l": "PRIVATE offline usb:private model:Quest_3 transport_id:1\n" });
  const report = probe(mock);
  assert.equal(report.connection.offlineUsbDevices, 1);
  assert.equal(report.status, "no-authorized-usb-quest");
  assert.equal(mock.calls.length, 1);
});

test("wireless Quests, emulators and non-Quest USB devices are ignored", () => {
  const mock = mocked({ "devices -l": "192.168.1.4:5555 device model:Quest_3 transport_id:1\nemulator-5554 device model:Quest_3 transport_id:2\nPHONE device usb:private model:Pixel_8 transport_id:3\n" });
  const report = probe(mock);
  assert.equal(report.status, "no-authorized-usb-quest");
  assert.equal(mock.calls.length, 1);
});

test("multiple USB Quests or a missing transport ID are never guessed", () => {
  const multiple = mocked({ "devices -l": listing + listing.replace("transport_id:7", "transport_id:8") });
  assert.equal(probe(multiple).status, "multiple-usb-quests");
  assert.equal(multiple.calls.length, 1);
  const missing = mocked({ "devices -l": listing.replace(" transport_id:7", "") });
  assert.equal(probe(missing).status, "transport-unavailable");
  assert.equal(missing.calls.length, 1);
});

test("inspects only the selected USB transport and emits allowlisted fields", () => {
  const mock = mocked();
  const report = probe(mock);
  assert.equal(report.status, "authorized-usb-quest");
  assert.deepEqual(report.device, { model: "Quest 3", authorized: true, transport: "USB", bootComplete: true, androidApiLevel: 32 });
  assert.equal(report.app.versionName, "0.1.0");
  assert.equal(report.app.versionCode, 8);
  assert.equal(report.app.cameraPermission, "granted");
  assert.equal(report.app.running, true);
  assert.deepEqual(report.health.battery, { percent: 83, temperatureCelsius: 31.7 });
  assert.deepEqual(report.health.thermal, { code: 1, level: "light" });
  assert.deepEqual(report.health.appMemory, { totalPssKiB: 234567, totalRssKiB: 456789 });
  assert.equal(report.health.frameRateVerified, false);
  for (const call of mock.calls.slice(1)) assert.deepEqual(call.args.slice(0, 2), ["-t", "7"]);
  for (const call of mock.calls) assert.equal(call.path, unityAdbPath(version));
  const output = JSON.stringify(report);
  for (const secret of [serial, "DO_NOT_UPLOAD_USB_LOCATION", "private-token", "10123", "/private/account/path", "12345", "PRIVATE_BATTERY_IDENTIFIER", "private registered listener/account", "private process paths"])
    assert.ok(!output.includes(secret), `Report leaked ${secret}`);
  assert.ok(!/logcat|screencap|screenrecord|install|uninstall|\bgrant\b|\brevoke\b|\bstart\b|\bkill\b/.test(mock.calls.map((call) => call.args.join(" ")).join("\n")));
});

test("an unconfirmed model or disconnected device is not queried for app data", () => {
  const unknown = mocked({ "shell getprop ro.product.model": "PRIVATE_OTHER_DEVICE" });
  assert.equal(probe(unknown).status, "device-unconfirmed");
  assert.equal(unknown.calls.length, 3);
  const gone = mocked({ "get-state": { status: 1, stderr: serial } });
  assert.equal(probe(gone).status, "device-unavailable");
  assert.equal(gone.calls.length, 2);
});

test("missing app or a stopped app does not request process memory or launch anything", () => {
  const missing = mocked({ [`shell dumpsys package ${PACKAGE}`]: `Unable to find package: ${PACKAGE}` });
  const missingReport = probe(missing);
  assert.equal(missingReport.app.installed, false);
  assert.equal(missingReport.app.cameraPermission, "not-installed");
  assert.ok(!missing.calls.some((call) => call.args.includes("pidof") || call.args.includes("meminfo")));
  const stopped = mocked({ [`shell pidof ${PACKAGE}`]: { status: 1, stdout: "" } });
  assert.equal(probe(stopped).app.running, false);
  assert.ok(!stopped.calls.some((call) => call.args.includes("meminfo")));
});

test("mixed profile permission grants remain explicitly ambiguous", () => {
  const state = parsePackage(packageDump + `    User 10: private-account\n      ${CAMERA_PERMISSION}: granted=false\n`);
  assert.equal(state.cameraPermission, "mixed-across-profiles");
  assert.equal(parsePackage(packageDump.replace("granted=true", "granted=false")).cameraPermission, "denied");
  assert.equal(parsePackage(packageDump.replace(`${CAMERA_PERMISSION}: granted=true`, "other.permission: granted=true")).cameraPermission, "unknown");
});

test("malformed numeric/permission values and private version strings are not echoed", () => {
  assert.equal(parsePackage(packageDump.replace("versionName=0.1.0", "versionName=someone@example.com")).versionName, null);
  assert.deepEqual(parseBattery("level: 900\nscale: 100\ntemperature: private-account"), { percent: null, temperatureCelsius: null });
  assert.deepEqual(parseThermal("Thermal Status: PRIVATE_DEVICE"), { code: null, level: "unknown" });
  assert.deepEqual(parseMemory("private account and host"), { totalPssKiB: null, totalRssKiB: null });
});

test("ADB failures retain safe summaries rather than private stdout/stderr", () => {
  const mock = mocked({ "devices -l": { status: 1, stdout: serial, stderr: "private-user@10.0.0.7" } });
  const report = probe(mock);
  assert.equal(report.status, "adb-unavailable");
  assert.ok(!JSON.stringify(report).includes(serial));
  assert.ok(!JSON.stringify(report).includes("10.0.0.7"));
  assert.equal(report.issues.length, 1);
});

test("host and missing Unity Android module checks run before ADB", () => {
  const mock = mocked();
  assert.equal(probe(mock, { os: "linux" }).status, "unsupported-host");
  assert.equal(probe(mock, { exists: () => false }).status, "adb-missing");
  assert.equal(mock.calls.length, 0);
});
