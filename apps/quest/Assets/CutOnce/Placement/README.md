# Physical surface checks and object placement demo (Quest 3)

## Real depth checks in the main app

`CutOnceApp` installs `LivePlacementCheck` automatically. It checks the visible surfaces of the selected
full-size box hologram using Quest environment depth and normals. Unity colliders, saved room planes,
YOLO's approximate centre, and simulated object transforms are never used as physical evidence.

1. Close Unity, run `pnpm quest:build`, then `pnpm quest:install`.
2. On Quest 3, allow camera and spatial data access. Load/start a build and lock its hologram in place.
3. Position the physical box, then point the right controller at its hologram. Expose at least two faces,
   keep hands clear, and view it from 25 cm to 2.5 m away.
4. Amber means measurements are settling. Green and **Visible surfaces match** mean repeated depth
   measurements match within 5 cm and surface-normal angles within 12 degrees for 0.5 seconds.
5. Inspect the object, then press B to mark built. The check does not write completion events.

At least two nonparallel faces must each match at 8 of 9 sampled points. Box dimensions must all be at
least 8 cm. Thin planks, cylinders, arbitrary meshes and scaled tabletop models are not supported.
Missing depth, low-confidence normals, occlusion, stale images, target changes or tracking loss clear
or restart feedback. Checks run at most 10 times/second, at most 27 rays, with a 3 ms ray-query budget.
The actual headset performance and sample availability within that budget still need device testing.

Green means **visible geometry matches**, not identification of a particular physical instance, proof
of fastening/hidden connections, release, or structural stability. The 12-degree test compares surface
normals, not a recovered complete object rotation. A similarly shaped substitute can match. A single
wall/table plane cannot confirm a box. The operator must still check object identity before marking B.

Camera capture age is preserved when converting to Unity's monotonic clock. The environment-raycast
API supplies hit position, normal and confidence, but no per-hit timestamp; independent depth-frame
freshness auditing remains a platform limitation. There is no simulated fallback when depth is missing.
The Mac simulator cannot validate real camera/depth or passthrough readability. The pose demo below
remains a separate way to exercise the existing pose tracker.

## Pose-based demo

This feature checks whether an identified object's observed 3D pose matches its assembly target.
`Confirmed` currently means **pose alignment confirmed**, not physical pickup/release or task completion.
The stronger product architecture and research decisions are documented in `RESEARCH.md`.
The demo supplies observations from movable Unity transforms. It does **not** recognize objects in
the simulator's room or track real camera pixels. The fixture objects can be moved with either Touch
controller, which stands in for moving a detected real object until the detector is integrated.

## Run the simulation

1. Open `apps/quest` with **Unity 6000.6.2f1**. Let script compilation finish.
2. Choose **Cut Once > Placement Demo > Open demo scene**. This opens
   `Assets/CutOnce/Placement/PlacementDemo.unity` (the menu generates it if missing).
3. Choose **Meta > Meta XR Simulator > Activate**. Open the installed standalone Meta XR Simulator
   if it is not already visible. Use its Quest 3 profile and choose a synthetic room through the
   environment control at the top right. The existing project README covers simulator installation.
4. Press **Play** in Unity. Three fixture objects and three target outlines appear, approximately
   one metre forward from the rig's origin, at table height. If they are outside your view, use the
   simulator's **Inputs / Input Bindings** controls to look down and toward them. Inspect the
   simulator window to see the synthetic passthrough room; Unity's Game view may show black behind
   the holograms because passthrough is composited separately.
5. For the fastest demonstration, choose **Cut Once > Placement Demo > Snap all objects to targets**.
   They become yellow while aligning, then green after **0.5 seconds** of fresh aligned observations.
   The separate target outlines disappear, leaving the green assembled shelf.
6. Choose **Reset demo** from the same menu to return the objects to their starting positions.

To move objects with a Quest controller, bring either Touch controller within about 16 cm of an object,
hold its **grip/hand trigger**, and move the controller. Release the grip to drop it. Both controllers are
enabled. The controller moves only the simulated object; its target outline stays fixed. The same input
path works in Quest Link and Meta XR Simulator. The object is not physically colliding with the room yet;
this is a direct pose-grab fixture for testing placement feedback.

All demo control menus below are under **Cut Once > Placement Demo** and operate during Play mode.

## Move an individual object yourself

1. Choose **Select box 1**, **Select box 2**, or **Select plank**.
2. In the Unity **Scene** tab, press **F** to frame the selection, then **W** to move or **E** to rotate.
   Alternatively edit its **Transform** in the Inspector. Move the child named
   `Simulated object - move me`, not its parent or `Target - keep fixed`.
3. Align it with its target outline. Or choose **Snap selected object to target** to align that object
   exactly and observe the transition.
4. Choose **Print placement status** and inspect the Console for the state, distance in metres,
   angle in degrees, and hold progress.

The default target transforms below are local to each identity parent; the generated demo's parents
have identity transforms, so these are also world positions:

| Object | Target position (X, Y, Z), metres | Target rotation |
|---|---|---|
| `box_01` | (-0.24, 0.85, 1.00) | (0, 0, 0) |
| `box_02` | (0.24, 0.85, 1.00) | (0, 0, 0) |
| `plank_01` | (0.00, 0.97, 1.00) | (0, 0, 0) |

## What to test

| Action | Expected result |
|---|---|
| Stay farther than 10 cm from target | Cyan, Misplaced |
| Move within 10 cm and 24 degrees | Yellow, Near |
| Stay within 5 cm and 12 degrees for 0.5 s | Yellow Aligning, then green Confirmed; target hides |
| Move a confirmed object more than 7.5 cm or 18 degrees away | Confirmation clears; target reappears |
| Select object, then **Toggle selected object tracking** | Grey, TrackingLost; target reappears |
| Toggle tracking back while aligned | Must complete a new hold before confirming |
| **Reset demo** | All objects misplaced, tracking restored |

At `[Placement Demo]`, the `SimulatedObjectPoseSource` Inspector exposes each object's **Tracked** and
**Confidence** fields. Confidence below 0.7 loses tracking. The boxes and plank accept a 180-degree
turn about their local Y axis. Other integrations can choose exact rotation or ignore it for a sphere.
Position and rotation must both satisfy a threshold; distance alone does not confirm a rotated plank.
These are adjustable prototype tolerances, not claims about detector accuracy or structural safety.

## Integrate the real detector later

The dependency boundary is `IObjectPoseSource.TryGetObservation(objectId, out observation)`.
`PlacementBinding` consumes it and checks its assigned `target`. `PlacementFeedback` is the visual
owner and reacts to the binding's `StateChanged` event. No changes to Copilot are required for this demo.

1. Your assembly plan assigns each target a **stable physical-object ID**, position, and orientation.
   Create one target Transform and one PlacementBinding for each assigned object.
2. Add `BufferedObjectPoseSource` to your integration GameObject. Assign it to each binding's
   `poseSource`. Configure the binding while its GameObject is inactive, then activate it. Set
   settings and object ID before enabling; disable/re-enable after changing identity/configuration.
3. Your friend calls `Submit` on that component for each new detection, **on Unity's main thread**:

```csharp
// worldPosition/worldRotation refer to this object's agreed centre in the shared room frame.
// captureTime is expressed on Time.unscaledTimeAsDouble's clock.
source.Submit(new ObjectObservation(
    "box_01", new Pose(worldPosition, worldRotation), confidence, captureTime));
```

4. Convert camera coordinates into the same Unity world/anchor frame as the targets. Use the
   camera pose **at capture time**, metres, and the same object centre and local axes. A 2D image
   rectangle alone is insufficient: 3D pose estimation/tracking must happen before this boundary.
5. Preserve the timestamp of the actual observation. Do not refresh its timestamp just because
   another Unity frame passed. Convert external timestamps into the Unity monotonic clock; neither
   Unix time nor an unsynchronised server clock is valid. Account for processing latency.
6. Submit confidence in [0, 1]. The checker rejects stale (>0.25 s), future, low-confidence, invalid,
   and wrong-ID poses. The buffer rejects duplicate/out-of-order timestamps. `Forget(id)` immediately
   removes an explicitly lost object. `Clear()` resets the buffer after changing tracking sessions.
7. Recenter the shared world frame consistently for both targets and observations. Disable bindings
   during relocalization and enable them after the frame is stable, requiring a fresh hold.

The buffer holds at most 256 IDs; remove obsolete objects with `Forget`. There is no per-frame LINQ,
scene lookup, material creation, or list allocation in the placement checker/feedback. The simulated
source uses a small array; a real detector should use the buffer or implement the interface directly.

## Verification

EditMode tests: **Window > General > Test Runner > EditMode**, select `CutOnce.Placement.Tests`, Run.
Tests cover stable dwell, frozen frames, missing/stale/future/low-confidence observations, wrong IDs,
out-of-order frames, invalid poses, target changes, rotation symmetry, and exit hysteresis.

The demo uses the existing stereo hologram shader, transparent camera background, passthrough rig,
and BudgetProbe. It is separate from Main and QuestBaseline and is not added to APK build scenes.
For a headset demo, include this scene deliberately in your build profile, or use Quest Link on Windows
with the simulator deactivated. Real detector integration, real passthrough readability and headset
frame time still require the project's step-5 headset verification.
