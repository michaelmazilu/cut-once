/** Fast, platform-independent guard for the three Quest demo pillars. Run: pnpm quest:pillars. */
import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
type Reader = (absolutePath: string) => string;

export interface PillarProblem { id: string; message: string }

const files = {
  package: "package.json",
  contract: "docs/quest-three-pillars.md",
  manifest: "apps/quest/Assets/Plugins/Android/AndroidManifest.xml",
  app: "apps/quest/Assets/CutOnce/Device/CutOnceApp.cs",
  controller: "apps/quest/Assets/CutOnce/Copilot/CopilotController.cs",
  client: "apps/quest/Assets/CutOnce/Copilot/Net/CopilotClient.cs",
  fastpath: "services/api/src/copilot/fastpath.ts",
  scanner: "apps/quest/Assets/CutOnce/Vision/RoomScanner.cs",
  tracker: "apps/quest/Assets/CutOnce/Vision/TrackedObjectManager.cs",
  visualizer: "apps/quest/Assets/CutOnce/Vision/ObjectVisualizer.cs",
  build: "apps/quest/Assets/CutOnce/Device/Build/BuildMode.cs",
} as const;

/**
 * These checks are deliberately structural, not formatting rules. Unity owns the behavioural tests, but those only run
 * on a licensed runner. This guard makes removing a headset fallback or the physical-Quest configuration path fail on
 * ordinary Linux/Windows CI too. If the architecture changes, update implementation, behavioural test and contract in
 * the same change; do not merely weaken this list.
 */
export function pillarProblems(root = ROOT, read: Reader = p => readFileSync(p, "utf8")): PillarProblem[] {
  const text = (key: keyof typeof files) => read(join(root, files[key]));
  const problem: PillarProblem[] = [];
  const needs = (id: string, ok: boolean, message: string) => { if (!ok) problem.push({ id, message }); };

  const contract = text("contract");
  needs("P0-CONTRACT", /Passive vision[\s\S]*Kit response[\s\S]*Blueprint creation/i.test(contract),
    "docs/quest-three-pillars.md must define all three protected behaviours.");

  const pkg = JSON.parse(text("package")) as { scripts?: Record<string, string> };
  for (const command of ["quest:pillars", "copilot:check", "quest:serve", "quest:tunnel", "quest:connect"])
    needs("P0-COMMANDS", Boolean(pkg.scripts?.[command]), `package.json must expose pnpm ${command}.`);

  const manifest = text("manifest");
  for (const permission of ["horizonos.permission.HEADSET_CAMERA", "android.permission.RECORD_AUDIO", "com.oculus.permission.USE_SCENE"])
    needs("P0-PERMISSIONS", manifest.includes(permission), `AndroidManifest.xml must retain ${permission}.`);

  const app = text("app");
  needs("P1-COPILOT-WIRING", /controller\.Configure\(_config\.BaseUrl,\s*_config\.api_token/.test(app),
    "CutOnceApp must inject runtime server settings into created and scene-placed Copilots.");
  needs("P1-COPILOT-WIRING", /if \(createCopilot \|\| FindAnyObjectByType<CopilotController>\(\) != null\) TryCreateCopilot\(\)/.test(app),
    "A scene-placed Copilot must be configured even when automatic creation is disabled.");
  needs("P1-CONFIG", /File\.Exists\(pushed\)[\s\S]*Resources\.Load<TextAsset>\("CutOnce\/config"\)/.test(app),
    "The private config pushed by quest:connect must win over the APK-bundled Editor config.");
  needs("P1-DIAGNOSTIC", /Run\(ReportConnectivity\(\)\)/.test(app),
    "The headset must visibly diagnose an unreachable/localhost server at startup.");

  const controller = text("controller");
  needs("P1-COPILOT-WIRING", /public void Configure\([\s\S]*baseUrl = url;[\s\S]*apiToken = token;[\s\S]*Bind\(\);/.test(controller),
    "CopilotController.Configure must rebind both credentials and runtime interfaces.");
  const timeout = text("client").match(/timeoutSeconds\s*=\s*([0-9]+)f/);
  needs("P1-LATENCY", Boolean(timeout && Number(timeout[1]) >= 20),
    "The Quest query timeout must leave at least 20 seconds for upload, the server cap and tunnel latency.");
  const fastpath = text("fastpath");
  needs("P1-BUILD-INSTRUCTIONS", /INSTRUCTION_ASK/.test(fastpath)
    && /answer_text:\s*`Step \$\{step\.index\} of \$\{plan\.steps\.length\}\. \$\{step\.instruction\}`/.test(fastpath)
    && /highlight_parts:\s*step\.part_ids/.test(fastpath),
    "An explicit build-instruction prompt must speak the authoritative current step and highlight its parts.");

  const scanner = text("scanner");
  needs("P2-VISION-FALLBACK", /Visualizer\.Show\(o, o == focus, Drawable\(o\)\)/.test(scanner),
    "Recognised objects must reach ObjectVisualizer even before a measured box exists.");
  needs("P2-VISION-FALLBACK", !/if \(!o\.visible \|\| !Drawable\(o\)\)/.test(scanner),
    "Do not hide recognition merely because measured geometry is unavailable.");
  const tracker = text("tracker");
  needs("P2-VISION-TRACKING", /public Vector3 DisplayCentre\s*=>\s*smoothedWorldPosition/.test(tracker),
    "Measured highlights must keep following the continuously updated tracked position between box fits.");
  const visualizer = text("visualizer");
  needs("P2-VISION-FALLBACK", /bool showGeometry = true/.test(visualizer)
    && /cached\.highlight\.enabled = showGeometry/.test(visualizer)
    && /unmeasuredLabelHeight/.test(visualizer),
    "Unmeasured detections need a label-only fallback while unsafe guessed geometry stays hidden.");

  const build = text("build");
  needs("P3-BLUEPRINT-RECOVERY", /OnScanAccepted\(ticket, accepted\.session_id\)[\s\S]{0,160}RecoverSession\(accepted\.session_id\)/.test(build),
    "Every accepted scan must start blueprint session recovery.");
  needs("P3-BLUEPRINT-RECOVERY", /async Task RecoverSession\([\s\S]*await CatchUp\(\)/.test(build),
    "Blueprint recovery must poll the authoritative session snapshot, not depend only on WebSocket delivery.");

  return problem;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const problems = pillarProblems();
  if (problems.length) {
    for (const p of problems) console.error(`FAIL ${p.id}: ${p.message}`);
    process.exitCode = 1;
  } else console.log("Quest three-pillar gate PASS: passive vision, Kit response, and blueprint recovery contracts are present.");
}
