import { defineConfig } from "@playwright/test";
import { ROOT, SIM_PORT } from "./paths.js";

/** One-off: photographs the Kit walkthrough (walkthrough.spec.ts) instead of the fixed scenes. */
export default defineConfig({
  testDir: ".",
  testMatch: "walkthrough.spec.ts",
  workers: 1,
  retries: 0,
  timeout: 90_000,
  reporter: [["list"]],
  outputDir: "../../sim-out/playwright-walkthrough",
  use: {
    baseURL: `http://127.0.0.1:${SIM_PORT}`,
    viewport: { width: 1280, height: 720 },
    deviceScaleFactor: 1,
    launchOptions: { args: ["--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist", "--autoplay-policy=no-user-gesture-required"] },
  },
  webServer: {
    command: "pnpm -F @cutonce/api exec tsx src/cli/sim-server.ts",
    cwd: ROOT,
    url: `http://127.0.0.1:${SIM_PORT}/health`,
    timeout: 60_000,
    reuseExistingServer: true,
    stdout: "pipe",
  },
});
