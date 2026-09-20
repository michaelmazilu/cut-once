# Quest three-pillar functionality contract

This is the hard lock for the VR demo. Changes to vision, AI providers, prompts, schemas, retrieval, analytics, data
engineering, networking, Unity scenes, or Quest settings are incomplete until these three behaviours still pass.

## Protected behaviours

### 1. Passive vision

- The passthrough camera starts after `HEADSET_CAMERA` permission and continuously feeds YOLO.
- A stable recognised object must remain visible to the wearer even when depth-box fitting is late or rejects the shape.
- Before a trustworthy measurement exists, show the label only. Never restore the oversized one-distance guessed box.
- When measurement succeeds, retain the measured centre, size, yaw, surface paint, smoothing, distance cap, and visual
  budget. The measured highlight must continue following the live tracked centre after every later detection; the slower
  round-robin box fit refines its size and yaw but must not freeze its position between measurements. Build mode may pause
  passive scanning for frame time and visual clarity, but it must resume on every exit or failed first scan.

Failure criteria: camera/model errors, detections permanently filtered to zero on a representative headset scene,
recognised objects hidden solely because `hasMeasuredBox` is false, stale objects surviving tracking-origin changes,
pink/one-eye/invisible passthrough materials, or the scanner remaining paused after build mode.

### 2. Kit response

- A Copilot created at runtime or already placed in a scene receives the current runtime server URL/token, `CutOnceApp`
  host, platform camera source, push-to-talk input, microphone, and PCM speaker before its first `Update`.
- Press A to start listening, press again to send. Text must appear even when the camera or TTS is unavailable.
- During an active generated build, asking how to build it or requesting the instructions must return the authoritative
  current plan step, keep that step's objects highlighted, and read the instruction through Kit's normal TTS stream.
- The request budget must include audio/frame upload, the server's hard cap, and tunnel latency. A stalled audio stream
  must end safely without suppressing the text response.
- On a physical Quest, a pushed persistent config must override the Editor's bundled config. `127.0.0.1` without an ADB
  reverse points at the headset and is a visible configuration failure, never silent dead air.

Failure criteria: no `[Copilot] configured/listening/query/answer` trace, localhost used on an untethered headset,
permission denial without a visible explanation, HTTP timeout shorter than the server budget, answer text discarded
because audio failed, or a scene prefab bypassing runtime dependency injection.

### 3. Blueprint creation

- “What can I build?” or X captures one colour-camera frame plus the depth grid and receives an HTTP 202 with a session.
- Inventory/twins and ideas normally arrive over the WebSocket. After every accepted scan, the headset also polls the
  authoritative session snapshot while it waits, so a dropped WebSocket message cannot strand a completed blueprint.
- Recovered messages must still pass `BuildFlow`'s session/ticket/phase checks. Late data from an abandoned session may
  never replace the current build. Starting an idea must load and place its generated plan.

Failure criteria: zero depth hits on a permission-ready Quest, an accepted scan with no recovery polling, inventory or
ideas from another/abandoned session being shown, previews that can no longer start remaining selectable, or a generated
plan failing the normal plan checker.

## Required gates

Run these before merging any change that can touch the headset path:

```text
pnpm quest:pillars       fast structural contract; runs on ordinary CI
pnpm typecheck
pnpm test                server, schemas, build generation, copilot and Quest tooling
pnpm quest:check         Android compile, EditMode tests, settings and scene budgets
pnpm quest:sim           simulator fidelity and visual smoke test
```

For a release APK, the last gate is physical hardware and cannot be replaced by Editor evidence:

```text
pnpm copilot:check
pnpm llm:smoke
pnpm tts:smoke
pnpm quest:build
pnpm quest:install
pnpm quest:connect       pushes the current tunnel URL/token, then relaunches
```

On the headset, record one trace proving all three in the same APK/session:

1. `[Vision] CAMERA READY`, non-zero accepted detections, and a visible label; confirm a measured highlight when depth fits.
2. `[Copilot] configured`, listening start/end, query sending, answer received, visible text, and audible PCM.
3. A scan with non-zero depth hits, HTTP acceptance, inventory/twins, ideas, selection, and the generated hologram.

The historical working reference is `43983d1`, but it is not the implementation target: newer measurement and audio logic
stays. The regression gate protects outcomes and fallbacks rather than freezing one algorithm.

## Changing the architecture intentionally

Do not weaken `tools/quest/pillars.ts` merely to make a refactor green. In the same change:

1. preserve the behaviour above or document the replacement contract;
2. update the behavioural Unity/server test that proves it;
3. update the structural gate to recognize the new implementation;
4. attach simulator/headset evidence for permission, networking, frame-time, or passthrough-rendering changes.

AI/provider or data-pipeline swaps must preserve the `CopilotResponse` contract, deterministic command safeguards,
OpenAI fallback when configured, text-first degradation, twin IDs, session snapshots, and plan validation. A provider's
health check or successful backend trace alone does not prove that the Quest received or rendered the result.
