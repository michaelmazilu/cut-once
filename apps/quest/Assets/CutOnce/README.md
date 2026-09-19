# Cut Once headset app: the build guide ("Litematica")

One component runs the whole thing. Add **`CutOnceApp`** to a scene that has Meta's camera rig, or use
**Cut Once > Rebuild main scene**, which writes `Assets/CutOnce/Scenes/Main.unity` with the rig, passthrough and the
app, and makes it the first scene in the build. There are no prefabs and no inspector references to wire.

## Run it

1. **Server address and token.** Copy `apps/quest/cutonce.config.example.json` to
   `Assets/CutOnce/AR/Resources/CutOnce/config.json` (git-ignored) and fill it in. On a built headset you can
   instead push a file, which wins over the bundled one:
   `adb push cutonce.config.json /sdcard/Android/data/<package>/files/cutonce.config.json`.
   With neither, the app uses `http://127.0.0.1:8080`: fine in the Editor next to `pnpm dev`, useless on a headset.
   Use the `https://` tunnel address on the headset: Android blocks plain `http://` unless the app allows cleartext.
2. **Cut Once > Rebuild main scene**, then Play (Meta XR Simulator on a laptop, or build to the Quest).
3. With no server at all the app still runs: it loads the last run from its journal, or opens empty. The app ships no
   plan: nothing is drawn until Kit builds something.

## Controls (right controller)

| When | Do | Result |
|---|---|---|
| Placing | Point at the floor or a table | The hologram follows, standing on the surface, facing you |
| Placing | Stick left / right | Turns it |
| Placing | **Trigger** | Locks it and saves a spatial anchor: next launch it is where you left it |
| Placing | **B** on each of the plan's two touch points (the small marker is the point that is recorded) | Second way in: snaps the plan onto something that already exists |
| Locked | Point at a part, **B** | Built ↔ missing (so B is also undo) |
| Locked | Point at a part, **hold B** | Flags it wrong |
| Locked | Hold **grip** + stick | Slides the hologram; with the trigger also held: stick turns and lifts it |
| Locked | Hold the **stick button** 1 s | Place again |
| Any time | Hold **A** | Ask the copilot (Rhythm's `CopilotController`; created automatically if the scene has none) |

There are no QR codes, no markers and no calibration step.

### Build mode ("What can I build?")

Off until a scan starts it, so other runs behave as above. The server does the thinking (`services/api/src/build`);
the headset scans, shows what comes back and flies the chosen design together.

| When | Do | Result |
|---|---|---|
| Any time | Say **"What can I build?"** | Scans this view: one passthrough photo plus 128 × 96 depth rays through its pixels. The run that was showing hides; outlines appear over the real objects, dim at first, then named with their sizes. Mid-build it is the deliberate way to start over (ignored for the second or two the pieces are flying) |
| Until a design is chosen | Press **X** (left controller) | The same scan. Once a design is chosen X is off, so a thumb resting on it cannot throw the walkthrough away |
| Any time in build mode | **Hold X** for a second | Leaves build mode: the run that is loaded shows again, where it is |
| Ideas are floating above the pile | Point at one (or reach into it), **trigger** | Starts that design: it locks where the server put it and each piece flies from its real object into place. A trigger that hits no preview does nothing |
| Objects are showing, no ideas yet | **Trigger** on empty space | Scans that view too; the server merges it into the room |
| Ideas are showing | Say **"Build the …"** (or **Start** on `/director`) | Picks by name |
| Building | Say **"Done"**, or **B** with nothing pointed at | Marks the whole current step; the next step is read aloud |
| Building | Everything in the table above | Unchanged: B on a part, next / back / undo, questions, grip to nudge |

A design's lock is for this session only: it gets a spatial anchor of its own, but the anchor and nudge saved for the last
build are left as they were, so the next launch still finds them. Another run started from the Director page ends build mode and stands on the build site, where the judge is looking. In the Editor there is no depth, so a
scan fails with "no depth here yet": replay a recorded scan from `/director` instead, and the twins, ideas and
fly-together all show.

## How it is put together, and why

```
events ─► BuildStateStore ─► Reducer.Fold ─► VisualStateResolver ─► HologramPalette.StyleFor ─► PartView (shader)
   ▲             │                                   └────────────► HudText ─► HudController
   │             └─► SyncEngine ─► ApiClient / Journal
 B button, voice, the stream
```

| Assembly | UnityEngine? | Holds | Why it is separate |
|---|---|---|---|
| `CutOnce.Core` | no | Plan/event/state types, `Reducer` (twin of `fold.ts`), `BuildStateStore`, visual states, palette, material list, accuracy tags, `HudText` | Pure C#, so its tests run in under a second with `pnpm quest:core-test`, and inside Unity too |
| `CutOnce.Net` | no | `ApiClient`, `StreamClient`, `Journal`, `SyncEngine` | Same: tested against a fake server that follows the API's rules |
| `CutOnce.Net.Unity` | yes | `UnityHttpTransport` | The one platform seam for HTTP |
| `CutOnce.AR` | yes, no Meta | `ModelSpace`, `ShapeFactory`, `PartView`, `AssemblyView`, the shader, placement, nudge, proof overlay, selection | Testable in EditMode with no headset and no Meta packages |
| `CutOnce.UI` | yes | `HudController` | Built in code; wording lives in `Core.HudText` |
| `Device/` (Assembly-CSharp) | Meta | `QuestInput`, `QuestSurfaceRaycaster`, `QuestAnchorStore`, `CutOnceApp`, `Build/` (build mode: `BuildMode`, the scan, the twin outlines, the previews, the fly-together) | Everything that names a Meta type is here and nowhere else |

Decisions worth knowing before you change something:

- **Build state is never stored.** It is always `Reducer.Fold(plan, events)`. The C# reducer is held to the TypeScript
  one by the shared fixtures in `data/fixtures/events_to_state`: all eight must fold to the same state, field for
  field (`ReducerFixtureTests`). Change one reducer and that test tells you.
- **A tap shows at once.** It becomes a provisional event (version `null`), is saved to the journal, then sent. The
  server's numbered copy replaces it by event id, whether it arrives in the POST response or on the stream. Only the
  server's 409 (no-op) and 422 (invalid) may discard a tap; no network, a 5xx or a wrong token keep it queued, and it
  survives a restart.
- **Plans are right-handed; Unity is not.** `ModelSpace` mirrors X, the same convention glTFast uses and the one
  `AlignmentSolver` already expects. Everything under `AssemblyRoot` is in that mirrored model space;
  `AssemblyRoot`'s pose *is* the alignment, and only `AlignmentController` writes it.
- **Meshes are generated at true size** (no transform scale), so the shader measures the distance to a box's edges in
  object space. That gives crisp edges of a constant width in pixels with no wireframe pass, no extra geometry and no
  post-processing, which the Quest cannot afford. `GameObject.CreatePrimitive` is avoided because a build can strip
  what it needs.
- **The shader is in a `Resources` folder** because nothing in a scene references it, and `Shader.Find` alone does not
  stop a shader being stripped from a build. It blends alpha with separate factors: over passthrough, joint factors
  square the alpha and wash the hologram out.
- **Colours come from `hologram-palette.json`**, generated from `HOLOGRAM_PALETTE` in `visual.ts`, so `/preview`, `/sim`
  and the headset match. The app carries a copy (a build cannot read outside `Assets`); `BundledFilesTests` fails when
  the copy is stale and `pnpm quest:bundle` refreshes it. 
- **Accuracy is visible.** A part tagged in `external_ids` with `source: assumed | inferred`, an unknown tolerance, or
  a tolerance above 5 cm draws **dashed**, and its card says so ("drawings, ±0.1 m"). An untagged part is a designed
  part: exact by definition.
- **Placement stands the model on the surface**: the footprint's centre goes to the pointed spot and the lowest face
  rests on it (the desk's tabletop extends below y = 0). It uses the Quest 3's depth raycast when there is one and the
  floor plane otherwise, so it works in the simulator and needs no room scan.
- **Buildings are tabletop models.** A plan wider than 4 m is shown at the largest architectural scale that keeps it
  under 80 cm (a 91 m building at 1:200). Everything scale-dependent (placement, nudge, collider padding, line width,
  dashes, the proof overlay) is sized for the room, not the model. The HUD title shows the scale. Touch points are
  refused at tabletop scale; they are for overlaying at full size.
- **Model files are read by our own `GlbReader`** (Core, tested under dotnet): a plan's mesh parts come from its `.glb`,
  fetched from the server's plan assets, else the bundled copy in `Resources/CutOnce/<file>.bytes`, else drawn as
  their bounds box. No extra Unity package. `GlbMeshes` mirrors X and swaps each triangle's winding (a reflection
  turns a mesh inside out; the signed-volume test holds it), and draws crease lines as thin crossed quads.
- **Selection prefers the smallest part among near-equal hits**, because colliders are padded by 1 cm and parts nest (a
  power strip inside its tray can only be reached that way).

## Tests

| Command | Runs | Needs |
|---|---|---|
| `pnpm quest:core-test` | Core + Net (state replay against the shared fixtures, sync against a fake server, HUD wording), under a second; also a CI job | Unity installed (uses its bundled .NET) or a system `dotnet` |
| Unity Test Runner, EditMode (or `pnpm quest:check`) | The same tests plus geometry, placement, and the shader compiled for Vulkan, GLES3 and Metal in mono and stereo | The Unity project |
| Unity Test Runner, PlayMode | `AppSmokeTests`: starts the whole app with no server and no headset; fails on any logged error | The Unity project |

None of this has run on a physical headset yet. First things to look at there: the hologram over passthrough (alpha), the
depth raycast while placing, the anchor coming back after a restart, and the pointer's direction.

## Not done here

`TimelineController` (history scrub on the headset), depth occlusion, a 1:1 walk-in mode for buildings, and the
camera check's "suggests; you confirm" screen.
