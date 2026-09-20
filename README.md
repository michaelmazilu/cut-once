# Cut Once

A construction copilot and "Litematica for the physical world", built at Hack the North 2026.
Drawings become a full-size hologram on a Meta Quest 3. The hologram shows what is built, what is missing and what comes
next, keeps a version history of the real build, and answers spoken questions about the part you point at.

## What is in this repo

| Path | What |
|---|---|
| `apps/quest` | Unity 6000.6.2f1 headset app (Meta XR SDK 205). Setup, the simulator loop and the checks: `apps/quest/README.md`; rules for Quest-safe code: `apps/quest/AGENTS.md` |
| `apps/web` | Laptop pages: `/director` (demo control), `/upload`, `/review/:plan`, `/history` |
| `services/api` | One Node 22 + Fastify server: plans, runs, events, live stream, uploads, search, copilot |
| `packages/schemas` | The shared data formats (Zod → JSON Schema). Every part has one stable `part_id` used everywhere |
| `packages/project-model` | Pure logic: replay events into state, step order, the plan checker |
| `knowledge` | Elasticsearch mappings, Agent Builder tools, the `log_issue` Workflow, ES\|QL |
| `data/fixtures` | The contract between TypeScript and C#: plans, events and the states they must produce |
| `data/demo` | Test data only (the desk plan, seeds, documents): loaded by the tests and simulations, never by the app |
| `docs` | Blueprint, team plan, build plan, critique |

## Two ideas hold it together
1. **One plan format.** Every design Kit builds is a normal plan. The headset, the web viewer, search and the copilot all read it. The app starts with nothing built.
2. **Build state is never stored.** Every change is an appended event; state is a replay of the log. That gives rewind, replay, planned-versus-actual and analytics for free. Disk is the record; Elasticsearch is a rebuildable index.

## Run it
```bash
pnpm install
cp .env.example .env.local        # fill in API_TOKEN at least
pnpm dev                          # API on http://127.0.0.1:8080
pnpm -F @cutonce/web dev          # web app with a proxy to the API
pnpm test && pnpm typecheck
```
Headset app (Unity closed): `pnpm quest:check`, `pnpm quest:build`, `pnpm quest:install`.
Useful: `pnpm pm validate <plan.json>`, `pnpm gen:fixtures`, `pnpm sync:fixtures`, `pnpm reindex`, `pnpm search:eval`, `pnpm serve:local` + `pnpm tunnel`, `pnpm backup`.
Put it online (laptop + Cloudflare tunnel, no VM): `infra/README.md`. Elasticsearch: `knowledge/README.md`.

Quest changes must preserve passive vision, Kit responses, and blueprint creation. Run `pnpm quest:pillars` and follow
[`docs/quest-three-pillars.md`](docs/quest-three-pillars.md) before changing vision, AI/provider, networking, schemas,
analytics, or data-pipeline code.

## Honest labels
**Live:** the aligned hologram, part states, the event log and rewind, the copilot, the camera check, search.
**Precomputed by our own pipeline, then replayed:** the desk test plan, read from its drawings and reviewed by a person.
**Vision, not built:** whole-building drawings to accurate 3D, electrical drawings to routes, site-scale tracking.

Credits and licences: `SOURCES.md`. Codex log: `CODEX_LOG.md`.
