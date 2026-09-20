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

RoomSense's room-wide glow and guessed gaze labels are suppressed while Vision
owns recognition. Its MRUK room geometry, anchors and colliders remain active.
This policy applies to both existing and later-created RoomSense components.

The bundled model has 80 COCO categories. Its legacy `diningtable` label is shown
as `dining table`; a water bottle is identified as `bottle`, not a particular
brand or its contents. No class outside that vocabulary is promised. Detection
batches are NMS-filtered, then associated one-to-one with nearby same-class
tracks so two nearby bottles do not update the same identity. This does not
guarantee identity across class changes, long absences, or crossing objects.

## What the evidence means

`pnpm quest:surface-proof` runs Unity with graphics, renders the production shader
against a synthetic depth texture from known geometry, and saves PNGs and a JSON
report in `apps/quest/Logs/cli/surface-proof`. This tests the surface renderer,
not object detection or the headset's sensors. The Mac workflow runs it before
building the APK and includes its output in the diagnostics artifact.

The fixture renders physical colour first, then clears its Unity depth before
drawing highlights: real passthrough is not an opaque virtual table. A separate
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
