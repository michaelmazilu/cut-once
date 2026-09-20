# Live object recognition acceptance

The target is the complete user checklist below, not merely a successful APK or
a rendered table fixture. Recorded-photo and synthetic-depth checks are useful
regressions, but cannot establish live camera behavior, stereo registration,
identity stability or sustained Quest performance.

Latest committed-model build: `a188e82`,
[Mac run 35499902362](https://github.com/michaelmazilu/kitbash/actions/runs/35499902362).
All 271 Unity tests, both recorded recognition photos (three repeats each),
blank negative control, preprocessing checks and 16 synthetic surface checks
passed. Android APK built successfully. Readiness reported no errors and one
existing simulator-update warning. Verification ladder: steps 1–3 plus explicit
recorded-image/GPU fixtures; not a live simulator session or step 5 headset proof.
The last Mac USB probe (`35499599170`) found no connected devices.

Pending follow-up: `2f97de5` adds direct asynchronous camera snapshots and
25 lifecycle regressions, plus sRGB/UNorm GPU snapshot checks. Its normal build
[35502199954](https://github.com/michaelmazilu/kitbash/actions/runs/35502199954)
is queued. The preceding candidate run `35501459393` lost communication with the
Mac runner during setup; it did not complete Unity tests. These new changes are
not included in the last verified APK above, and their Mac gates remain pending.

The subsequent scanner-lifetime fix keeps the automatic loop and submitted
inference alive through scanner/parent disable, draining before worker reuse or
retirement. Nine pure scheduler tests pass locally in .NET. Unity compilation and
recorded-photo execution are pending; seven new explicit PlayMode host tests and
the opt-in native source-teardown stress probe have not run. The existing normal
build does not automatically run that new PlayMode namespace. Neither authored
tests nor code review establish successful native camera teardown on Quest.

| Requirement | Existing implementation / evidence | Remaining acceptance |
| --- | --- | --- |
| Identifies the object | Actual YOLO inference, 80 COCO class names; shipping FP32 weights pass the unchanged bottle/table photo tests | Test representative objects through the Quest camera. No claim to recognize every possible category. |
| Knows its room position | Calibrated captured-camera rays and measured environment depth; no guessed fallback | Measure placement on real bottle/table surfaces, at image edges and off-axis. |
| Highlights the object blue | Production stereo surface shader, synthetic-depth GPU checks | Verify real passthrough alignment and both eyes. |
| Shows its name beside it | Recognizer class name on a tracked world-space label | Check readability and label overlap while moving. |
| Highlight stays attached | Captured camera pose, world-space tracks, filtered depth; rejected jumps cannot refresh old geometry | Head-motion and moving-object tests, stale-frame rejection and latency measurement. |
| Scans continuously, automatically | Scanner bootstrap and continuous camera/inference loop; no scan button required | Cold launch, permission grant/denial, pause and sleep/resume on hardware. |
| Finds new objects while looking around | Repeated inference of fresh camera frames | Pan into a previously unseen part of the room. |
| Does not duplicate objects | Class-aware NMS, one-to-one same-class spatial association and regression tests | Nearby/crossing objects, cross-class conflicting predictions and turn-away/reacquisition remain to verify. |
| RoomSense stays invisible | Scanner-owned visibility policy, including later component startup; tests | Confirm in the final live scene while preserving invisible room geometry. |
| Only detected objects receive blue | Empty/invalid depth and surfaces outside the estimated object volume are excluded | **Not yet guaranteed:** no semantic pixel masks; unrelated clutter inside an object's volume can also be painted. Requires instance masks and calibrated reprojection, not just another shader style. |
| Smooth, real-time Quest operation | Inference throttling/readback and bounded visible objects; existing BudgetProbe | Quest 3 sustained 72fps (13.9ms), actual scan cadence, camera-to-highlight latency, dropped frames, memory and thermals. Mac timing cannot satisfy this. |

## Device gate

`Quest Mac` is manually dispatched on a reviewed branch. `mode=device-status`
performs only redacted, read-only USB diagnostics; it neither installs nor starts
the app. The user must connect the Quest to the Mac and authorize USB debugging
inside the headset before hardware tests are possible. Authorization alone is
not a passing camera or recognition test.

For visual proof, distinguish actual Quest recordings from recorded-image
detector annotations and synthetic-depth rendering. Never substitute generated
mockups for screenshots of the running build.

## Remaining localization risks

- The SDK 205 `PassthroughCameraAccess.GetTexture` documentation warns that a
  blocking `Graphics.Blit` can sample previous-frame pixels because the camera
  texture updates on the render thread. Live acquisition now uses its documented
  direct asynchronous readback route, then holds an owned RGBA snapshot with the
  enqueue-time pose/calibration/timestamp. Letterboxing samples that held copy,
  not the live camera texture. Same-frame metadata checks and restart generations
  protect this handoff, but cannot prove native sensor/render timestamp pairing.
  Physical head-motion correspondence and the extra transfer cost need measurement;
  the captured-camera projection helper assumes a correctly paired capture.
- Captured RGB rays currently query depth when inference completes. An object
  moving away during that interval can expose a valid wall hit; a short freshness
  limit alone cannot synchronize RGB and depth.
- The box-grid percentile can select background through an open table's legs.
  Valid depth is not necessarily depth belonging to the recognized object.
- Ordinary size caps can clip large furniture. Surface-based track centers can
  move with viewpoint, so walking around a large table can create another track.
  Neither issue is established as solved by two-dimensional photo tests.

These need explicit geometry/motion regressions and measured device sequences.
Semantic masks are necessary for the clutter requirement but do not, alone,
solve moving-object synchronization or persistent identity.

## Experimental mask evidence, not a shipping feature

A separate Linux experiment keeps the already-tested YOLO recognizer as the only
source of labels and detections, then associates RTMDet-tiny 320 mask proposals
by matching winning class and at least .5 box IoU. It does not change standalone
RTMDet's failed recognition gate or promote its weights into the application.
All ten accepted detections in the two existing photos received a proposal; only
the four fixed bottle/table targets have ground-truth quality checks.

Those four targets pass the existing mask-IoU >= .5 threshold: bottles .907/.932,
tables .734/.777. Inspection of the exact predicted pixels exposes an important
remaining failure: table masks cover foreground bottles, food, boards/glass and
parts of a person. Passing aggregate IoU is therefore **not proof of exclusive
visible-object coverage**. No masks were retouched to make the preview cleaner.

This experiment adds a second model and measured 188–361ms of masking work per
photo on a shared Linux CPU, excluding YOLO and XR rendering. It has not run
end-to-end in Unity or on Quest, and is not included in the latest APK. Broader
scenes, overlap ownership, native capture correspondence, memory and measured
headset latency remain promotion gates. The recorded-photo preview is evidence
of the experimental masks, not a screenshot of the final application.
