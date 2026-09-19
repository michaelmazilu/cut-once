// Copies the shared fixtures and demo plans into the Unity project (Unity and symlinks do not mix well).
import { cpSync, existsSync, mkdirSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const quest = join(root, "apps", "quest", "Assets");
if (!existsSync(quest)) { console.log("apps/quest/Assets does not exist yet: nothing to sync"); process.exit(0); }

const fixtures = join(quest, "CutOnce", "Core", "Tests", "Fixtures");
const streaming = join(quest, "StreamingAssets");
mkdirSync(fixtures, { recursive: true }); mkdirSync(streaming, { recursive: true });
cpSync(join(root, "data", "fixtures"), fixtures, { recursive: true });
let copied = 0;
// Plans and models for the app, and the stored camera frame the copilot's FixtureFrameSource reads in the Editor.
const wanted = [["data/fixtures", /^frame_.*\.(jpg|pose\.json)$/]];
for (const [dir, pattern] of wanted) {
  const abs = join(root, dir);
  if (!existsSync(abs)) continue;
  for (const f of readdirSync(abs).filter((f) => pattern.test(f))) { cpSync(join(abs, f), join(streaming, f)); copied++; }
}
console.log(`synced fixtures to ${fixtures} and ${copied} files to ${streaming}`);
