# Build mode ("What can I build?")

Owner: Michael. Server code in `services/api/src/build/`, headset code in `apps/quest/Assets/CutOnce/Device/Build/`
and `Core/Build/`, the Director card in `apps/web/src/director/BuildPanel.tsx`. It registers itself as a `Plugin`
in `services/api/src/plugins.ts`. Nothing is removed: build mode is off until a scan starts it, and E7 and the desk
behave exactly as before.

The plan it was built from is `docs/superpowers/plans/2026-09-19-build-mode.md`; Kit, the co-pilot the builder talks
to, is `docs/superpowers/plans/2026-09-19-kit-copilot.md`. Where the code differs from a plan, the commit message
says why.

## What happens

```
"What can I build?" / "build me a birdhouse" / X
        │                      one photo + 128 × 96 depth rays through its pixels (an organised point cloud)
POST /v1/build/scans ─► 202    saved under the runtime data folder, then processed in order, every step broadcast:
        │
  twins.ts      surfaces (floor, tables, shelves) from a height histogram; objects = connected points above a
        │       surface; each fitted as a box or an upright cylinder, with an error estimate     ─► build_inventory
  label.ts      one vision call (KIT_LABEL_AI: OMNI or OpenAI) names the numbered objects from a fixed vocabulary,
        │       splits lumps, drops non-objects, adds missed clear bottles. No key or no network: names from sizes
  sizes.ts      a known object becomes exactly its standard size (a can is 15.7 × 6.6 cm, whatever was measured)
        │                                                                                         ─► build_inventory (labelled)
  ideas.ts      Kit designs for these objects and the wish: the live model first (BUILD_LIVE_MS, 8 s), then the
        │       rehearsal cache (the same objects and wish, designed before), then the stored rules (rules.ts,
        │       data/build/rules.json). A design is written in a small placement language, solved to exact poses
        │       (solver.ts), checked for tipping, for supports that span what rests on them and for tape
        │       (stability.ts), repaired once, turned into a Plan (plan.ts) that passes validatePlan, and placed
        │       beside the pile facing you (site.ts)                                                 ─► build_ideas (final)
        │
pick one (trigger, "the left one", "build the …", or Start on /director)
        │
POST /v1/build/ideas/:id/start  a normal run: putDraft → approve → seed → createAssembly. From here it is the
                                same step engine, copilot, undo and Director as E7 and the desk.
```

The headset locks the hologram where the server put it, flies each piece from the real object to its place in the
design, then reads each step aloud. "Done" (or B with nothing pointed at) marks the whole current step.

## Endpoints

Every route needs the bearer token.

| Endpoint | Body | Returns |
|---|---|---|
| `POST /v1/build/scans` | `BuildScanUpload` (8 MB limit; the photo must be a JPEG) | `202 { scan_id, session_id }`; results arrive on the stream |
| `GET /v1/build/scans` | — | live scans and recordings, newest first |
| `POST /v1/build/scans/:scan_id/replay` | `{ labels: "saved" \| "live" }` | `{ session_id }`; replays into a new session |
| `POST /v1/build/sessions` | — | a new, empty session |
| `GET /v1/build/sessions/current` | — | the session's wish, surfaces, twins and ideas |
| `POST /v1/build/ideas/rethink` | `{ request }` | `{ accepted }`; new ideas arrive on the stream. A change: the designs on show are not offered again |
| `POST /v1/build/ideas/:idea_id/start` | — | `{ assembly_id, plan_id, revision }` |
| `POST /v1/build/objects` | `{ name }` (a vocabulary object with a standard size) | the added twin |
| `GET /v1/build/vocabulary` | — | `{ items: [{ name, label, standard }] }` |
| `POST /v1/build/say` | `{ text }` | `{ turn_id, audio_url }` in the copilot's voice |

Stream messages: `build_inventory { inventory }` (outlines, then named; while Kit designs, the named inventory is
sent again with a HUD `message` such as "Checked 5 designs: 3 stand up.") and
`build_ideas { session_id, ideas, final, audio_url, message }` (one final list per scan or rethink; each idea's
`made` says `live`, `cache` or `rule`).

## Kit: every spoken turn in build mode

In build mode (`context.mode = "build"`, from the first scan to the end of the walkthrough) every spoken turn is one
Kit turn (`copilot/kit.ts`). E7 and the desk keep the copilot's own turn (`docs/copilot.md`).

```
OMNI:    clip + photo + tables ─► one streamed call ─► KitTurn ─► command said outright? ─► decideKit ─► action
OpenAI:  stt ─► command said outright? ─► words + photo + tables ─► one call ─► KitTurn ─► decideKit ─► action
```

- **In:** what was said (OMNI hears the clip itself; OpenAI gets the transcript), the headset's photo at 1024 px, and
  short tables: the objects as measured, with where they stood at the last scan ("30 cm to your left, 1.2 m away"),
  the tools (tape), the wish, the designs on show left to right, the build in progress and its step, what the server
  is doing, and the last two turns.
- **Out:** `heard, intent, wish, pick, answer, objects, confidence`. The model only classifies and answers;
  `decideKit()` turns that into one of the actions code already has:

| intent | action |
|---|---|
| question | say the answer; `highlight_twins` names the objects it is about (real ids only), for the glow |
| ideas | objects known and nothing being built: rethink them for the wish, no rescan. Otherwise scan, carrying the wish |
| change ("something crazier") | as ideas, but the designs already shown are not offered again; mid-build it rescans (the pieces have moved) |
| pick | start the design named by id, by name or by position ("the left one"); never while a build is under way |
| done, undo | the step commands, at confidence 0.8 or more, and never on a question |
| next, back | step navigation |
| open_e7 | "build E7": a new E7 run, as the Director's New run (not if E7 is up already) |
| unclear | ask back |

- **Commands said outright stay exact.** Whatever the model made of it, "done", "next", "what can I build", "build me
  a birdhouse" or "build E7" goes through the fast path on what was heard. On OpenAI that happens before the model
  call, so a command costs only the transcription.
- **Unsure means asking back in code's words.** Below 0.6 (0.8 for done, undo and E7) Kit asks a question that names
  the command ("Is this step finished? Say done when it is."). The model's own answer describes the action, so it is
  never spoken for an action that is not taken.
- **Providers.** `KIT_TURN_AI` (OMNI by default). With an OpenAI key too, the clip is also transcribed alongside the
  OMNI call: a command or a design named outright answers as soon as its words are known, so a stalled OMNI call
  cannot hold up "next"; everything else waits for what OMNI heard. When OMNI fails and at least 4 s of the 9 s cap
  remain, OpenAI tries once, reusing those words; otherwise "That took too long. Ask me again." Kit's model never
  gets more than the cap has left. `timings_ms` says which answered (`kit_omni`, `kit_openai`).
- **Picking.** A design on show named outright ("let's build the robot", even "build me a robot" with a Robot on show)
  is that design. A position counts only where it says which design ("the right one", "the one on the left", "the
  second design"): "that's right, the birdhouse" is not the right-hand one, and two positions ask which.
- **Outside build mode** a small router (`copilot/router.ts`: OpenAI's `OPENAI_ROUTER_MODEL` within `COPILOT_ROUTE_MS`,
  as the rest of an E7 or desk turn runs on OpenAI) decides whether a sentence wants build ideas, and its wish. Only a
  sure yes (0.7 or more) scans; anything else is answered as a question, exactly as before build mode existed. There,
  "make" is a wish only with "me" ("make me a robot"): "make a list of the parts" is an E7 request.

## The wish

- Said once, it is kept for the session and goes with every design request. Said before a scan ("build me a
  birdhouse"), it rides with that scan if one arrives within a minute; said with the objects known, it rethinks them
  at once.
- A plain "what can I build?" (or "build me something") forgets it. "Scan again" (another view) keeps it.
- A **change** ("something crazier", "make me something bigger") builds on the ask it changes: the designer, Kit and
  the cache all get "a birdhouse, then something crazier". The newest change replaces the last one. It keeps the
  designs already offered out of the next list, except one it names ("make the laptop riser taller").
- A **new ask** replaces both and starts afresh: the birdhouse shown before may be the answer. When every design
  found was offered already, a repeat beats an empty list.
- Said while a scan is being read or named, it goes to that scan, once: no rethink on top, and no second scan.
- The Director's Replay takes the waiting wish too (the fallback when the headset's own scan fails on stage).
- The Director's Build panel shows it ("Asked for: …").

## Designs: live, from rehearsal, or a rule

- The live model has `BUILD_LIVE_MS` (8 s). Past that, the rehearsal cache answers if it holds designs for the same
  objects and wish, and the live answer, when it lands, refreshes the cache. Only new designs end the wait: a cache of
  what was just shown does not. The key is the prompt's version, each object's kind and size (to the centimetre for
  standard sizes, 5 cm for measured ones) and the wish: a birdhouse is not the answer to "something crazier". Not
  the model: a rehearsal on one provider serves the other, or no provider at all. A cached design is solved and
  checked again on today's objects.
- Then the stored rules (`data/build/rules.json`).
- The Director's Build panel marks each design **live**, **from rehearsal** or **offline rule** (`made`). Only a
  live one may be called live on stage.
- To fill the cache before a demo, run the scene at rehearsal with a key: every design that stands is kept under
  the runtime data folder (`build/idea-cache/`).

## Tape

- A tape roll on the table (`tape_roll`, or anything labelled tape; a tape measure is not tape) puts `TOOLS: tape` in
  the design request. A step may then list in `taped_to` the pieces placed before it that it touches (within 5 mm,
  measured between the real shapes: two cans corner to corner do not touch).
- Taped pieces stand or fall as one rigid body: a box taped onto a single can stands, where loose it would slide off.
  A tall, narrow taped stack still tips (it must lean 7° before it goes), and tape holds a box, not a brick: at most
  1.5 kg hangs on tape, counting whatever rests on the taped pieces.
- A joint that is not taped is loose, whatever is taped above it: a box loose on one can still falls off, even with
  a can taped on top of the box.
- The plan says what to tape ("Lay the pizza box flat on top of the tall can. Tape it to the tall can."), joins the
  parts through a `Tape` material and lists tape among the tools. Tape used as a piece (a roll as a wheel) is just
  another object.

## Things the rest of the team needs to know

- **Frames.** The server is right-handed, +Y up, in metres; the headset mirrors X at the boundary (`ModelSpace`), for
  the scan as for everything else. The camera's right is `cross(forward, up)`.
- **A surface keeps its own outline** (`Surface.rect`). The headset's frame points wherever it started, so a table is
  almost never square to the room's axes, and its `min`/`max` box covers floor the table does not. Ask `onSurface()`
  (`build/twins.ts`), never the box.
- **A build plan never sets `rotation_quat`.** A piece on its side has its `size` reordered instead, and cylinders are
  only ever upright. `partAabb` ignores rotation, so this keeps the plan checker honest.
- **`isBuildPlan(plan)`** (`build/plan.ts`) is how anything tells a build-mode run from E7 or the desk.
- **Sizes are snapped.** Depth measures a can 1 to 3 cm wrong at 1.5 m; the vocabulary's standard size replaces the
  measurement whenever the name is known and the measurement could be that object.
- **The server never writes into `data/build/recordings/`.** Live scans, labels, sessions and the idea cache live
  under the runtime data folder, which git ignores: photos of the room must never reach the public repo.
- **`highlight_twins` (for Rhythm's glow).** A Kit answer lists the objects it is about (`o1`, `o2`…, only ids on the
  table). The headset lights those twins; a design's `twin_of` maps its parts back to the same ids.
- **Models.** Each of Kit's jobs (the spoken turn, naming, designing) runs on OMNI (Qwen3.5-Omni through yibuapi's
  OpenAI-compatible API) or OpenAI: `KIT_AI` sets all three, `KIT_TURN_AI`, `KIT_LABEL_AI` and `KIT_IDEAS_AI` one each,
  OMNI by default. A job whose provider has no key uses the other. On OMNI, `OMNI_MODEL` (and `OMNI_IDEAS_MODEL` for
  designs); on OpenAI, `OPENAI_ROUTER_MODEL`, `OPENAI_LABEL_MODEL` (must take images) and `OPENAI_IDEAS_MODEL` each
  default to `OPENAI_MODEL`. The router should be the smallest, fastest model the key lists. Every setting is in
  `.env.example`.

## Running it with no headset

```bash
pnpm build:fixtures     # regenerate data/build/recordings/synthetic_kit (a made-up scene: safe in the public repo)
pnpm build:eval         # every recording with a truth.json: found, labels, size error, ideas. No key needed
pnpm sim                # the pretend headset ends with: build scan → build start → build step
pnpm dev                # then /director → Build mode → Replay on scan_rec_synthetic_kit → Start
                        # or talk to Kit: /sim?mode=build, hold Space ("what can I build?" replays the kit)
```

With Unity in Play mode (Meta XR Simulator), the Director's **Replay** shows the outlines, the names, the previews,
the fly-together and the walkthrough. In the Editor a scan of your own fails with "no depth": there is no depth
sensor to cast against.

With a key in `.env.local`:

```bash
pnpm omni:probe             # OMNI first: text, a photo, the voice clip sent both ways (it says which OMNI_AUDIO works), one Kit turn
pnpm build:eval --live      # name with the vision model and ask the ideas model
pnpm build:eval --router    # on each provider with a key: the router outside build mode, Kit's turn inside it (bar 97% right in budget)
```

On the Quest: `pnpm build:record <scan_id> <name>` keeps a live scan as a recording. Look at its `photo.jpg` first
(the kit pile only, no people), then tape-measure every object into `truth.json`.

## Fallback ladder

| If | Then |
|---|---|
| No key for either provider, no Wi-Fi, or the vision call fails | Objects are named from their sizes alone, and the HUD says so; cached and rule designs still work |
| The live designs fail, or take longer than `BUILD_LIVE_MS` | The rehearsal cache for these objects and wish, then the rules. A late live answer still refreshes the cache |
| OMNI fails on a spoken turn | OpenAI tries once when 4 s of the cap remain; otherwise "That took too long. Ask me again." |
| Kit is unsure what was meant | It asks back, naming the command ("Say done when it is."); nothing changes |
| The scan missed an object | `/director` → Add a missed object (its standard size, in the middle of the table) |
| The scan is unusable | `/director` → Replay a recording, then Start |
| The router is slow or down | Every spoken turn is a question, as before build mode existed |
| A design would tip, balances on too narrow a support, or fails the plan checker | It is repaired once, then dropped: it is never offered |
| The Director starts a new session or a replay mid-scan | The old session stops and says nothing more |

## Known limits

- One session at a time (one headset).
- Kit knows the objects as they stood at the last scan. Move a piece and ask where it is, and the answer is from the
  scan: "scan again" brings it up to date.
- When the table has no free space beside the pile, the design is built where the pile is.
