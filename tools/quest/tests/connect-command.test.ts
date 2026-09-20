import { existsSync, readFileSync } from "node:fs";
import { afterEach, expect, it, vi } from "vitest";
import { main } from "../connect.js";

const { adb } = vi.hoisted(() => ({ adb: vi.fn() }));
vi.mock("node:child_process", async importOriginal => ({
  ...await importOriginal<typeof import("node:child_process")>(), spawnSync: adb,
}));
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); vi.restoreAllMocks(); adb.mockReset(); });

it("connect pushes the expected private config, restarts the app in order, and removes the temporary file", async () => {
  vi.stubEnv("API_TOKEN", "test-private-token");
  vi.stubEnv("ANDROID_SERIAL", "quest-test");
  vi.stubEnv("QUEST_DEVICE_ID", "quest-1");
  vi.stubEnv("ADB", "test-adb");
  const output = vi.spyOn(console, "log").mockImplementation(() => {});
  vi.stubGlobal("fetch", vi.fn()
    .mockResolvedValueOnce(Response.json({ ok: true, copilot: "live" }))
    .mockResolvedValueOnce(Response.json({ assembly_id: "a" })));
  let pushed = "";
  adb.mockImplementation((binary: string, args: string[]) => {
    expect(binary).toBe("test-adb");
    if (args.includes("push")) {
      pushed = args[3];
      expect(JSON.parse(readFileSync(pushed, "utf8"))).toEqual({
        server_url: "https://example.com", api_token: "test-private-token", device_id: "quest-1",
      });
      expect(args[4]).toBe("/sdcard/Android/data/com.cutonce.quest/files/cutonce.config.json");
    }
    return { status: 0, stdout: args[0] === "devices" ? "quest-test\tdevice\n" : args.includes("pm") ? "package:/data/app/base.apk" : "", stderr: "" };
  });
  await main("connect", ["https://example.com"]);
  expect(adb.mock.calls.map(call => call[1].join(" "))).toEqual([
    "devices",
    "-s quest-test shell pm path com.cutonce.quest",
    "-s quest-test shell mkdir -p /sdcard/Android/data/com.cutonce.quest/files",
    expect.stringContaining("-s quest-test push"),
    "-s quest-test shell am force-stop com.cutonce.quest",
    "-s quest-test shell monkey -p com.cutonce.quest -c android.intent.category.LAUNCHER 1",
  ]);
  expect(pushed).not.toBe("");
  expect(existsSync(pushed)).toBe(false);
  expect(JSON.stringify(output.mock.calls)).not.toContain("test-private-token");
});

it("does not touch the headset when the server rejects its token", async () => {
  vi.stubEnv("API_TOKEN", "test-private-token");
  vi.stubGlobal("fetch", vi.fn()
    .mockResolvedValueOnce(Response.json({ ok: true, copilot: "live" }))
    .mockResolvedValueOnce(new Response(null, { status: 401 })));
  await expect(main("connect", ["https://example.com"])).rejects.toThrow(/does not match/);
  expect(adb).not.toHaveBeenCalled();
});

it("removes the temporary token file even if USB transfer fails, without restarting the app", async () => {
  vi.stubEnv("API_TOKEN", "test-private-token");
  vi.stubEnv("ANDROID_SERIAL", "quest-test");
  vi.stubGlobal("fetch", vi.fn()
    .mockResolvedValueOnce(Response.json({ ok: true, copilot: "live" }))
    .mockResolvedValueOnce(Response.json({ assembly_id: "a" })));
  let pushed = "";
  adb.mockImplementation((_binary: string, args: string[]) => {
    if (args.includes("push")) {
      pushed = args[3];
      return { status: 1, stdout: "", stderr: "device disconnected" };
    }
    return { status: 0, stdout: args[0] === "devices" ? "quest-test\tdevice\n" : "package:/data/app/base.apk", stderr: "" };
  });
  await expect(main("connect", ["https://example.com"])).rejects.toThrow(/disconnected/);
  expect(pushed).not.toBe("");
  expect(existsSync(pushed)).toBe(false);
  expect(adb.mock.calls.some(call => call[1].includes("force-stop"))).toBe(false);
});
