using System;
using System.IO;
using System.Security.Cryptography;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;

namespace CutOnce.Vision.Editor
{
    /// <summary>
    /// Explicit batch-only candidate conversion. The normal setup/check/build path does not call
    /// this method. Imports a checksum-pinned upstream FP32 ONNX and uses the same three-output
    /// functional graph as Meta's converter, intentionally omitting weight quantization.
    /// </summary>
    public static class ExportRecognitionModel
    {
        private const string SourceAsset = "Assets/CutOnce/Vision/Editor/ModelSource/yolov9-source.onnx";
        private const string OutputAsset = "Assets/CutOnce/Vision/Resources/yolov9sentis.sentis";

        [Serializable]
        private sealed class Manifest
        {
            public int schemaVersion;
            public string modelName, sourceCommit, sourceUrl, sourceSha256, sourceAssetPath;
            public long sourceBytes;
            public string sourceAssetGuid, license, licenseUrl, licenseNoticeAssetPath, outputAssetPath, precision, outputGraph;
        }

        [Serializable]
        private sealed class Report
        {
            public string scope = "Explicit FP32 candidate conversion; not recognition accuracy, live-camera or headset-performance proof.";
            public bool passed, outputReplaced;
            public string error, utc, unity, inferenceEngineVersion;
            public Manifest provenance;
            public string sourceSha256, previousBundledSha256, convertedSha256, outputMetaSha256, inputShape;
            public string candidateArtifact = "yolov9-fp32.sentis";
            public string previousArtifact = "previous-bundled.sentis";
            public string licenseArtifact = "MODEL-LICENSE.txt";
            public long candidateBytes;
            public int outputCount;
            public bool quantized = false;
        }

        public static void Run()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Candidate model conversion must run in a separate batchmode Editor.");
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/cli/model-conversion"));
            Directory.CreateDirectory(directory);
            var report = new Report { utc = DateTime.UtcNow.ToString("O"), unity = Application.unityVersion };
            try
            {
                var manifestPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../tools/quest/fixtures/recognition-model.json"));
                var manifestJson = File.ReadAllText(manifestPath);
                var manifest = JsonUtility.FromJson<Manifest>(manifestJson);
                report.provenance = manifest;
                if (manifest == null || manifest.schemaVersion != 1 || manifest.sourceAssetPath != SourceAsset || manifest.outputAssetPath != OutputAsset)
                    throw new InvalidOperationException("Unexpected model conversion paths/schema in the pinned manifest.");
                report.inferenceEngineVersion = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ModelLoader).Assembly)?.version;
                if (report.inferenceEngineVersion != "2.6.1")
                    throw new InvalidOperationException("Conversion was verified for pinned Inference Engine 2.6.1, not " + report.inferenceEngineVersion);

                report.sourceSha256 = Sha256(File.ReadAllBytes(SourceAsset));
                if (report.sourceSha256 != manifest.sourceSha256 || new FileInfo(SourceAsset).Length != manifest.sourceBytes)
                    throw new InvalidOperationException("Source ONNX checksum/size does not match the pinned manifest.");
                var originalMeta = File.ReadAllBytes(OutputAsset + ".meta");
                report.outputMetaSha256 = Sha256(originalMeta);
                report.previousBundledSha256 = Sha256(File.ReadAllBytes(OutputAsset));
                File.Copy(OutputAsset, Path.Combine(directory, report.previousArtifact), true);
                File.Copy(manifest.licenseNoticeAssetPath, Path.Combine(directory, report.licenseArtifact), true);
                File.WriteAllText(Path.Combine(directory, "provenance.json"), manifestJson);

                AssetDatabase.ImportAsset(SourceAsset, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                if (AssetDatabase.AssetPathToGUID(SourceAsset) != manifest.sourceAssetGuid)
                    throw new InvalidOperationException("Imported ONNX source GUID does not match the pinned manifest.");
                var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(SourceAsset);
                if (asset == null) throw new InvalidOperationException("Unity did not import the upstream ONNX as a ModelAsset.");
                var model = ModelLoader.Load(asset);
                if (model.inputs.Count != 1 || model.inputs[0].shape.Get(0) != 1 || model.inputs[0].shape.Get(1) != 3 ||
                    model.inputs[0].shape.Get(2) != 640 || model.inputs[0].shape.Get(3) != 640)
                    throw new InvalidOperationException("Expected the original model's NCHW 1x3x640x640 input.");

                // Identical to Meta's SentisModelEditorConverter output graph. No threshold, NMS,
                // input normalization or resizing is baked here; production code owns those steps.
                var graph = new FunctionalGraph();
                var input = graph.AddInput(model, 0);
                var raw = Functional.Forward(model, input)[0];
                var boxCoords = raw[0, ..4, ..].Transpose(0, 1);
                var allScores = raw[0, 4.., ..].Transpose(0, 1);
                var scores = Functional.ReduceMax(allScores, 1);
                var classIds = Functional.ArgMax(allScores, 1);
                var centersToCorners = Functional.Constant(new TensorShape(4, 4), new[]
                {
                    1f, 0f, 1f, 0f,
                    0f, 1f, 0f, 1f,
                    -.5f, 0f, .5f, 0f,
                    0f, -.5f, 0f, .5f,
                });
                var corners = Functional.MatMul(boxCoords, centersToCorners);
                var converted = graph.Compile(corners, classIds, scores);
                // Deliberately NO ModelQuantizer.QuantizeWeights call: this is the FP32 control.
                var candidatePath = Path.Combine(directory, report.candidateArtifact);
                ModelWriter.Save(candidatePath, converted);
                var roundTrip = ModelLoader.Load(candidatePath);
                report.inputShape = roundTrip.inputs[0].shape.ToString();
                report.outputCount = roundTrip.outputs.Count;
                if (report.outputCount != 3) throw new InvalidOperationException("Converted model must expose boxes, class IDs and scores.");
                report.convertedSha256 = Sha256(File.ReadAllBytes(candidatePath));
                report.candidateBytes = new FileInfo(candidatePath).Length;

                // Keep the existing asset path/GUID: this changes only the model bytes in this
                // explicitly selected candidate workspace. The original is recoverable above.
                File.Copy(candidatePath, OutputAsset, true);
                report.outputReplaced = true;
                AssetDatabase.ImportAsset(OutputAsset, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var imported = AssetDatabase.LoadAssetAtPath<ModelAsset>(OutputAsset);
                if (imported == null || ModelLoader.Load(imported).outputs.Count != 3)
                    throw new InvalidOperationException("Reimported runtime candidate is not a valid three-output model.");
                if (Sha256(File.ReadAllBytes(OutputAsset + ".meta")) != report.outputMetaSha256)
                    throw new InvalidOperationException("Unity modified existing model metadata; inspect before continuing.");
                if (Sha256(File.ReadAllBytes(OutputAsset)) != report.convertedSha256)
                    throw new InvalidOperationException("Runtime candidate checksum changed while importing.");
                report.passed = true;
                Debug.Log($"[Recognition model] FP32 candidate exported: {report.candidateBytes} bytes, SHA-256 {report.convertedSha256}. Existing metadata preserved.");
            }
            catch (Exception error)
            {
                report.error = error.ToString();
                Debug.LogException(error);
            }
            finally
            {
                File.WriteAllText(Path.Combine(directory, "report.json"), JsonUtility.ToJson(report, true));
                EditorApplication.Exit(report.passed ? 0 : 1);
            }
        }

        private static string Sha256(byte[] bytes)
        {
            using var digest = SHA256.Create();
            return BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
