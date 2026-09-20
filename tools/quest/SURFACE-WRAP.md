# Surface wrap verification

Normal object recognition draws blue only on the live environment depth surface
inside an estimated object volume. The volume is invisible. A world-space grid
and depth-discontinuity rim follow that surface; gaps between table legs remain
unpainted when the background is outside the volume. Per-eye reconstruction uses
Meta's current reprojection matrices and texture. It never draws an enclosing
cube as a normal fallback.

Without depth, labels say `DEPTH UNAVAILABLE`. A detection fades between 0.5 and
0.75 seconds old, then its surface disappears and the label says `REACQUIRING`.
Holding both thumbsticks for one second explicitly enables diagnostic boxes.

New world positions require measured environment-depth rays. Missing calibration,
unsupported depth, and off-image detections do not become guessed two-metre
positions or points on room planes. Rejected depth jumps do not refresh an old
track's timestamp or count toward confirmation; it ages out normally.

Live batches carry a monotonic application-acquisition timestamp. Results more
than 0.5 seconds old, or from before a pause/resume transition, are discarded
before they can update any track. Live fading/pruning uses that acquisition age,
not result arrival. This timestamp is a lower bound on actual sensor age; it
does not claim RGB/depth synchronization. Recorded-photo inference deliberately
does not claim live freshness. Scan rate reports actual publication intervals,
including the rate limit and camera waits, separately from processing latency.

RoomSense's room-wide glow and guessed gaze labels are suppressed while Vision
owns recognition. Its MRUK room geometry, anchors and colliders remain active.
This policy applies to both existing and later-created RoomSense components.

The bundled model has 80 COCO categories. Its legacy `diningtable` label is shown
as `dining table`; a water bottle is identified as `bottle`, not a particular
brand or its contents. No class outside that vocabulary is promised. Detection
batches are NMS-filtered within each class, then associated one-to-one with nearby same-class
tracks so two nearby bottles do not update the same identity. This does not
guarantee identity across class changes, long absences, or crossing objects.
Different classes can overlap: an object on a table must not suppress the table
just because their image boxes overlap. Conflicting class predictions for one
physical object can still coexist; NMS alone is not semantic identity tracking.

## What the evidence means

`pnpm quest:surface-proof` runs Unity with graphics, renders the production shader
against a synthetic depth texture from known geometry, and saves PNGs and a JSON
report in `apps/quest/Logs/cli/surface-proof`. This tests the surface renderer,
not object detection or the headset's sensors. The Mac workflow runs it before
building the APK and includes its output in the diagnostics artifact.

The fixture renders physical colour to a texture, then draws it as a depth-free
backdrop before highlights: real passthrough is not an opaque virtual table. A separate
virtual card, excluded from environment depth, must still occlude the highlight.
Assertions also require clear gaps/background and unchanged pixels without
valid depth. Screenshots upload before the APK build to allow early review.

The detector remains the existing COCO-class YOLO model. It returns 2D boxes,
not per-object pixel masks. The locator estimates the object's size and position
from multiple depth rays. Consequently, a second surface inside that volume can
also receive colour. Low-resolution/noisy depth, thin structures, fast motion,
and hidden surfaces remain limitations. This is not a full 3D mesh or true
instance segmentation, and unseen surfaces are not invented. A segmentation
model plus captured RGB pose/intrinsics would be needed for semantic separation
of overlapping/same-depth objects.

## On-headset acceptance

Before headset testing, `pnpm quest:recognition-proof` runs the production model
and preprocessing on two pinned COCO validation photos with real bottles and
tables. It repeats each inference three times at the unchanged 0.35 detector /
0.40 scanner thresholds, checks the predicted names and IoU >= 0.40 against
the dataset's annotated boxes, and checks a blank negative control. The rendered
diagnostic boxes show actual detector coordinates; they are not the headset UI.
Results and annotated photos are in `Logs/cli/recognition-proof`.

The report separately records the first blank-image cold start, including any
frame discarded by the production timeout, and requires scanning to recover
for all measured photos. A numeric GPU preprocessing check tests RGB ordering,
image orientation and encoded mid-gray values. Optional pre-NMS class scores
explain low-confidence misses without changing the acceptance thresholds;
this extra diagnostic pass is disabled in the normal headset pipeline.

The stretch-input baseline at `bf24b4c` is deliberately retained in Actions run
`35497651075`: RGB/orientation checks passed and repeated inference recovered,
but **both recorded photos failed recognition acceptance**. Before thresholding
or NMS, the best bottle/table scores were 0.098/0.087 on photo 160012 and
0.342/0.302 on photo 146489, below the unchanged 0.40 scanner cutoff. The first
photo still found a person and pizza; the second found a wine glass and pizza.
Do not describe that run as successful table/bottle recognition. It is the
same-input baseline for subsequent preprocessing changes, not a reason to
remove the failing photos or lower their required confidence/overlap.

The bundled Meta model already converts its output to `(x1,y1,x2,y2)` corners;
decoding it again as centre/size misplaces the depth rays. `cornerBoxes` therefore
defaults to true, matching the bundled asset and Meta's converter. A recorded
photo proof validates this path but cannot validate physical camera access,
permissions, stereo alignment or Quest performance while the device is disconnected.

Camera images now fit inside the model's fixed square without stretching:
`YoloLetterbox` preserves the aspect ratio and pads with encoded RGB 114.
Decoded boxes are mapped back to original camera-image pixels before any depth
rays or labels are placed. The GPU proof checks landscape/portrait inputs and
the same sRGB RenderTexture format used by the camera, including padding,
orientation, colours and inverse coordinates. A camera-format test buffer is
still synthetic input, not evidence that the physical camera works.

Measured letterbox result: commit `91eb7e2`, Actions run `35498191832`.
All 228 EditMode tests, the numeric RGB check, all 18 letterbox colour/padding
probes, all six inverse-coordinate checks, and the blank negative control passed.
Photo 146489 now finds the bottle at 0.413 confidence / 0.943 IoU and the dining
table at 0.419 confidence / 0.824 IoU, identically across three runs. Photo 160012
still misses both at the scanner threshold (best raw scores: bottle 0.293,
table 0.185). **Overall recognition acceptance remains failing: one of two
photos passes.** The workflow did not build a new APK from this revision.
Do not substitute these Mac timings or recorded-image results for a live Quest
camera, stereo registration or frame-rate test; the headset was disconnected.

An explicit `model_precision=float32-candidate` Mac workflow run compares the
same upstream model without weight quantization. `pnpm quest:convert-model`
downloads a SHA-256-pinned editor-only ONNX and reproduces Meta's three-output
graph in Unity 2.6.1, preserving the runtime asset GUID. The isolated run uploads
the candidate, original backup, license/provenance and conversion report. Normal
builds keep the bundled model unchanged. Conversion is not recognition proof:
the unchanged photo acceptance checks must still pass before an APK is built.

The FP32 comparison in Actions run `35499278669` passed both original photos,
all three repeats, the unchanged confidence/IoU requirements, blank negative
control and preprocessing checks. Photo 160012: bottle 0.938 confidence / 0.933
IoU, table 0.423 / 0.909. Photo 146489: bottle 0.970 / 0.961, table 0.616 / 0.809.
The production shader also passed all 16 synthetic-depth checks. This isolates
the observed accuracy loss to the quantized export/execution path in this
comparison, not to class-name mapping or a need to lower confidence thresholds.

The verified candidate is now bundled as `Resources/yolov9sentis.sentis`, SHA-256
`d827fbd4be185f6af53f5a6c07e51ae05b024714284f8019e42adad6e17b4224`
(8,311,864 bytes), preserving the original `.meta`. It is generated from the
same upstream FP32 model with the same corner/class/score output graph, without
weight quantization. Its MIT notice is included as a Resources text asset in
the player. A normal build uses these committed bytes and does not convert them.
Neither the two-photo success nor Mac CPU latency establishes accuracy on every
COCO class, live Quest camera access, real-world alignment or sustained 72fps.

The subsequent normal/bundled-model run `35499902362` at `a188e82` also passed:
271 Unity tests (including the new acquisition-age/pause/cadence regressions),
both photos across all repeats with identical predictions, blank/preprocessing
checks, all 16 surface checks, readiness with zero errors and one existing
simulator-update warning, and the Android APK build. No model conversion was
performed in that run. The latest read-only Mac USB check (`35499599170`) found
zero connected devices, so no live recognition or frame-time acceptance follows
from these passing offline checks. See `ACCEPTANCE.md` for the complete checklist.

The same manual workflow supports `mode=device-status`, or run
`pnpm quest:device-status` on the Mac. This only inspects an authorized USB Quest
using Unity's bundled ADB; it does not install/launch the app, grant permissions,
capture camera pixels or collect logcat. Its redacted connection/app/health report
is not a live recognition or frame-rate test. A disconnected headset is reported
as unavailable, never as a successful hardware check.

Photo URLs, SHA-256 digests, original Flickr sources and CC BY 2.0 license links
are recorded in `tools/quest/fixtures/recognition-coco.json`. Inputs are downloaded
only for the explicit recognition-proof command; photos are not shipped in the APK.

1. Install the built APK on the Quest 3, grant camera/scene permission, and use a
   well-lit table with visible legs and a bottle on top. Keep debug off.
2. Confirm the table top/legs and bottle gain blue, while empty space, the wall,
   and the desk surrounding the bottle remain clear. Move around the scene and
   compare both eyes. Note any clipping, bleed, or lag instead of treating the
   synthetic test as proof of live accuracy.
3. Move the bottle and turn your head. Stale highlights must fade, not paint an
   unrelated surface. Cover the camera or interrupt tracking: no solid cubes
   should appear. Test wake-from-sleep and permission denial too.
4. Capture a Quest recording showing those movements and the diagnostic budget
   output. Only a headset run can verify the 72fps budget and alignment with
   real passthrough. The Mac render proof cannot establish either.
5. Check readable names beside a table and bottle, then place a second bottle
   12cm away. Both should have distinct labels; turning away and back should
   not accumulate duplicate labels. Scan a new part of the room without pressing
   a button. Confirm the room-wide RoomSense overlay stays invisible.
