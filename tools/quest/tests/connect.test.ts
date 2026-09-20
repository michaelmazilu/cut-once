import { parseEnv } from "node:util";
import { describe, expect, it, vi } from "vitest";
import { checkServer, headsetConfig, prepareEnv, selectDevice, serverUrl, tunnelAddress, voiceProblems } from "../connect.js";

describe("portable voice configuration", () => {
  it("creates a private token once, enables live voice, and preserves keys and model choices", () => {
    const input = '# Keep this comment\r\nAPI_TOKEN=\r\nCOPILOT_MODE=off\r\nKIT_AI=omni\r\nOPENAI_API_KEY="test-key"\r\nOPENAI_MODEL=chosen-model\r\n';
    const result = prepareEnv(input, true);
    expect(parseEnv(result)).toMatchObject({ COPILOT_MODE: "live", KIT_AI: "openai", OPENAI_API_KEY: "test-key", OPENAI_MODEL: "chosen-model" });
    expect(parseEnv(result).API_TOKEN).toMatch(/^[0-9a-f]{48}$/);
    expect(result).toContain("# Keep this comment");
    expect(prepareEnv(result, true)).toBe(result);
  });

  it("preserves an existing token and configured Omni provider", () => {
    const result = parseEnv(prepareEnv("API_TOKEN=existing-token\nOMNI_API_KEY=configured\nKIT_AI=omni\n", true));
    expect(result).toMatchObject({ API_TOKEN: "existing-token", KIT_AI: "omni", COPILOT_MODE: "live" });
    expect(parseEnv(prepareEnv("API_TOKEN=dev-token\n")).API_TOKEN).not.toBe("dev-token");
  });

  it("does not switch voice mode when merely starting the server", () => {
    expect(parseEnv(prepareEnv("API_TOKEN=existing\nCOPILOT_MODE=fake\n"))).toMatchObject({ API_TOKEN: "existing", COPILOT_MODE: "fake" });
  });

  it("reports missing credentials and rejects fake mode as a live voice setup", () => {
    expect(voiceProblems({ COPILOT_MODE: "fake", API_TOKEN: "dev-token" }).join("\n"))
      .toMatch(/fake ignores/);
    expect(voiceProblems({ COPILOT_MODE: "live", API_TOKEN: "a", OPENAI_API_KEY: "b", ELEVENLABS_API_KEY: "c", ELEVENLABS_VOICE_ID: "d" })).toEqual([]);
  });
});

describe("server and headset connection", () => {
  it("normalizes the origin and writes the headset's expected config fields", () => {
    expect(headsetConfig("https://example.com/", "private", "my-quest")).toEqual({ server_url: "https://example.com", api_token: "private", device_id: "my-quest" });
    expect(serverUrl("http://192.168.1.2:8080")).toBe("http://192.168.1.2:8080");
    expect(() => headsetConfig("https://example.com", "dev-token")).toThrow(/non-default/);
  });

  it.each(["file:///tmp/test", "https://user:pass@example.com", "https://example.com/director", "https://example.com?token=x", "https://example.com#test"])("rejects invalid server origin %s", raw => {
    expect(() => serverUrl(raw)).toThrow();
  });

  it("verifies health and the bearer token before configuring the headset, refusing redirects", async () => {
    const request = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json({ ok: true, copilot: "live", openai: "set", tts: "set" }))
      .mockResolvedValueOnce(Response.json({ assembly_id: "blank" }));
    expect(await checkServer("https://example.com", "private", request)).toMatchObject({ copilot: "live" });
    expect(request.mock.calls[1]).toEqual(["https://example.com/v1/assemblies/current", expect.objectContaining({ headers: { authorization: "Bearer private" }, redirect: "error" })]);
    expect(request.mock.calls[0][1]?.redirect).toBe("error");
  });

  it("does not send the token when health is not a Cut Once response", async () => {
    const request = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ ok: true }));
    await expect(checkServer("https://example.com", "private", request)).rejects.toThrow(/not answering/);
    expect(request).toHaveBeenCalledTimes(1);
  });

  it("explains a token mismatch", async () => {
    const request = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(Response.json({ ok: true, copilot: "live" }))
      .mockResolvedValueOnce(new Response(null, { status: 401 }));
    await expect(checkServer("https://example.com", "private", request)).rejects.toThrow(/Restart the server/);
  });

  it("requires an authorized, unambiguous USB device", () => {
    const output = "List of devices attached\r\nquest123\tdevice\r\nphone456\tunauthorized\r\n";
    expect(selectDevice(output)).toBe("quest123");
    expect(() => selectDevice("quest123\tunauthorized")).toThrow(/allow USB debugging/);
    expect(() => selectDevice("quest123\tdevice\nphone456\tdevice")).toThrow(/Multiple/);
    expect(selectDevice("quest123\tdevice\nphone456\tdevice", "quest123")).toBe("quest123");
    expect(() => selectDevice(output, "missing")).toThrow(/not connected/);
  });

  it("extracts the public tunnel address, ignoring Cloudflare's API endpoint", () => {
    expect(tunnelAddress("https://api.trycloudflare.com\n| https://four-random-words-here.trycloudflare.com |\n"))
      .toBe("https://four-random-words-here.trycloudflare.com");
    expect(tunnelAddress("https://api.trycloudflare.com")).toBeUndefined();
  });
});
