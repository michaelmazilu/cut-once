using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CutOnce.Vision.Editor
{
    /// <summary>
    /// Explicit, isolated Editor-only importer/backend gate for the segmentation candidate.
    /// Compares every raw-head and mask-decoder output element against pinned Torch references.
    /// No production model, scene, detector threshold or runtime integration is changed.
    /// Run with graphics enabled, -batchmode, and no -quit:
    /// -executeMethod CutOnce.Vision.Editor.SegmentationProof.Run
    /// -segmentationManifest /absolute/segmentation-inputs/manifest.json
    /// -segmentationManifestSha256 optional-expected-manifest-sha256
    /// </summary>
    public static class SegmentationProof
    {
        private const string SourceDirectory = "Assets/CutOnce/Vision/Editor/SegmentationSource";
        private const string InferenceVersion = "2.6.1";
        private const double AbsoluteTolerance = .01;
        private const double RelativeTolerance = .001;
        private const double CaseDeadlineSeconds = 120;
        private const int MaximumTensorElements = 16 * 1024 * 1024;
        private static readonly string[] RequiredCases =
        {
            "raw-coco-146489", "masks8-coco-146489", "raw-coco-160012", "masks8-coco-160012",
        };

        [Serializable]
        private sealed class FileSpec
        {
            public string file, sha256;
        }

        [Serializable]
        private sealed class TensorSpec
        {
            public string name, file, sha256;
            public int[] shape;
        }

        [Serializable]
        private sealed class ModelSpec
        {
            public string name;
            public FileSpec onnx;
            public TensorSpec[] inputs, outputs;
        }

        [Serializable]
        private sealed class Manifest
        {
            public int schemaVersion;
            public string dtype;
            public bool candidateOnly;
            public ModelSpec[] models;
        }

        [Serializable]
        private sealed class Report
        {
            public int schemaVersion = 1;
            public string scope = "Editor-only numeric import/backend gate for a segmentation candidate. " +
                "Compares all supplied raw heads and fixed-eight-mask logits against official Torch reference tensors. " +
                "Not live-camera recognition, object-mask accuracy, headset alignment, or Quest performance proof. " +
                "Production models and scenes are not modified.";
            public string backendScope = "CPU and GPUCompute are requested independently. Inference Engine may internally fall back to CPU for unsupported GPU operators; a GPUCompute pass is not a pure-GPU execution claim.";
            public string timingScope = "One cold invocation per case/backend, including Editor scheduling and readback. Durations are diagnostic, not warmed-up throughput or a headset frame-rate measurement.";
            public string comparison = "Every output element must be finite and abs(actual-reference) <= atol + rtol * abs(reference). No masks, classes, elements, or failed cases are excluded.";
            public string utc, unity, inferenceEngineVersion, graphics, graphicsApi;
            public string manifestSha256, expectedManifestSha256, error, cleanup;
            public bool passed, graphicsAvailable, computeSupported, manifestPinProvided;
            public double atol = AbsoluteTolerance, rtol = RelativeTolerance, caseDeadlineSeconds = CaseDeadlineSeconds;
            public int expectedCases = RequiredCases.Length * 2, completedCases;
            public List<CaseResult> cases = new List<CaseResult>();
        }

        [Serializable]
        private sealed class CaseResult
        {
            public string name, backend, onnxFile, onnxSha256, importedAssetPath, error;
            public bool passed, completed, timedOut, reusedImportedModel;
            public double importMilliseconds, scheduleWallMilliseconds, scheduleCpuMilliseconds, executionAndReadbackMilliseconds, comparisonMilliseconds;
            public int scheduledLayers;
            public List<TensorProvenance> inputs = new List<TensorProvenance>();
            public List<OutputResult> outputs = new List<OutputResult>();
        }

        [Serializable]
        private sealed class TensorProvenance
        {
            public string name, file, sha256;
            public int[] shape;
            public int elements;
        }

        [Serializable]
        private sealed class OutputResult
        {
            public string name, referenceFile, referenceSha256, actualType, error;
            public int[] expectedShape, actualShape;
            public int expectedElements, comparedElements, mismatchCount, nonFiniteCount;
            public int maximumAbsoluteErrorIndex = -1, firstMismatchIndex = -1;
            public double maximumAbsoluteError, maximumRelativeError, meanAbsoluteError;
            public double actualAtMaximumAbsoluteError, referenceAtMaximumAbsoluteError;
            public bool passed;
        }

        private sealed class Execution
        {
            public ModelSpec spec;
            public CaseResult result;
            public Worker worker;
            public IEnumerator schedule;
            public readonly List<Tensor<float>> inputs = new List<Tensor<float>>();
            public readonly List<Tensor> outputs = new List<Tensor>();
            public readonly List<float[]> references = new List<float[]>();
            public double startedAt;
            public bool readbackRequested;

            public void Dispose()
            {
                (schedule as IDisposable)?.Dispose();
                schedule = null;
                // PeekOutput values belong to the worker, not this proof. Dispose only owned inputs.
                worker?.Dispose();
                worker = null;
                foreach (var input in inputs) input.Dispose();
                inputs.Clear();
            }
        }

        private static readonly Dictionary<string, Model> ImportedModels = new Dictionary<string, Model>();
        private static Report _report;
        private static Manifest _manifest;
        private static string _inputDirectory, _reportDirectory;
        private static int _nextCase;
        private static Execution _execution;
        private static bool _finished;

        public static void Run()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("SegmentationProof must run in its own graphics-enabled batchmode Editor.");
            _reportDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/cli/segmentation-proof"));
            Directory.CreateDirectory(_reportDirectory);
            _report = new Report
            {
                utc = DateTime.UtcNow.ToString("O"), unity = Application.unityVersion,
                graphics = SystemInfo.graphicsDeviceName, graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                graphicsAvailable = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null,
                computeSupported = SystemInfo.supportsComputeShaders,
            };
            try
            {
                if (!_report.graphicsAvailable) throw new InvalidOperationException("Graphics are required; do not pass -nographics.");
                if (!BitConverter.IsLittleEndian) throw new InvalidOperationException("Reference tensors require a little-endian host.");
                _report.inferenceEngineVersion = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ModelLoader).Assembly)?.version;
                if (_report.inferenceEngineVersion != InferenceVersion)
                    throw new InvalidOperationException("Expected pinned Inference Engine " + InferenceVersion + ", not " + _report.inferenceEngineVersion);
                var manifestPath = Path.GetFullPath(Argument("-segmentationManifest") ??
                    Path.Combine(Application.dataPath, "../Logs/cli/segmentation-inputs/manifest.json"));
                _inputDirectory = Path.GetDirectoryName(manifestPath);
                var manifestBytes = File.ReadAllBytes(manifestPath);
                _report.manifestSha256 = Sha256(manifestBytes);
                _report.expectedManifestSha256 = Argument("-segmentationManifestSha256");
                _report.manifestPinProvided = !string.IsNullOrEmpty(_report.expectedManifestSha256);
                if (_report.manifestPinProvided && !HashMatches(_report.manifestSha256, _report.expectedManifestSha256))
                    throw new InvalidOperationException("Manifest SHA-256 does not match the requested pin.");
                _manifest = JsonUtility.FromJson<Manifest>(System.Text.Encoding.UTF8.GetString(manifestBytes));
                ValidateManifest();
                // Preserve the full manifest, including exporter metadata unknown to JsonUtility.
                File.WriteAllBytes(Path.Combine(_reportDirectory, "input-manifest.json"), manifestBytes);
                EditorApplication.update += Tick;
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch (Exception error)
            {
                _report.error = error.ToString();
                Finish();
            }
        }

        private static void ValidateManifest()
        {
            if (_manifest == null || _manifest.schemaVersion != 1 || _manifest.dtype != "float32-le" || !_manifest.candidateOnly)
                throw new InvalidDataException("Expected a schemaVersion 1, float32-le, candidateOnly segmentation manifest.");
            if (_manifest.models == null || _manifest.models.Length != RequiredCases.Length)
                throw new InvalidDataException("All four agreed photo/raw-head/mask-decoder records are required.");
            var caseNames = new HashSet<string>(RequiredCases, StringComparer.Ordinal);
            foreach (var spec in _manifest.models)
            {
                if (spec == null || string.IsNullOrEmpty(spec.name) || !caseNames.Remove(spec.name))
                    throw new InvalidDataException("Unexpected or duplicate candidate case name.");
                if (spec.onnx == null || !string.Equals(Path.GetExtension(spec.onnx.file), ".onnx", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Each case must identify an ONNX file.");
                VerifyFile(spec.onnx.file, spec.onnx.sha256);
                ValidateTensorList(spec.inputs);
                ValidateTensorList(spec.outputs);
            }
        }

        private static void ValidateTensorList(TensorSpec[] tensors)
        {
            if (tensors == null || tensors.Length == 0) throw new InvalidDataException("Empty tensor manifest.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var spec in tensors)
            {
                if (spec == null || string.IsNullOrEmpty(spec.name) || !names.Add(spec.name))
                    throw new InvalidDataException("Missing or duplicate tensor name.");
                // Verify full contents up front, so malformed or modified references cannot yield a partial pass.
                ReadTensor(spec);
            }
        }

        private static void Tick()
        {
            if (_finished) return;
            try
            {
                if (_execution == null)
                {
                    if (_nextCase == _report.expectedCases) { Finish(); return; }
                    BeginCase();
                    EditorApplication.QueuePlayerLoopUpdate();
                    return;
                }
                var current = _execution;
                var elapsed = EditorApplication.timeSinceStartup - current.startedAt;
                if (elapsed > CaseDeadlineSeconds)
                {
                    current.result.timedOut = true;
                    current.result.error = "Case exceeded the unchanged 120-second scheduling/readback deadline.";
                    current.result.executionAndReadbackMilliseconds = elapsed * 1000;
                    _report.error = "A backend timed out. Remaining cases were not run; the gate failed.";
                    // Do not block on pending GPU/CPU work, reuse its worker, or orphan an async clone.
                    // The isolated batch process owns remaining native allocations until process exit.
                    Finish(pendingBackendWork: true);
                    return;
                }
                if (current.schedule != null)
                {
                    var tickStart = EditorApplication.timeSinceStartup;
                    for (var layers = 0; layers < 32; ++layers)
                    {
                        if (!current.schedule.MoveNext())
                        {
                            (current.schedule as IDisposable)?.Dispose();
                            current.schedule = null;
                            current.result.scheduleWallMilliseconds = (EditorApplication.timeSinceStartup - current.startedAt) * 1000;
                            break;
                        }
                        ++current.result.scheduledLayers;
                        if (EditorApplication.timeSinceStartup - tickStart > .008) break;
                    }
                    current.result.scheduleCpuMilliseconds += (EditorApplication.timeSinceStartup - tickStart) * 1000;
                    EditorApplication.QueuePlayerLoopUpdate();
                    return;
                }
                if (!current.readbackRequested)
                {
                    foreach (var spec in current.spec.outputs)
                    {
                        var output = current.worker.PeekOutput(spec.name);
                        if (output == null) throw new InvalidOperationException("Missing worker output " + spec.name);
                        current.outputs.Add(output);
                        output.ReadbackRequest();
                    }
                    current.readbackRequested = true;
                }
                foreach (var output in current.outputs)
                {
                    if (output.count != 0 && !output.IsReadbackRequestDone())
                    {
                        EditorApplication.QueuePlayerLoopUpdate();
                        return;
                    }
                }
                current.result.executionAndReadbackMilliseconds = (EditorApplication.timeSinceStartup - current.startedAt) * 1000;
                var compareStart = EditorApplication.timeSinceStartup;
                current.result.passed = true;
                for (var index = 0; index < current.outputs.Count; ++index)
                {
                    var result = Compare(current.spec.outputs[index], current.outputs[index], current.references[index]);
                    current.result.outputs.Add(result);
                    current.result.passed &= result.passed;
                }
                current.result.comparisonMilliseconds = (EditorApplication.timeSinceStartup - compareStart) * 1000;
                current.result.completed = true;
                ++_report.completedCases;
                current.Dispose();
                _execution = null;
                WriteReport();
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch (Exception error)
            {
                if (_execution != null)
                {
                    _execution.result.error = error.ToString();
                    _execution.result.passed = false;
                }
                _report.error = "Backend execution failed: " + error;
                // An exception during scheduling/readback may leave outstanding work. Stop this
                // isolated process rather than pretending subsequent cases ran or synchronously waiting.
                Finish(pendingBackendWork: _execution?.worker != null);
            }
        }

        private static void BeginCase()
        {
            var spec = _manifest.models[_nextCase / 2];
            var backend = _nextCase % 2 == 0 ? BackendType.CPU : BackendType.GPUCompute;
            ++_nextCase;
            var result = new CaseResult { name = spec.name, backend = backend.ToString(), onnxFile = spec.onnx.file, onnxSha256 = spec.onnx.sha256 };
            _report.cases.Add(result);
            var current = new Execution { spec = spec, result = result };
            _execution = current;
            try
            {
                if (backend == BackendType.GPUCompute && !_report.computeSupported)
                    throw new InvalidOperationException("GPUCompute is required for this gate but this Editor does not support compute shaders.");
                var importStart = EditorApplication.timeSinceStartup;
                var sourcePath = VerifyFile(spec.onnx.file, spec.onnx.sha256);
                result.importedAssetPath = SourceDirectory + "/candidate-" + spec.onnx.sha256.ToLowerInvariant() + ".onnx";
                var physicalSourceDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", SourceDirectory));
                var physicalAssetPath = Path.Combine(physicalSourceDirectory, Path.GetFileName(result.importedAssetPath));
                if (!ImportedModels.TryGetValue(spec.onnx.sha256, out var model))
                {
                    if (!Directory.Exists(physicalSourceDirectory))
                        throw new DirectoryNotFoundException("The committed Editor-only SegmentationSource directory is missing.");
                    if (File.Exists(physicalAssetPath))
                    {
                        if (!HashMatches(Sha256(File.ReadAllBytes(physicalAssetPath)), spec.onnx.sha256))
                            throw new InvalidDataException("Existing candidate asset differs from its content-addressed name; refusing to overwrite it.");
                    }
                    else File.Copy(sourcePath, physicalAssetPath, false);
                    AssetDatabase.ImportAsset(result.importedAssetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(result.importedAssetPath);
                    if (asset == null) throw new InvalidOperationException("Unity did not import the candidate ONNX as a ModelAsset.");
                    model = ModelLoader.Load(asset);
                    ImportedModels.Add(spec.onnx.sha256, model);
                }
                else result.reusedImportedModel = true;
                result.importMilliseconds = (EditorApplication.timeSinceStartup - importStart) * 1000;
                ValidateModel(model, spec);
                foreach (var tensor in spec.inputs)
                {
                    var values = ReadTensor(tensor);
                    current.inputs.Add(new Tensor<float>(new TensorShape(tensor.shape), values));
                    result.inputs.Add(new TensorProvenance { name = tensor.name, file = tensor.file, sha256 = tensor.sha256, shape = tensor.shape, elements = values.Length });
                }
                foreach (var tensor in spec.outputs) current.references.Add(ReadTensor(tensor));
                current.worker = new Worker(model, backend);
                for (var index = 0; index < spec.inputs.Length; ++index)
                    current.worker.SetInput(spec.inputs[index].name, current.inputs[index]);
                current.startedAt = EditorApplication.timeSinceStartup;
                current.schedule = current.worker.ScheduleIterable();
                Debug.Log("[Segmentation proof] " + spec.name + " on " + backend + ": comparing every output against pinned Torch references.");
            }
            catch (Exception error)
            {
                // Import/contract/backend-construction failures occur before scheduling; cleanup is
                // safe, and still attempt other records/backends to expose their individual results.
                result.error = error.ToString();
                result.completed = true;
                ++_report.completedCases;
                current.Dispose();
                _execution = null;
                WriteReport();
            }
        }

        private static void ValidateModel(Model model, ModelSpec spec)
        {
            if (model.inputs.Count != spec.inputs.Length || model.outputs.Count != spec.outputs.Length)
                throw new InvalidDataException("Imported model input/output counts do not match the complete reference contract.");
            foreach (var expected in spec.inputs)
            {
                var found = false;
                foreach (var actual in model.inputs)
                {
                    if (actual.name != expected.name) continue;
                    found = true;
                    if (actual.dataType != DataType.Float || actual.shape.rank != expected.shape.Length)
                        throw new InvalidDataException("Input type/rank mismatch: " + expected.name);
                    for (var axis = 0; axis < expected.shape.Length; ++axis)
                        if (actual.shape.Get(axis) != expected.shape[axis])
                            throw new InvalidDataException("Input shape mismatch: " + expected.name + " axis " + axis);
                }
                if (!found) throw new InvalidDataException("Missing imported model input " + expected.name);
            }
            foreach (var expected in spec.outputs)
            {
                var found = false;
                foreach (var actual in model.outputs) found |= actual.name == expected.name;
                if (!found) throw new InvalidDataException("Missing imported model output " + expected.name);
            }
        }

        private static OutputResult Compare(TensorSpec expected, Tensor output, float[] reference)
        {
            var result = new OutputResult
            {
                name = expected.name, referenceFile = expected.file, referenceSha256 = expected.sha256,
                expectedShape = expected.shape, expectedElements = reference.Length,
                actualShape = new int[output.shape.rank], actualType = output.GetType().Name,
            };
            for (var axis = 0; axis < result.actualShape.Length; ++axis) result.actualShape[axis] = output.shape[axis];
            try
            {
                if (!(output is Tensor<float> floats)) throw new InvalidDataException("Expected a float32 output tensor.");
                if (result.actualShape.Length != expected.shape.Length) throw new InvalidDataException("Output rank mismatch.");
                for (var axis = 0; axis < expected.shape.Length; ++axis)
                    if (result.actualShape[axis] != expected.shape[axis]) throw new InvalidDataException("Output shape mismatch at axis " + axis);
                using var cpu = floats.ReadbackAndClone();
                var values = cpu.AsReadOnlySpan();
                if (values.Length != reference.Length) throw new InvalidDataException("Output element count mismatch.");
                double errorSum = 0;
                for (var index = 0; index < reference.Length; ++index)
                {
                    var value = values[index];
                    ++result.comparedElements;
                    if (float.IsNaN(value) || float.IsInfinity(value))
                    {
                        ++result.nonFiniteCount;
                        ++result.mismatchCount;
                        if (result.firstMismatchIndex < 0) result.firstMismatchIndex = index;
                        continue;
                    }
                    var absoluteError = Math.Abs((double)value - reference[index]);
                    var relativeError = absoluteError / Math.Max(Math.Abs((double)reference[index]), 1e-12);
                    errorSum += absoluteError;
                    result.maximumRelativeError = Math.Max(result.maximumRelativeError, relativeError);
                    if (result.maximumAbsoluteErrorIndex < 0 || absoluteError > result.maximumAbsoluteError)
                    {
                        result.maximumAbsoluteError = absoluteError;
                        result.maximumAbsoluteErrorIndex = index;
                        result.actualAtMaximumAbsoluteError = value;
                        result.referenceAtMaximumAbsoluteError = reference[index];
                    }
                    if (absoluteError > AbsoluteTolerance + RelativeTolerance * Math.Abs(reference[index]))
                    {
                        ++result.mismatchCount;
                        if (result.firstMismatchIndex < 0) result.firstMismatchIndex = index;
                    }
                }
                var finiteCount = result.comparedElements - result.nonFiniteCount;
                result.meanAbsoluteError = finiteCount > 0 ? errorSum / finiteCount : 0;
                result.passed = result.comparedElements == reference.Length && result.mismatchCount == 0;
            }
            catch (Exception error) { result.error = error.ToString(); }
            return result;
        }

        private static float[] ReadTensor(TensorSpec spec)
        {
            var count = ElementCount(spec.shape);
            var path = VerifyFile(spec.file, spec.sha256);
            if (new FileInfo(path).Length != (long)count * sizeof(float))
                throw new InvalidDataException("Tensor byte count does not match its shape: " + spec.file);
            var values = new float[count];
            Buffer.BlockCopy(File.ReadAllBytes(path), 0, values, 0, count * sizeof(float));
            for (var index = 0; index < values.Length; ++index)
                if (float.IsNaN(values[index]) || float.IsInfinity(values[index]))
                    throw new InvalidDataException("Reference/input tensor contains a non-finite value: " + spec.file + " at " + index);
            return values;
        }

        private static int ElementCount(int[] shape)
        {
            if (shape == null || shape.Length == 0 || shape.Length > 8) throw new InvalidDataException("Invalid static tensor rank.");
            long count = 1;
            foreach (var dimension in shape)
            {
                if (dimension <= 0 || dimension > MaximumTensorElements) throw new InvalidDataException("Tensor dimensions must be positive and bounded.");
                count *= dimension;
                if (count > MaximumTensorElements) throw new InvalidDataException("Tensor exceeds this explicit proof's size limit.");
            }
            return (int)count;
        }

        private static string VerifyFile(string relativePath, string expectedHash)
        {
            if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath))
                throw new InvalidDataException("Manifest files must be relative to the artifact directory.");
            var fullPath = Path.GetFullPath(Path.Combine(_inputDirectory, relativePath));
            var prefix = _inputDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Manifest path escapes its input directory.");
            using var stream = File.OpenRead(fullPath);
            using var digest = SHA256.Create();
            var actual = BitConverter.ToString(digest.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (!HashMatches(actual, expectedHash)) throw new InvalidDataException("SHA-256 mismatch for " + relativePath);
            return fullPath;
        }

        private static bool HashMatches(string actual, string expected)
        {
            if (string.IsNullOrEmpty(expected) || expected.Length != 64) return false;
            foreach (var character in expected)
                if (!Uri.IsHexDigit(character)) return false;
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static string Sha256(byte[] bytes)
        {
            using var digest = SHA256.Create();
            return BitConverter.ToString(digest.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static string Argument(string name)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index < arguments.Length; ++index)
            {
                if (arguments[index] != name) continue;
                if (index + 1 == arguments.Length || arguments[index + 1].StartsWith("-", StringComparison.Ordinal))
                    throw new ArgumentException("Missing value for " + name);
                return arguments[index + 1];
            }
            return null;
        }

        private static void WriteReport() => File.WriteAllText(Path.Combine(_reportDirectory, "report.json"), JsonUtility.ToJson(_report, true));

        private static void Finish(bool pendingBackendWork = false)
        {
            if (_finished) return;
            _finished = true;
            EditorApplication.update -= Tick;
            _report.passed = string.IsNullOrEmpty(_report.error) && _report.completedCases == _report.expectedCases;
            foreach (var result in _report.cases) _report.passed &= result.passed;
            _report.cleanup = pendingBackendWork
                ? "Pending backend work was not reused or synchronously waited on. The isolated batch Editor process releases native resources on exit; no asynchronous clone task was abandoned."
                : "All owned workers, inputs, and completed CPU clones were disposed. Imported candidate assets remain only in the ignored Editor-only source directory.";
            try
            {
                if (!pendingBackendWork) _execution?.Dispose();
                WriteReport();
                Debug.Log("[Segmentation proof] " + (_report.passed ? "PASS" : "FAIL") + ": " + _report.completedCases + "/" + _report.expectedCases + " cases completed. See Logs/cli/segmentation-proof/report.json.");
            }
            catch (Exception error)
            {
                _report.passed = false;
                _report.error = (_report.error ?? "") + "\nFinal report/cleanup failed: " + error;
                Debug.LogException(error);
                try { WriteReport(); }
                catch (Exception writeError) { Debug.LogException(writeError); }
            }
            finally { EditorApplication.Exit(_report.passed ? 0 : 1); }
        }
    }
}
