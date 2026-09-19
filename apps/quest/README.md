# Cut Once on the Quest 3: open, simulate, check

This folder is the Unity project for the headset app, the same project on every machine: Michael's Mac and the
Windows laptops. [AGENTS.md](AGENTS.md) has the rules every change follows so that what works on a laptop also works
on the Quest.

## One-time setup (Mac or Windows)

1. **Unity Hub.** Install **Unity 6000.6.2f1** exactly, with **Android Build Support** and, under it, **OpenJDK**.
   **Android SDK & NDK Tools** is needed only to build an APK. On a Mac, Meta's SDK does not compile at all without
   the Android module, even just for the simulator. If Unity is already installed: Installs, the gear on
   6000.6.2f1, Add modules.
2. **Meta XR Simulator** v205, the standalone app:
   [Mac (Apple Silicon)](https://developers.meta.com/horizon/downloads/package/meta-xr-simulator-mac-arm/) or
   [Windows](https://developers.meta.com/horizon/downloads/package/meta-xr-simulator-windows/). On a Mac it must end
   up at `/Applications/MetaXRSimulator.app`. Do not install the old `com.meta.xr.simulator` package.
3. **Git LFS:** `git lfs install && git lfs pull`. Without it, binary files are text pointers.
4. In Unity Hub, click **Add → Add project from disk** and pick `apps/quest`. The first open imports packages for a
   few minutes.
5. Check it with **Cut Once > Check Quest readiness**. If anything is red, run **Cut Once > Apply Quest 3 settings**.

## The laptop loop: Meta XR Simulator

1. **Meta > Meta XR Simulator > Activate**, or the simulator icon beside Play. The Console says
   `[Meta XR Simulator is activated]`.
2. In the simulator window, **Choose environment** and pick a room. The room stands in for passthrough. Without
   one, the room is a magenta checkerboard.
3. Open `Assets/CutOnce/Scenes/QuestBaseline.unity`, or your scene, and press **Play**. Leave the simulator running
   between Play sessions; the next one starts faster.
4. The simulator window lists the keyboard and mouse controls for the head, the controllers and the hands.

`QuestBaseline` shows one box per hologram state (built, replay, current step, missing, future, wrong) in the
shared palette, at true scale: 15 cm boxes on a table-height row, 80 cm in front of you.

**One command:** `pnpm quest:play` (Editor closed) opens the simulator, a room and Unity in Play mode on `Main.unity`,
and leaves them running for you (`--scene QuestBaseline`, `--room office|furnished|living|game|bedroom`).

**Unattended:** `pnpm quest:sim` (Editor closed) opens the simulator and a room (`--room office`, the default, or
`furnished`, `living`, `game`, `bedroom`), runs the baseline scene in the Editor window, and prints what the simulator
gave. The simulator only starts a session from the Editor window with its own window running, never from Unity's
batch mode, so the command opens both. On Windows, open the simulator and pick a room first.

Measured on the M3 Mac, simulator v205, Quest 3 profile:

| | Simulator |
|---|---|
| Eye buffer | 1680 x 1760 per eye, 72 Hz |
| Field of view, left eye | 54° outward, 40° inward, 50° up, 49° down |
| Passthrough | Running, showing the room |
| Passthrough camera (`PassthroughCameraAccess`) | Starts, and reports the Quest 3 lens: 1280 x 960, focal length 853.6 px, 3.2 cm left, 1.7 cm down and 6.3 cm forward of the eyes. **No pixels on macOS while a room is connected** (Meta's checkerboard without one) |
| Budget | Draw calls and triangles counted by `BudgetProbe` |

## What the laptop shows truthfully, and what only the headset shows

| | On the laptop (simulator) | Only on the headset |
|---|---|---|
| Code and settings | The same project, C# and OpenXR calls. `pnpm quest:check` also compiles the scripts as the Android build does | Code behind `#if UNITY_ANDROID && !UNITY_EDITOR`; the permission prompts |
| What you see | Quest 3 eye buffer, field of view and refresh rate; scale in metres; hologram colours and see-through fills over a room | Lens clarity; the Quest display's colour and contrast |
| The room behind | A synthetic room | Real passthrough: grain, exposure, your actual desk |
| Aligning to the desk | Controller touches and hand placement, with the simulated controllers. The rooms have no desk of ours | Your real desk and hand |
| Copilot photo | The camera's lens geometry (what `PartProjector` uses), but no pixels on macOS. `FixtureFrameSource` supplies a stored photo in the Editor | The real photo |
| Build-mode scan | No depth, so a scan fails with "no depth here yet". Replay a recorded scan from `/director`: the twins, ideas and fly-together then show as on the headset | The depth rays (`EnvironmentRaycastManager`), the spatial-data permission, how long a scan takes |
| Controllers and hands | Keyboard, mouse or a gamepad. Meta lists some controller-input limits on macOS | The real feel |
| Mic and network | The laptop mic; the server on `localhost` | The Quest mic; Wi-Fi to the laptop |
| Depth occlusion | Windows only | Yes |
| How much it draws | Draw calls and triangles: **the same numbers as the headset** (`BudgetProbe`) | — |
| How fast it runs | No: the laptop is several times faster | Frame time, heat, throttling |
| Comfort | No | Yes |

The web app's `/sim` covers the server and copilot loop without Unity. This simulator runs the actual headset app.

## Checks before pushing

- **Editor open:** use **Cut Once > Check Quest readiness**, and the EditMode tests (Window > General > Test Runner).
- **Editor closed:**
  - `pnpm quest:check` compiles the scripts for the Editor and for the Android build, checks the settings and every
    build scene against the Quest 3 budget, runs Meta's Project Setup Tool, and runs the EditMode tests. It exits 1
    if the Quest would break. Logs are in `Logs/cli/`.
  - `pnpm quest:setup` puts the Quest 3 settings back and applies Meta's automatic fixes.

One warning stays on purpose: Meta suggests dynamic resolution. It is off because the simulator cannot show it and
it softens the thin edge lines. Turn it on only if the headset misses 72 fps with a design loaded.

## On the headset

- **Windows:** use Quest Link. Deactivate the simulator first (Meta > Meta XR Simulator > Deactivate), connect Link,
  and press Play: same project, same scene.
- **APK** (needs the NDK module): `pnpm quest:build`, then `pnpm quest:install` with the Quest plugged in by USB-C and
  USB debugging allowed. The install does not pre-grant permissions, so the headset asks for the camera and the
  microphone as it will at the demo. Development builds log `[Budget]` lines with the frame rate and dropped frames
  every 10 s: `adb logcat -s Unity`.

Check on the headset before the demo:

- **Passthrough:** the room shows, not black. The hologram sits on the desk at the right size.
- **Alignment:** the hologram sits on the real desk, with the far corner off by at most 5 mm.
- **Copilot:** it asks for the camera and microphone once, and its answers point at the right part.
- **Frame rate:** the `[Budget]` lines show 72 fps and no dropped frames with a full design loaded.
- **Legibility:** the palette states can be told apart over the real room at arm's length.

## AI agents (Claude Code, Codex)

Meta's SDK has two MCP servers. Connect them once in Unity: **Meta > Tools > AI Tools**, pick your assistant, and
run the connection command it shows. Then turn on **Meta > Meta XR Operator > Activate**.

- **`meta-xr-unity-runtime`** (Editor): compile, compile errors, run tests, and Meta's Project Setup Tool checks and
  fixes.
- **`meta-xr-operator`** (Play mode with the simulator): screenshots, head pose, controller input and the scene
  hierarchy, on port 8720. It is also in `.mcp.json` here (`http://localhost:8720/sse`).

Unity registers them for this folder, so start the assistant in `apps/quest` for Unity work.
