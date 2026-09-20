using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CutOnce.Vision.Editor
{
    /// <summary>
    /// Runs the bundled production detector on recorded RGB photos, without Play mode, camera
    /// simulation, lowered confidence thresholds, invented world positions, or substitute predictions.
    /// Graphics must remain enabled: even the CPU model uses TextureConverter for preprocessing.
    /// -executeMethod CutOnce.Vision.Editor.RecognitionProof.Run
    /// -recognitionPhoto "/absolute/table.jpg;/absolute/bottle.jpg"
    /// -recognitionExpected "dining table;bottle"
    /// A comma separates multiple required classes in one photo; a semicolon separates photos.
    /// Do not pass -quit: the Editor update loop advances the asynchronous production coroutine.
    /// </summary>
    public static class RecognitionProof
    {
        private const float DetectorConfidence = .35f;
        private const float ScannerConfidence = .4f;
        private const int Repeats = 3;
        private const float RequiredIoU = .4f;
        private const double InferenceDeadlineSeconds = 60;
        private const string Scope = "Recorded RGB recognition using the bundled production YoloDetector. " +
            "Bounding boxes in these diagnostic images are measured model output, not the in-headset highlight UI. " +
            "No simulated camera, depth, object position, or tracking is used. " +
            "This does not verify the live Quest camera, permission flow, headset frame rate, 3D alignment, or surface wrapping.";

        [Serializable]
        private sealed class Report
        {
            public string scope = Scope;
            public string unity, graphics;
            public string modelResource = RoomScannerBootstrap.ModelResource;
            public string labelsResource = RoomScannerBootstrap.LabelsResource;
            public string modelAssetPath, modelSha256;
            public bool cornerBoxes;
            public float nmsIoU;
            public string backend = "CPU";
            public float detectorConfidence = DetectorConfidence, scannerConfidence = ScannerConfidence;
            public float minimumGroundTruthIoU = RequiredIoU;
            public string groundTruthFile;
            public int repeats = Repeats;
            public bool passed;
            public string error;
            public string warmupPolicy = "One explicit blank production inference before measured photos. A cold-start frame discarded by the unchanged 8-second production timeout is reported, not hidden; all subsequent photo inferences must deliver valid output.";
            public InferenceResult coldStartWarmup;
            public bool recoveredAfterWarmup;
            public PreprocessingResult preprocessing;
            public List<PhotoResult> photos = new List<PhotoResult>();
            public InferenceResult blankNegativeControl;
            public bool blankNegativeControlPassed;
        }

        [Serializable]
        private sealed class PhotoResult
        {
            public string source, sha256, inputImage, annotatedImage;
            public int width, height;
            public string[] expectedClasses;
            public bool passed;
            public List<InferenceResult> inferences = new List<InferenceResult>();
        }

        [Serializable]
        private sealed class InferenceResult
        {
            public int iteration, rawCandidates;
            public float milliseconds;
            public float wallClockMilliseconds;
            public bool eventDelivered, timeoutDiscarded;
            public bool passed;
            public string error;
            public List<DetectionResult> detections = new List<DetectionResult>();
            public List<GroundTruthMatch> groundTruthMatches = new List<GroundTruthMatch>();
            public string rawClassSummaryScope = "Winning-class candidates before confidence filtering and NMS; the exported graph retains one ArgMax class per anchor, not every class logit.";
            public float rawClassThreshold;
            public List<RawClassResult> rawClasses = new List<RawClassResult>();
        }

        [Serializable]
        private sealed class PreprocessingResult
        {
            public string scope = "Actual TextureConverter defaults used by production, on a 2x2 exact-size sRGB test texture (not the model's resized photo path). Checks NCHW RGB channels, top-left tensor origin, and encoded gray 128/255 instead of linear-light 0.216.";
            public string textureFormat;
            public float[] expected, actual;
            public float tolerance = .01f;
            public bool passed;
        }

        [Serializable]
        private sealed class RawClassResult
        {
            public int classId;
            public string className;
            public float maxScore;
            public int aboveThreshold, candidateCount;
        }

        [Serializable]
        private sealed class GroundTruth
        {
            public List<GroundTruthFixture> fixtures = new List<GroundTruthFixture>();
        }

        [Serializable]
        private sealed class GroundTruthFixture
        {
            public string fileName;
            public List<ExpectedBox> expectations = new List<ExpectedBox>();
        }

        [Serializable]
        private sealed class ExpectedBox
        {
            public string className;
            public float x, y, width, height;
        }

        [Serializable]
        private sealed class GroundTruthMatch
        {
            public ExpectedBox expected;
            public float bestIoU;
            public float requiredIoU = RequiredIoU;
            public bool passed;
            public DetectionResult bestPrediction;
        }

        [Serializable]
        private sealed class DetectionResult
        {
            public int classId;
            public string name;
            public float confidence;
            public float x, y, width, height, inputWidth, inputHeight;
            public bool acceptedByScanner, validBox, rawBoxInsideFrame;
        }

        private static readonly Stack<IEnumerator> Routine = new Stack<IEnumerator>();
        private static readonly List<Object> Owned = new List<Object>();
        private static Report _report;
        private static string _directory;
        private static YoloDetector _detector;
        private static double _inferenceDeadline;
        private static bool _finished;
        private static GroundTruth _groundTruth;

        public static void Run()
        {
            _directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/cli/recognition-proof"));
            Directory.CreateDirectory(_directory);
            _report = new Report { unity = Application.unityVersion, graphics = SystemInfo.graphicsDeviceName };
            _finished = false;
            try
            {
                if (!Application.isBatchMode)
                    throw new InvalidOperationException("RecognitionProof requires a separate batchmode Editor; it creates an empty temporary scene.");
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    throw new InvalidOperationException("Recognition proof needs graphics for production TextureConverter. Remove -nographics.");

                var photos = RequiredArgument("-recognitionPhoto").Split(';');
                var expectations = RequiredArgument("-recognitionExpected").Split(';');
                if (photos.Length != expectations.Length)
                    throw new ArgumentException("Each semicolon-separated recognition photo needs its own semicolon-separated expected-class group.");
                var groundTruthPath = OptionalArgument("-recognitionGroundTruth");
                _groundTruth = null;
                if (!string.IsNullOrEmpty(groundTruthPath))
                {
                    _report.groundTruthFile = Path.GetFullPath(groundTruthPath);
                    _groundTruth = JsonUtility.FromJson<GroundTruth>(File.ReadAllText(_report.groundTruthFile));
                    if (_groundTruth == null || _groundTruth.fixtures == null || _groundTruth.fixtures.Count == 0)
                        throw new ArgumentException("Ground-truth JSON must contain a nonempty fixtures list.");
                }

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var root = new GameObject("Recorded RGB recognition proof (no camera or fake depth)");
                Owned.Add(root);
                _detector = root.AddComponent<YoloDetector>();
                _detector.modelAsset = Resources.Load<ModelAsset>(RoomScannerBootstrap.ModelResource);
                _detector.labelsAsset = Resources.Load<TextAsset>(RoomScannerBootstrap.LabelsResource);
                _detector.backend = BackendType.CPU;
                _detector.collectRawClassSummaries = true; // Diagnostics only; no thresholds or model output are changed.
                // Assert the actual defaults instead of changing the model's operating point to make a test pass.
                if (Mathf.Abs(_detector.scoreThreshold - DetectorConfidence) > .0001f)
                    throw new InvalidOperationException("Production detector confidence changed; update this proof explicitly before comparing results.");
                _report.modelAssetPath = AssetDatabase.GetAssetPath(_detector.modelAsset);
                if (!string.IsNullOrEmpty(_report.modelAssetPath))
                    _report.modelSha256 = Sha256(File.ReadAllBytes(_report.modelAssetPath));
                _report.cornerBoxes = _detector.cornerBoxes;
                _report.nmsIoU = _detector.iouThreshold;
                _detector.LoadModel();
                if (!_detector.ModelLoaded) throw new InvalidOperationException(_detector.LastError);

                Routine.Clear();
                Routine.Push(Body(photos, expectations));
                EditorApplication.update += Tick;
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch (Exception error) { Finish(error); }
        }

        private static IEnumerator Body(string[] paths, string[] expectationGroups)
        {
            yield return CheckPreprocessing();
            var blank = new Texture2D(640, 640, TextureFormat.RGBA32, false);
            Owned.Add(blank);
            var gray = new Color32[640 * 640];
            for (var pixel = 0; pixel < gray.Length; pixel++) gray[pixel] = new Color32(128, 128, 128, 255);
            blank.SetPixels32(gray);
            blank.Apply(false, false);
            yield return Infer(blank, 0, value => _report.coldStartWarmup = value);
            Debug.Log($"[Recognition proof] Explicit cold-start blank warmup: {_report.coldStartWarmup.wallClockMilliseconds:0.0} ms; " +
                $"event={_report.coldStartWarmup.eventDelivered}, timeoutDiscarded={_report.coldStartWarmup.timeoutDiscarded}, error={_report.coldStartWarmup.error}");

            for (var photoIndex = 0; photoIndex < paths.Length; photoIndex++)
            {
                var path = Path.GetFullPath(paths[photoIndex].Trim());
                var expected = ExpectedClasses(expectationGroups[photoIndex]);
                GroundTruthFixture known = null;
                if (_groundTruth != null)
                {
                    known = _groundTruth.fixtures.Find(fixture => fixture.fileName == Path.GetFileName(path));
                    if (known == null || known.expectations == null || known.expectations.Count == 0)
                        throw new ArgumentException("No ground-truth boxes supplied for photo " + Path.GetFileName(path));
                    foreach (var box in known.expectations)
                        if (string.IsNullOrWhiteSpace(box.className) || !Finite(box.x) || !Finite(box.y) ||
                            !Finite(box.width) || !Finite(box.height) || box.width <= 0 || box.height <= 0)
                            throw new ArgumentException("Invalid ground-truth box for " + Path.GetFileName(path));
                }
                var bytes = File.ReadAllBytes(path);
                var photo = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                Owned.Add(photo);
                if (!photo.LoadImage(bytes)) throw new InvalidOperationException("Cannot decode recognition photo: " + path);
                var prefix = "photo-" + (photoIndex + 1).ToString("00", CultureInfo.InvariantCulture);
                var result = new PhotoResult
                {
                    source = path, sha256 = Sha256(bytes), width = photo.width, height = photo.height,
                    expectedClasses = expected, inputImage = prefix + "-input.png", annotatedImage = prefix + "-annotated.png",
                };
                _report.photos.Add(result);
                File.WriteAllBytes(Path.Combine(_directory, result.inputImage), photo.EncodeToPNG());

                string firstClassSignature = null;
                for (var iteration = 1; iteration <= Repeats; iteration++)
                {
                    InferenceResult inference = null;
                    yield return Infer(photo, iteration, value => inference = value);
                    result.inferences.Add(inference);
                    foreach (var expectedName in expected)
                        if (!inference.detections.Exists(d => d.acceptedByScanner && d.name == expectedName))
                            Reject(inference, $"Expected '{expectedName}' was not detected at the unchanged scanner confidence {ScannerConfidence:0.00}.");
                    if (known != null) MatchGroundTruth(inference, known, photo.width, photo.height);
                    var signature = AcceptedClassSignature(inference);
                    if (firstClassSignature == null) firstClassSignature = signature;
                    else if (signature != firstClassSignature)
                        Reject(inference, "Repeated inference on identical pixels changed the accepted class set.");
                    if (iteration == 1) WriteAnnotated(photo, inference, Path.Combine(_directory, result.annotatedImage));
                    Debug.Log($"[Recognition proof] {prefix}, pass {iteration}: {signature}; {inference.milliseconds:0.0} ms; {(inference.passed ? "PASS" : "FAIL")}");
                }
                result.passed = result.inferences.TrueForAll(inference => inference.passed);
            }

            // A real negative control, not a substituted detector response. It uses exactly the same
            // preprocessing/model/decode path and reports any actual output, including false positives.
            yield return Infer(blank, 1, value => _report.blankNegativeControl = value);
            foreach (var detection in _report.blankNegativeControl.detections)
                if (detection.acceptedByScanner)
                    Reject(_report.blankNegativeControl, "Blank negative control falsely detected '" + detection.name + "'.");
            _report.blankNegativeControlPassed = _report.blankNegativeControl.passed;
            WriteAnnotated(blank, _report.blankNegativeControl, Path.Combine(_directory, "blank-negative-control.png"));
            _report.recoveredAfterWarmup = _report.photos.TrueForAll(photo => photo.inferences.TrueForAll(inference => inference.eventDelivered)) &&
                _report.blankNegativeControl.eventDelivered && !_detector.InferenceRunning;
            var warmupAcceptable = _report.coldStartWarmup.passed || _report.coldStartWarmup.timeoutDiscarded;
            _report.passed = _report.preprocessing.passed && warmupAcceptable && _report.recoveredAfterWarmup &&
                _report.photos.TrueForAll(photo => photo.passed) && _report.blankNegativeControlPassed;
            if (!_report.passed) _report.error = "At least one actual model-output assertion failed; inspect photo inferences and the negative control.";
        }

        private static IEnumerator CheckPreprocessing()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Owned.Add(texture);
            texture.SetPixels32(new[]
            {
                new Color32(0, 0, 255, 255), new Color32(128, 128, 128, 255), // Unity's first row is bottom-left.
                new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255),
            });
            texture.Apply(false, false);
            var gray = 128f / 255f;
            _report.preprocessing = new PreprocessingResult
            {
                textureFormat = texture.graphicsFormat.ToString(),
                expected = new[] { 1f, 0f, 0f, gray, 0f, 1f, 0f, gray, 0f, 0f, 1f, gray },
                actual = new float[12],
            };
            using (var input = new Tensor<float>(new TensorShape(1, 3, 2, 2)))
            {
                TextureConverter.ToTensor(texture, input, new TextureTransform().SetDimensions(2, 2, 3));
                var readback = input.ReadbackAndCloneAsync().GetAwaiter();
                var deadline = EditorApplication.timeSinceStartup + InferenceDeadlineSeconds;
                while (!readback.IsCompleted)
                {
                    if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Known-pixel preprocessing readback did not complete.");
                    yield return null;
                }
                using (var pixels = readback.GetResult())
                {
                    _report.preprocessing.passed = true;
                    for (var index = 0; index < _report.preprocessing.actual.Length; index++)
                    {
                        var value = pixels[index];
                        _report.preprocessing.actual[index] = value;
                        if (!Finite(value) || Mathf.Abs(value - _report.preprocessing.expected[index]) > _report.preprocessing.tolerance)
                            _report.preprocessing.passed = false;
                    }
                }
            }
            Debug.Log("[Recognition proof] Actual RGB/gray preprocessing check: " + (_report.preprocessing.passed ? "PASS" : "FAIL"));
        }

        private static IEnumerator Infer(Texture photo, int iteration, Action<InferenceResult> done)
        {
            var result = new InferenceResult { iteration = iteration, passed = true };
            var startedAt = EditorApplication.timeSinceStartup;
            var delivered = false;
            void Capture(List<DetectedObject> detections, Pose pose, Vector2 inputSize)
            {
                delivered = true;
                foreach (var detection in detections)
                {
                    var box = detection.boundingBox;
                    var finite = Finite(box.x) && Finite(box.y) && Finite(box.width) && Finite(box.height) &&
                        Finite(detection.confidence) && Finite(inputSize.x) && Finite(inputSize.y);
                    var valid = finite && box.width > 0 && box.height > 0 && inputSize.x > 0 && inputSize.y > 0 &&
                        box.xMax > 0 && box.yMax > 0 && box.xMin < inputSize.x && box.yMin < inputSize.y;
                    var item = new DetectionResult
                    {
                        classId = detection.classId, name = detection.className, confidence = detection.confidence,
                        x = box.x, y = box.y, width = box.width, height = box.height,
                        inputWidth = inputSize.x, inputHeight = inputSize.y,
                        acceptedByScanner = detection.confidence >= ScannerConfidence,
                        validBox = valid,
                        // An object cut by the photo edge may legitimately extend beyond the image.
                        // Preserve its raw coordinates in JSON; only diagnostic drawing is clipped.
                        rawBoxInsideFrame = valid && box.xMin >= 0 && box.yMin >= 0 && box.xMax <= inputSize.x && box.yMax <= inputSize.y,
                    };
                    result.detections.Add(item);
                    if (!valid || !Finite(detection.confidence) || detection.confidence < DetectorConfidence || detection.confidence > 1f)
                        Reject(result, "Invalid model confidence or non-finite/non-intersecting box for '" + detection.className + "'.");
                }
            }

            _detector.OnDetections += Capture;
            _inferenceDeadline = EditorApplication.timeSinceStartup + InferenceDeadlineSeconds;
            try
            {
                // Identity pose is merely an unused event argument: no locator or tracker exists in this test.
                yield return _detector.DetectOnce(photo, new Pose(Vector3.zero, Quaternion.identity));
                result.rawCandidates = _detector.LastRawDetections;
                result.milliseconds = _detector.LastInferenceMs;
                result.wallClockMilliseconds = (float)((EditorApplication.timeSinceStartup - startedAt) * 1000);
                result.eventDelivered = delivered;
                result.timeoutDiscarded = !delivered && !_detector.InferenceRunning &&
                    _detector.LastError.StartsWith("inference readback stalled", StringComparison.Ordinal);
                if (delivered)
                {
                    result.rawClassThreshold = _detector.LastRawClassThreshold;
                    foreach (var summary in _detector.LastRawClassSummaries)
                        result.rawClasses.Add(new RawClassResult
                        {
                            classId = summary.classId, className = summary.className, maxScore = summary.maxScore,
                            aboveThreshold = summary.aboveThreshold, candidateCount = summary.candidateCount,
                        });
                }
                if (!delivered) Reject(result, "Production detector did not emit a detection event: " + _detector.LastError);
                if (!string.IsNullOrEmpty(_detector.LastError)) Reject(result, _detector.LastError);
            }
            finally
            {
                _inferenceDeadline = 0;
                _detector.OnDetections -= Capture;
            }
            done(result);
        }

        private static void Tick()
        {
            if (_finished) return;
            try
            {
                if (_inferenceDeadline > 0 && EditorApplication.timeSinceStartup > _inferenceDeadline)
                    throw new TimeoutException("Production inference did not finish within " + InferenceDeadlineSeconds + " seconds.");
                while (Routine.Count > 0)
                {
                    var current = Routine.Peek();
                    if (!current.MoveNext())
                    {
                        Routine.Pop();
                        (current as IDisposable)?.Dispose();
                        continue;
                    }
                    if (current.Current is IEnumerator child) { Routine.Push(child); continue; }
                    if (current.Current != null)
                        throw new InvalidOperationException("Unhandled production coroutine yield: " + current.Current.GetType().FullName);
                    EditorApplication.QueuePlayerLoopUpdate();
                    return; // A null yield must really advance an Editor frame, never busy-poll the readback.
                }
                Finish(null);
            }
            catch (Exception error) { Finish(error); }
        }

        private static void Finish(Exception error)
        {
            if (_finished) return;
            _finished = true;
            EditorApplication.update -= Tick;
            if (error != null)
            {
                _report.passed = false;
                _report.error = error.ToString();
                Debug.LogException(error);
            }
            while (Routine.Count > 0) (Routine.Pop() as IDisposable)?.Dispose();
            foreach (var resource in Owned) if (resource != null) Object.DestroyImmediate(resource);
            Owned.Clear();
            File.WriteAllText(Path.Combine(_directory, "report.json"), JsonUtility.ToJson(_report, true));
            Debug.Log($"Recorded RGB recognition proof: {(_report.passed ? "PASS" : "FAIL")}. Output: {_directory}. NOT live-headset evidence.");
            if (Application.isBatchMode) EditorApplication.Exit(_report.passed ? 0 : 1);
        }

        private static string RequiredArgument(string flag)
        {
            var value = OptionalArgument(flag);
            if (value != null) return value;
            throw new ArgumentException("Missing " + flag + ". Supply a real photo and explicit expected classes; no default can silently pass.");
        }

        private static string OptionalArgument(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (var index = 0; index < args.Length - 1; index++)
                if (args[index] == flag && !string.IsNullOrWhiteSpace(args[index + 1])) return args[index + 1];
            return null;
        }

        private static string[] ExpectedClasses(string group)
        {
            var names = group.Split(',');
            for (var index = 0; index < names.Length; index++)
            {
                if (string.IsNullOrWhiteSpace(names[index])) throw new ArgumentException("Expected class names cannot be empty.");
                names[index] = YoloDetector.NormalizeClassName(names[index]);
            }
            return names;
        }

        private static string Sha256(byte[] bytes)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static void Reject(InferenceResult result, string message)
        {
            result.passed = false;
            result.error = string.IsNullOrEmpty(result.error) ? message : result.error + " " + message;
        }

        private static string AcceptedClassSignature(InferenceResult result)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var detection in result.detections) if (detection.acceptedByScanner) names.Add(detection.name);
            return string.Join(", ", names);
        }

        private static void MatchGroundTruth(InferenceResult result, GroundTruthFixture known, int photoWidth, int photoHeight)
        {
            foreach (var expected in known.expectations)
            {
                var match = new GroundTruthMatch { expected = expected };
                var expectedName = YoloDetector.NormalizeClassName(expected.className);
                var expectedRect = new Rect(expected.x, expected.y, expected.width, expected.height);
                foreach (var prediction in result.detections)
                {
                    if (!prediction.acceptedByScanner || !prediction.validBox || prediction.name != expectedName) continue;
                    var predictedRect = new Rect(prediction.x / prediction.inputWidth * photoWidth,
                        prediction.y / prediction.inputHeight * photoHeight,
                        prediction.width / prediction.inputWidth * photoWidth,
                        prediction.height / prediction.inputHeight * photoHeight);
                    var intersectionWidth = Mathf.Max(0, Mathf.Min(expectedRect.xMax, predictedRect.xMax) - Mathf.Max(expectedRect.xMin, predictedRect.xMin));
                    var intersectionHeight = Mathf.Max(0, Mathf.Min(expectedRect.yMax, predictedRect.yMax) - Mathf.Max(expectedRect.yMin, predictedRect.yMin));
                    var intersection = intersectionWidth * intersectionHeight;
                    var union = expected.width * expected.height + predictedRect.width * predictedRect.height - intersection;
                    var iou = union > 0 ? intersection / union : 0;
                    if (iou > match.bestIoU) { match.bestIoU = iou; match.bestPrediction = prediction; }
                }
                match.passed = match.bestIoU >= RequiredIoU;
                result.groundTruthMatches.Add(match);
                if (!match.passed)
                    Reject(result, $"Ground-truth '{expectedName}' overlap {match.bestIoU:0.000} is below required IoU {RequiredIoU:0.00}.");
            }
        }

        // This is a diagnostic raster annotation of real inference coordinates. No learned output is
        // edited or generated. A tiny fixed bitmap alphabet avoids font/render-pipeline dependencies.
        private static void WriteAnnotated(Texture2D photo, InferenceResult result, string path)
        {
            const int banner = 54;
            var width = photo.width;
            var height = photo.height + banner;
            var pixels = new Color32[width * height];
            var source = photo.GetPixels32();
            Array.Copy(source, pixels, source.Length);
            var dark = new Color32(13, 24, 37, 255);
            var cyan = new Color32(35, 201, 255, 255);
            var white = new Color32(237, 245, 249, 255);
            Fill(pixels, width, height, 0, photo.height, width, banner, dark);
            Text(pixels, width, height, 8, height - 18, "RECORDED RGB - PRODUCTION MODEL", white, 1);
            Text(pixels, width, height, 8, height - 34, "NOT LIVE / NOT THE HEADSET HIGHLIGHT UI", white, 1);
            Text(pixels, width, height, 8, height - 48, "BLUE BOXES = MODEL DIAGNOSTICS AT 40%+", cyan, 1);
            foreach (var detection in result.detections)
            {
                if (!detection.acceptedByScanner || !detection.validBox) continue;
                var left = Mathf.Clamp(Mathf.RoundToInt(detection.x / detection.inputWidth * width), 0, width - 1);
                var right = Mathf.Clamp(Mathf.RoundToInt((detection.x + detection.width) / detection.inputWidth * width), 0, width - 1);
                var top = Mathf.Clamp(Mathf.RoundToInt((1f - detection.y / detection.inputHeight) * photo.height), 0, photo.height - 1);
                var bottom = Mathf.Clamp(Mathf.RoundToInt((1f - (detection.y + detection.height) / detection.inputHeight) * photo.height), 0, photo.height - 1);
                Fill(pixels, width, height, left, bottom, right - left + 1, 3, cyan);
                Fill(pixels, width, height, left, top - 2, right - left + 1, 3, cyan);
                Fill(pixels, width, height, left, bottom, 3, top - bottom + 1, cyan);
                Fill(pixels, width, height, right - 2, bottom, 3, top - bottom + 1, cyan);
                var label = detection.name.ToUpperInvariant() + " " + (detection.confidence * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
                const int scale = 2;
                var labelWidth = Mathf.Min(width, label.Length * 6 * scale + 8);
                var labelX = Mathf.Clamp(left, 0, width - labelWidth);
                var labelY = Mathf.Clamp(top - 20, 0, photo.height - 20);
                Fill(pixels, width, height, labelX, labelY, labelWidth, 20, dark);
                Text(pixels, width, height, labelX + 4, labelY + 3, label, white, scale);
            }
            var annotated = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                annotated.SetPixels32(pixels);
                annotated.Apply(false, false);
                File.WriteAllBytes(path, annotated.EncodeToPNG());
            }
            finally { Object.DestroyImmediate(annotated); }
        }

        private static void Fill(Color32[] pixels, int width, int height, int x, int y, int rectangleWidth, int rectangleHeight, Color32 color)
        {
            for (var row = Mathf.Max(0, y); row < Mathf.Min(height, y + rectangleHeight); row++)
                for (var column = Mathf.Max(0, x); column < Mathf.Min(width, x + rectangleWidth); column++)
                    pixels[row * width + column] = color;
        }

        private static void Text(Color32[] pixels, int width, int height, int x, int y, string text, Color32 color, int scale)
        {
            foreach (var character in text)
            {
                if (Glyphs.TryGetValue(character, out var glyph))
                    for (var row = 0; row < 7; row++)
                        for (var column = 0; column < 5; column++)
                            if (glyph[row * 5 + column] == '1')
                                Fill(pixels, width, height, x + column * scale, y + (6 - row) * scale, scale, scale, color);
                x += 6 * scale;
            }
        }

        private static readonly Dictionary<char, string> Glyphs = new Dictionary<char, string>
        {
            ['A'] = "01110100011000111111100011000110001", ['B'] = "11110100011000111110100011000111110",
            ['C'] = "01111100001000010000100001000001111", ['D'] = "11110100011000110001100011000111110",
            ['E'] = "11111100001000011110100001000011111", ['F'] = "11111100001000011110100001000010000",
            ['G'] = "01111100001000010111100011000101111", ['H'] = "10001100011000111111100011000110001",
            ['I'] = "11111001000010000100001000010011111", ['J'] = "00111000100001000010000101001001100",
            ['K'] = "10001100101010011000101001001010001", ['L'] = "10000100001000010000100001000011111",
            ['M'] = "10001110111010110101100011000110001", ['N'] = "10001110011010110011100011000110001",
            ['O'] = "01110100011000110001100011000101110", ['P'] = "11110100011000111110100001000010000",
            ['Q'] = "01110100011000110001101011001001101", ['R'] = "11110100011000111110101001001010001",
            ['S'] = "01111100001000001110000010000111110", ['T'] = "11111001000010000100001000010000100",
            ['U'] = "10001100011000110001100011000101110", ['V'] = "10001100011000110001100010101000100",
            ['W'] = "10001100011000110101101011101110001", ['X'] = "10001100010101000100010101000110001",
            ['Y'] = "10001100010101000100001000010000100", ['Z'] = "11111000010001000100010001000011111",
            ['0'] = "01110100011001110101110011000101110", ['1'] = "00100011000010000100001000010001110",
            ['2'] = "01110100010000100010001000100011111", ['3'] = "11110000010000101110000010000111110",
            ['4'] = "00010001100101010010111110001000010", ['5'] = "11111100001000011110000010000111110",
            ['6'] = "01110100001000011110100011000101110", ['7'] = "11111000010001000100010000100001000",
            ['8'] = "01110100011000101110100011000101110", ['9'] = "01110100011000101111000010000101110",
            ['%'] = "11001110100001000100010000101110011", ['-'] = "00000000000000011111000000000000000",
            ['/'] = "00001000100001000100010000100010000", ['+'] = "00000001000010011111001000010000000",
            ['='] = "00000000001111100000111110000000000", ['.'] = "00000000000000000000000000011000110",
        };
    }
}
