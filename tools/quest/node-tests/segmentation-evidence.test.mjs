import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtempSync, readFileSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import { verifySegmentationEvidence } from "../verify-segmentation-evidence.mjs";

const digest = (bytes) => createHash("sha256").update(bytes).digest("hex");
function fixture(modify = () => {}) {
  const root = mkdtempSync(join(tmpdir(), "segmentation-evidence-"));
  const semantic = {
    passed: true, confidence: 0.4, nmsIou: 0.5, minimumBboxIou: 0.4, minimumMaskIou: 0.5,
    photos: ["000000160012.jpg", "000000146489.jpg"].map(fileName => ({
      fileName, passed: true, checks: ["bottle", "dining table"].map(className => ({
        className, passed: true, found: true, score: 0.8, bboxIou: 0.9, maskIou: 0.8,
      })),
    })),
  };
  const numerical = { passed: true };
  modify(semantic, numerical);
  const manifest = { schemaVersion: 1, candidateOnly: true, dtype: "float32-le", reports: {} };
  for (const [name, report] of Object.entries({ semantic, numerical })) {
    const bytes = JSON.stringify(report);
    const file = `${name}-report.json`;
    writeFileSync(join(root, file), bytes);
    manifest.reports[name] = { file, sha256: digest(bytes) };
  }
  return { root, manifest, pin: repin(root, manifest) };
}
function repin(root, manifest) {
  const bytes = JSON.stringify(manifest);
  writeFileSync(join(root, "manifest.json"), bytes);
  return digest(bytes);
}

test("accepts all four recorded masks only after verifying manifest and sidecar pins", () => {
  const { root, pin } = fixture();
  assert.deepEqual(verifySegmentationEvidence(root, pin), { manifestSha256: pin, photographs: 2, objectMasks: 4 });
});
test("requires a valid externally supplied manifest pin", () => {
  const { root } = fixture();
  for (const pin of [undefined, "", "0".repeat(63), "0".repeat(64)])
    assert.throws(() => verifySegmentationEvidence(root, pin), /SHA-256|checksum/);
});
test("rejects altered semantic report bytes even if passed remains true", () => {
  const { root, pin } = fixture();
  const file = join(root, "semantic-report.json");
  writeFileSync(file, readFileSync(file, "utf8") + "\n");
  assert.throws(() => verifySegmentationEvidence(root, pin), /semantic report checksum/);
});
test("numeric success cannot hide a failed or missing object mask", () => {
  for (const modify of [
    r => { r.passed = false; },
    r => { r.photos[0].checks[1].passed = false; },
    r => { r.photos[0].checks[1].found = false; },
    r => { r.photos[0].checks[1].maskIou = 0.49; },
    r => { r.photos[0].checks[1].score = 0.39; },
    r => { r.photos[0].checks[1].bboxIou = null; },
    r => { r.photos[0].checks.pop(); },
    r => { r.photos.pop(); },
    r => { r.photos[1] = r.photos[0]; },
    r => { r.photos[0].checks[1] = r.photos[0].checks[0]; },
  ]) {
    const { root, pin } = fixture(modify);
    assert.throws(() => verifySegmentationEvidence(root, pin));
  }
});
test("rejects altered acceptance thresholds and failed numerical evidence", () => {
  for (const modify of [
    r => { r.confidence = 0.25; },
    r => { r.nmsIou = 0.6; },
    r => { r.minimumMaskIou = 0.4; },
    (_, n) => { n.passed = false; },
  ]) {
    const { root, pin } = fixture(modify);
    assert.throws(() => verifySegmentationEvidence(root, pin));
  }
});
test("rejects absolute, parent traversal and symlink escapes in sidecar paths", () => {
  for (const kind of ["absolute", "parent", "symlink"]) {
    const { root, manifest } = fixture();
    const outside = join(mkdtempSync(join(tmpdir(), "segmentation-outside-")), "report.json");
    writeFileSync(outside, "{}");
    if (kind === "symlink") symlinkSync(outside, join(root, "link.json"));
    manifest.reports.semantic.file = kind === "absolute" ? outside : kind === "parent" ? `../${outside.split("/").slice(-2).join("/")}` : "link.json";
    assert.throws(() => verifySegmentationEvidence(root, repin(root, manifest)), /pinned|escapes/);
  }
});
