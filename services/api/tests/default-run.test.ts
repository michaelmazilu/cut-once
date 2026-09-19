import { expect, it } from "vitest";
import { auth, makeApp } from "./helpers.js";

it("a fresh app starts on a blank run: no hologram until Kit builds one, and no test plans loaded", async () => {
  const t = await makeApp({ defaultSeed: "blank", testFixtures: false });
  try {
    const r = await t.app.inject({ method: "GET", url: "/v1/assemblies/current", headers: auth });
    expect(r.statusCode).toBe(200);
    expect(r.json()).toMatchObject({ plan_id: "plan_blank", seed: "blank" });
    const state = (await t.app.inject({ method: "GET", url: `/v1/assemblies/${r.json().assembly_id}/state`, headers: auth })).json();
    expect([state.progress.total, state.current_step_id]).toEqual([0, null]);
    const plans = (await t.app.inject({ method: "GET", url: "/v1/plans", headers: auth })).json().plans.map((p: { plan_id: string }) => p.plan_id);
    expect(plans).toEqual(["plan_blank"]);
  } finally { await t.cleanup(); }
});

it("an unknown default seed falls back to the blank run", async () => {
  const t = await makeApp({ defaultSeed: "no_such_seed", testFixtures: false });
  try {
    const r = await t.app.inject({ method: "GET", url: "/v1/assemblies/current", headers: auth });
    expect(r.json()).toMatchObject({ seed: "blank" });
  } finally { await t.cleanup(); }
});
