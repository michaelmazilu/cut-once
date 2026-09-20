import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { PROJECT, adbPath, editorVersion, explain, invalidAssetMetadata, readFindings, readTestResults, simulatorDir, simulatorEnv, unityExitCode, unityPath } from "../unity.js";

describe("finding Unity and adb", () => {
  it("reads the pinned editor version from the project", () => {
    expect(editorVersion(readFileSync(join(PROJECT, "ProjectSettings", "ProjectVersion.txt"), "utf8"))).toBe("6000.6.2f1");
  });

  it("uses Unity Hub's default folders on macOS and Windows, and UNITY_PATH when set", () => {
    expect(unityPath("6000.6.2f1", "darwin", {})).toBe("/Applications/Unity/Hub/Editor/6000.6.2f1/Unity.app/Contents/MacOS/Unity");
    expect(unityPath("6000.6.2f1", "win32", {})).toBe("C:\\Program Files\\Unity\\Hub\\Editor\\6000.6.2f1\\Editor\\Unity.exe");
    expect(unityPath("6000.6.2f1", "darwin", { UNITY_PATH: "/opt/unity" })).toBe("/opt/unity");
  });

  it("finds the adb inside Unity's Android module", () => {
    expect(adbPath(unityPath("6000.6.2f1", "darwin", {}), "darwin", {}))
      .toBe("/Applications/Unity/Hub/Editor/6000.6.2f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb");
    expect(adbPath(unityPath("6000.6.2f1", "win32", {}), "win32", {}))
      .toBe("C:\\Program Files\\Unity\\Hub\\Editor\\6000.6.2f1\\Editor\\Data\\PlaybackEngines\\AndroidPlayer\\SDK\\platform-tools\\adb.exe");
  });
});

describe("finding Meta XR Simulator", () => {
  it("uses the Mac app's runtime folder, and Windows' xrsim:// handler from the registry", () => {
    expect(simulatorDir("darwin", {})).toBe("/Applications/MetaXRSimulator.app/Contents/Resources/MetaXRSimulator");
    const reg = '\r\nHKEY_CURRENT_USER\\SOFTWARE\\Classes\\xrsim\\shell\\open\\command\r\n    (Default)    REG_SZ    "C:\\Program Files\\Meta XR Simulator\\MetaXRSimulator.exe" "%1"\r\n';
    expect(simulatorDir("win32", {}, () => reg)).toBe("C:\\Program Files\\Meta XR Simulator");
    expect(simulatorDir("win32", {}, () => "")).toBeNull();
    expect(simulatorDir("darwin", { XRSIM_DIR: "/x" })).toBe("/x");
  });

  it("sets the same variables as Meta's Activate menu", () => {
    expect(simulatorEnv("/sim", "darwin")).toEqual({
      XR_RUNTIME_JSON: "/sim/meta_openxr_simulator.json",
      XR_SELECTED_RUNTIME_JSON: "/sim/meta_openxr_simulator.json",
      META_XRSIM_CONFIG_JSON: "/sim/config/sim_core_configuration.json",
    });
  });
});

describe("reading Unity's output", () => {
  it("says what to do for the failures we have actually hit", () => {
    const log = [
      "Library/PackageCache/com.meta.xr.sdk.core/Editor/CaptureTool.cs(43,28): error CS0103: The name 'AndroidExternalToolsSettings' does not exist in the current context",
      "It looks like another Unity instance is running with this project open.",
    ].join("\n");
    const lines = explain(log);
    expect(lines[0]).toMatch(/Editor has apps\/quest open/);
    expect(lines[1]).toMatch(/Android module is missing or half installed/);
    expect(lines[2]).toBe("1 compile error(s):");
  });

  it("points at the missing Android piece, and stays quiet about a healthy log", () => {
    expect(explain("Android NDK not found. Set the NDK path in Preferences.")[0]).toMatch(/Android SDK & NDK Tools/);
    expect(explain("Unable to find JDK: JDK not found")[0]).toMatch(/OpenJDK/);
    expect(explain("[CutOnce] check done: 0 error(s), 1 warning(s)")).toEqual([]);
  });

  it("finds and deduplicates ignored asset metadata even when Unity repeats it with worker prefixes", () => {
    const ignoredTest = "The .meta file Assets/CutOnce/Vision/Tests/YoloLabelTests.cs.meta does not have a valid GUID and its corresponding Asset file will be ignored.";
    const ignoredShader = "The .meta file 'Assets/Surface Paint.shader.meta' does not have a valid GUID and its corresponding Asset file will be ignored.";
    const log = `[Worker0] ${ignoredTest}\r\n${ignoredShader}\n[Worker1] ${ignoredTest}\n[CutOnce] check done: 0 error(s)`;
    expect(invalidAssetMetadata(log)).toEqual([ignoredTest, ignoredShader]);
    expect(explain(log)[0]).toMatch(/ignored 2 asset\(s\).*omitted scripts and tests.*false pass/);
    expect(explain(log).join("\n")).toContain("YoloLabelTests.cs.meta");
  });

  it("fails a successful Unity process if invalid metadata silently removed a test", () => {
    const log = "The .meta file Assets/CutOnce/Vision/Tests/YoloLabelTests.cs.meta does not have a valid GUID and its corresponding Asset file will be ignored.\nTest run completed: 201 passed, 0 failed.";
    expect(unityExitCode(0, log)).toBe(1);
    expect(unityExitCode(2, log)).toBe(2);
    expect(unityExitCode(null, log)).toBe(1);
  });

  it("preserves exit codes for ordinary logs and does not mistake normal GUID messages for corruption", () => {
    const log = "Imported Assets/Test.cs with a valid GUID.\n[CutOnce] check done: 0 error(s), 1 warning(s)";
    expect(invalidAssetMetadata(log)).toEqual([]);
    expect(unityExitCode(0, log)).toBe(0);
    expect(unityExitCode(7, log)).toBe(7);
    expect(unityExitCode(null, log)).toBe(1);
  });

  it("reads the check's findings, and none from a missing or half-written file", () => {
    const dir = mkdtempSync(join(tmpdir(), "quest-cli-"));
    const good = join(dir, "good.json"), cut = join(dir, "cut.json");
    writeFileSync(good, JSON.stringify({ findings: [{ level: "Error", area: "xr", message: "OpenXR must be the XR loader" }] }));
    writeFileSync(cut, '{ "findings": [ { "level": "Err');
    expect(readFindings(good)).toHaveLength(1);
    expect(readFindings(cut)).toEqual([]);
    expect(readFindings(join(dir, "missing.json"))).toEqual([]);
  });

  it("summarises an NUnit result file, with the failing test's message", () => {
    const xml = `<?xml version="1.0"?>
<test-run id="2" testcasecount="3" result="Failed(Child)" total="3" passed="2" failed="1" inconclusive="0" skipped="0">
  <test-suite><test-case id="1" name="A" fullname="CutOnce.A" result="Passed"></test-case>
  <test-case id="2" name="B" fullname="CutOnce.QuestTools.Tests.QuestReadinessTests.ProjectSettingsAreReadyForTheQuest" result="Failed">
    <failure><message><![CDATA[Run Cut Once > Apply Quest 3 settings.
[Error] xr: OpenXR must be the XR loader]]></message></failure>
  </test-case></test-suite>
</test-run>`;
    const s = readTestResults(xml);
    expect(s).toMatchObject({ total: 3, passed: 2, failed: 1 });
    expect(s.failures[0].name).toMatch(/ProjectSettingsAreReadyForTheQuest$/);
    expect(s.failures[0].message).toMatch(/^Run Cut Once/);
  });
});
