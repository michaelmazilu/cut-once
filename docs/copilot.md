# The copilot (pillar C)

Owner: Rhythm. Server code in `services/api/src/copilot/`, headset code in
`apps/quest/Assets/CutOnce/Copilot/`. Nothing in the core imports either — the copilot registers itself
as a `Plugin` in `services/api/src/plugins.ts` and hands the core one hook.

The copilot is called **Kit** (the prompt says so). In build mode every spoken turn is a Kit turn instead of the
one below: one call that hears, sees the table and answers, on OMNI or OpenAI (`copilot/kit.ts`, `kitTurn` in
`pipeline.ts`, and `docs/build-mode.md`). A Kit answer may carry `highlight_twins`, the objects it is about, for the
headset's glow. E7 and the desk keep the turn below, all on OpenAI.

## Endpoints

| Endpoint | Body | Returns |
|---|---|---|
| `POST /v1/assemblies/:aid/copilot/query` | multipart `context` (CopilotContext), `audio` (WAV), `frame` (JPEG) | `CopilotResponse` |
| `GET /v1/audio/:turn_id` | — | chunked `audio/L16; rate=22050` PCM, streamed while it is still being generated |
| `POST /v1/assemblies/:aid/verify` | multipart `request` (VerificationRequest), `frame`, `expected_view?` | `VerificationResult` |
| `GET /v1/copilot/cache` | — | what has been promoted as a fallback answer |
| `POST /v1/copilot/debug/capture` | multipart `frame?`, `audio?`, `note?`, `context?` | the capture's metadata (this is G2) |
| `GET /v1/copilot/debug/last`, `/frame.jpg`, `/audio.wav` | — | the newest capture |
| `GET /debug` | — | a self-contained debug page; the only route outside `/v1`, and every call it makes carries a bearer |

## The turn, stage by stage

```
upload → stt → [ retrieve ‖ annotate ‖ route ] → model (+ at most one tool round) → ground → respond → tts
         │                          │                                                             │
         │                          └─ a sure "build ideas" (with its wish): start_scan, no model  └─ streamed
         └─ fast path: "done" / "next" / "back" / "undo" / "mark X built", "what can I build",
            "build me a …", "build E7": skips the model entirely, under 1.5 s
```

Every stage records its own milliseconds into `timings_ms`, which the Director page shows. The hard cap
is 9 s (`COPILOT_CAP_MS`); past it the turn falls back to a cached answer, or to text that says so.

## Things the rest of the team needs to know

- **The server writes the event for a spoken command.** A `mark_state` action in the response has already
  been applied. The headset shows a 2 s Undo toast and must **not** append its own event.
- **Only commands change the build.** A state change the model proposes is applied only for a statement
  (never a question), with `needs_clarification` false and confidence ≥ 0.8; its event records the model's
  confidence and the words. A replayed cached answer never carries an action.
- **"Undo" steps back** through changes a person made (voice or manual), one per "undo", and never reverses
  the seeded demo state. Its event is noted `undo of evt_…`. With nothing left it answers "There's nothing to undo."
- **Nothing heard is still an answer.** An empty or failed transcription returns a spoken "I didn't catch
  that / couldn't hear that. Hold A and ask again." Only a missing `OPENAI_API_KEY` is a 503 (in build mode: no
  key for OMNI or OpenAI).
- **A frame is optional.** Without one the headset sends `"camera": null` and no `frame` part; the model
  answers from the tables and documents, and the HUD's cached answers still work.
- **`CopilotAction` gained a `step_nav` variant** (`{type:"step_nav", direction:"next"|"back"}`) for spoken
  "next" and "back". It carries no part and writes no event — it is headset-local navigation. The JSON
  Schema in `packages/schemas/dist/jsonschema/` is regenerated, so the C# mirror picks it up.
- **The camera check never writes part state.** A confident verdict appends a `verification` event, which
  attaches a verdict and leaves `state` exactly as the person set it. `unsure` writes nothing at all.
- **Nothing invented reaches the headset.** Part ids the plan does not have are dropped, and citations
  that were not in the retrieved set are dropped, before the response is sent.

## Running it

```bash
pnpm g0                 # does the model take an image and return strict JSON? Also checks STT and TTS
pnpm copilot:fixtures   # regenerate the synthetic frame, context packet and response
pnpm sync:fixtures      # copy them into the Unity project
pnpm dev                # API on :8080, then open http://127.0.0.1:8080/debug
```

`pnpm g0` is the gate. It checks the model the copilot actually uses (`OPENAI_COPILOT_MODEL` if set,
else `OPENAI_MODEL`); a check whose key is missing is skipped, not failed. If it fails on images, set
`OPENAI_COPILOT_MODEL` in `.env.local` to the vision-capable model and re-run: voice answers and camera
verification both follow it. Tell the team too — drawing extraction reads images with `OPENAI_MODEL`
through the same helper (`services/api/src/llm.ts`).

## Fallback ladder

| If | Then |
|---|---|
| The model will not take images | `OPENAI_COPILOT_MODEL` |
| No camera frame (G2 failed) | The copilot answers from geometry and documents; verification is cut |
| Search is down | `retrieve()` returns `[]` and the answer says it has no drawing for it |
| Elasticsearch tools are down | `callKnowledgeTool` falls back to its direct twin, then to an error string |
| The turn passes 9 s | The cached answer for that question, matched by `scripted_query_id` or by wording |
| No TTS key | Text answer with `audio_url` set but no bytes; the headset shows the text |
| The network is gone | The HUD query buttons send `scripted_query_id`, which never touches the model |
