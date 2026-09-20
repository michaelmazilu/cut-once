// Download pinned public regression inputs, not private headset camera captures.
import { createHash } from "node:crypto";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const manifestPath = join(root, "tools/quest/fixtures/recognition-coco.json");
const output = join(root, "apps/quest/Logs/cli/recognition-fixtures");
const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
mkdirSync(output, { recursive: true });
for (const fixture of manifest.fixtures) {
  if (basename(fixture.fileName) !== fixture.fileName) throw new Error("Invalid fixture filename");
  const response = await fetch(fixture.url, { signal: AbortSignal.timeout(30000) });
  if (!response.ok) throw new Error(`Fixture download failed: ${response.status} ${fixture.fileName}`);
  const bytes = Buffer.from(await response.arrayBuffer());
  const sha256 = createHash("sha256").update(bytes).digest("hex");
  if (sha256 !== fixture.sha256) throw new Error(`Fixture checksum mismatch: ${fixture.fileName}`);
  writeFileSync(join(output, fixture.fileName), bytes);
  console.log(`Verified COCO input: ${fixture.fileName}`);
}
writeFileSync(join(output, "sources.json"), JSON.stringify(manifest, null, 2));
