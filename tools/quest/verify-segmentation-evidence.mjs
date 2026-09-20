import { createHash } from "node:crypto";
import { readFileSync, realpathSync } from "node:fs";
import { dirname, isAbsolute, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const hash = (bytes) => createHash("sha256").update(bytes).digest("hex");
const isHash = (value) => typeof value === "string" && /^[a-f0-9]{64}$/.test(value);

/** Verify the same-run artifact before accepting its separate semantic gate. */
export function verifySegmentationEvidence(directory, expectedManifestSha256) {
  if (!isHash(expectedManifestSha256)) throw new Error("A pinned segmentation manifest SHA-256 is required.");
  const root = realpathSync(directory);
  const manifestBytes = readFileSync(join(root, "manifest.json"));
  if (hash(manifestBytes) !== expectedManifestSha256) throw new Error("Segmentation manifest checksum mismatch.");
  const manifest = JSON.parse(manifestBytes.toString("utf8"));
  if (manifest.schemaVersion !== 1 || manifest.candidateOnly !== true || manifest.dtype !== "float32-le")
    throw new Error("Unexpected segmentation candidate manifest contract.");

  function report(name) {
    const spec = manifest.reports?.[name];
    if (!spec || typeof spec.file !== "string" || isAbsolute(spec.file) || !isHash(spec.sha256))
      throw new Error(`Missing pinned ${name} report.`);
    const path = realpathSync(resolve(root, spec.file));
    const child = relative(root, path);
    if (!child || child === ".." || child.startsWith(`..${sep}`) || isAbsolute(child))
      throw new Error(`${name} report escapes the artifact directory.`);
    const bytes = readFileSync(path);
    if (hash(bytes) !== spec.sha256) throw new Error(`${name} report checksum mismatch.`);
    return JSON.parse(bytes.toString("utf8"));
  }

  const numerical = report("numerical");
  const semantic = report("semantic");
  if (numerical.passed !== true) throw new Error("Export numerical comparison failed.");
  if (semantic.confidence !== 0.4 || semantic.nmsIou !== 0.5 ||
      semantic.minimumBboxIou !== 0.4 || semantic.minimumMaskIou !== 0.5)
    throw new Error("Semantic acceptance thresholds differ from the fixed regression contract.");
  const photos = semantic.photos;
  const expectedNames = new Set(["000000160012.jpg", "000000146489.jpg"]);
  if (!Array.isArray(photos) || photos.length !== expectedNames.size)
    throw new Error("Both unchanged recognition photographs are required.");
  for (const photo of photos) {
    if (!expectedNames.delete(photo.fileName)) throw new Error("Unexpected or duplicated semantic photograph.");
    const classes = new Set(["bottle", "dining table"]);
    if (!Array.isArray(photo.checks) || photo.checks.length !== classes.size)
      throw new Error("Both bottle and dining-table mask checks are required for each photograph.");
    for (const check of photo.checks) {
      if (!classes.delete(check.className)) throw new Error("Unexpected or duplicated semantic target.");
      if (check.passed !== true || check.found !== true || !Number.isFinite(check.score) || check.score < 0.4 ||
          !Number.isFinite(check.bboxIou) || check.bboxIou < 0.4 || check.bboxIou > 1 ||
          !Number.isFinite(check.maskIou) || check.maskIou < 0.5 || check.maskIou > 1)
        throw new Error(`Semantic mask acceptance failed for ${photo.fileName}: ${check.className}.`);
    }
    if (photo.passed !== true) throw new Error(`Semantic mask acceptance failed for ${photo.fileName}.`);
  }
  if (semantic.passed !== true) throw new Error("Semantic mask acceptance failed; numerical agreement is not segmentation accuracy.");
  return { manifestSha256: expectedManifestSha256, photographs: photos.length, objectMasks: 4 };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
  try {
    const result = verifySegmentationEvidence(process.argv[2] ?? join(root, "apps/quest/Logs/cli/segmentation-inputs"),
      process.env.SEGMENTATION_MANIFEST_SHA256);
    console.log(`Pinned semantic mask gate passed: ${result.objectMasks} masks in ${result.photographs} recorded photographs. Live Quest alignment and speed still require hardware.`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
