import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, expect, it } from "vitest";
import { auth, makeApp } from "./helpers.js";

let t: Awaited<ReturnType<typeof makeApp>>;
let src: string;
beforeEach(async () => {
  t = await makeApp();
  src = join(mkdtempSync(join(tmpdir(), "asset-src-")), "model.glb");
  writeFileSync(src, "glTF version 1");
  t.app.ctx.store.syncAsset("plan_blank", "model.glb", src);
});
afterEach(async () => { await t.cleanup(); });

it("serves a model kept next to its plan, byte for byte", async () => {
  const r = await t.app.inject({ method: "GET", url: "/v1/plans/plan_blank/assets/model.glb", headers: auth });
  expect(r.statusCode).toBe(200);
  expect(r.rawPayload.equals(readFileSync(src))).toBe(true);
});

it("refreshes an asset only when its source file changes", () => {
  const { store } = t.app.ctx;
  expect(store.syncAsset("plan_blank", "model.glb", src)).toBe(false);   // identical: nothing to do
  writeFileSync(src, "glTF version 2");
  expect(store.syncAsset("plan_blank", "model.glb", src)).toBe(true);
  expect(readFileSync(store.assetPath("plan_blank", "model.glb")!, "utf8")).toBe("glTF version 2");
});

it("rejects odd names, reports missing files and needs the token", async () => {
  expect((await t.app.inject({ method: "GET", url: "/v1/plans/plan_blank/assets/..%2F..%2Fsecret.glb", headers: auth })).statusCode).toBe(400);
  expect((await t.app.inject({ method: "GET", url: "/v1/plans/plan_blank/assets/nope.glb", headers: auth })).statusCode).toBe(404);
  expect((await t.app.inject({ method: "GET", url: "/v1/plans/plan_blank/assets/model.glb" })).statusCode).toBe(401);
});
