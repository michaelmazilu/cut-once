// Explicit candidate conversion input, not a runtime model download or package upgrade.
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const project = join(root, "apps/quest");
const manifest = JSON.parse(readFileSync(join(root, "tools/quest/fixtures/recognition-model.json"), "utf8"));
const expectedPath = "Assets/CutOnce/Vision/Editor/ModelSource/yolov9-source.onnx";
if (manifest.sourceAssetPath !== expectedPath || !/^[a-f0-9]{32}$/.test(manifest.sourceAssetGuid))
  throw new Error("Unexpected source model path or asset GUID in pinned manifest.");
if (!manifest.sourceUrl.includes(`/${manifest.sourceCommit}/`) || !/^[a-f0-9]{40}$/.test(manifest.sourceCommit))
  throw new Error("Model download must use a full pinned upstream commit.");
if (!existsSync(join(project, manifest.licenseNoticeAssetPath)))
  throw new Error("The source model's committed license notice is missing.");

const output = join(project, expectedPath);
const checksum = bytes => createHash("sha256").update(bytes).digest("hex");
if (existsSync(output)) {
  const existing = readFileSync(output);
  if (existing.length !== manifest.sourceBytes || checksum(existing) !== manifest.sourceSha256)
    throw new Error("An existing source model differs from the pinned file; refusing to overwrite it.");
} else {
  const response = await fetch(manifest.sourceUrl, { signal: AbortSignal.timeout(60000) });
  if (!response.ok) throw new Error(`Source model download failed: HTTP ${response.status}`);
  const bytes = Buffer.from(await response.arrayBuffer());
  if (bytes.length !== manifest.sourceBytes || checksum(bytes) !== manifest.sourceSha256)
    throw new Error("Source model size/checksum does not match the pinned manifest.");
  mkdirSync(dirname(output), { recursive: true });
  writeFileSync(output, bytes, { flag: "wx" });
}

const metaPath = output + ".meta";
if (existsSync(metaPath)) {
  if (!readFileSync(metaPath, "utf8").includes(`guid: ${manifest.sourceAssetGuid}`))
    throw new Error("Existing source model metadata has an unexpected GUID; refusing to replace it.");
} else {
  // The ONNX importer GUID is verified against the project's pinned Inference Engine 2.6.1.
  const metadata = [
    "fileFormatVersion: 2", `guid: ${manifest.sourceAssetGuid}`, "ScriptedImporter:",
    "  internalIDToNameTable: []", "  externalObjects: {}", "  serializedVersion: 2",
    "  userData:", "  assetBundleName:", "  assetBundleVariant:",
    "  script: {fileID: 11500000, guid: f22407ba6b4157b4a93d0a670bd3dd57, type: 3}",
    "  dynamicDimConfigs: []", "",
  ].join("\n");
  writeFileSync(metaPath, metadata, { flag: "wx" });
}
console.log(`Verified editor-only ${manifest.modelName} FP32 source: ${manifest.sourceSha256}`);
console.log("Bundled runtime model unchanged. Run the explicit Unity conversion command to create a candidate.");
