# Vision lifetime regression tests

`VisionLifetimePlayTests` uses real Unity GameObject/component/parent toggles and scene unload with a controlled fake capture/inference backend. It verifies the production independent lifetime driver, not camera pixels or inference accuracy. The nine `VisionInferenceOperationTests` in the adjacent EditMode assembly also run as pure .NET logic tests.

Run the PlayMode group explicitly with the project's exact Unity Editor, for example:

```text
-batchmode -projectPath apps/quest -runTests -testPlatform PlayMode -testFilter CutOnce.Vision.PlayTests.VisionLifetimePlayTests -testResults /absolute/lifetime-play-results.xml
```

The normal `quest:check` only runs EditMode tests. `quest:sim` currently filters a different namespace. Neither is evidence that these PlayMode tests ran; no workflow was expanded by this change.

`CameraSourceTeardownGpuTests` is an **explicit**, isolated graphics-enabled stress probe for the borrowed-texture hazard: SDK 205 destroys its original texture in `OnDisable` even if a readback was enqueued. The probe invalidates delivery, destroys a matching test source, drains the outstanding request, and requires no publication or early slot reuse. Unity's public API does not guarantee ordinary texture retention after `Destroy` during readback. A passing Editor probe would establish only that observed Editor/backend behavior—not native PCA plugin ordering or Quest safety. Do not claim this test passed until it is actually run, and do not use it to replace the real headset lifecycle check.

The player pump is unparented and persists across scene unload. Editor play-stop transfers driver ownership to Editor updates rather than treating `OnApplicationQuit` as process exit. Assembly/domain reload remains an engine teardown boundary; these tests do not certify native GPU survival across reload.
