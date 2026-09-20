# kitbash.vercel.app

The public page that explains how Kitbash works, for judges and for the Devpost link. Plain HTML, CSS
and SVG: no build step, no API calls, no login. It cannot reach the Director, the uploads or any key.

- `demo/index.html` — the page, served at `/demo`
- `index.html` — sends anyone who lands on the bare domain to `/demo`
- `vercel.json` — the redirect, clean URLs and two safety headers

## Live at https://kitdemo.vercel.app

`kitbash.vercel.app` was already taken by someone else's project, so the address is
**kitdemo.vercel.app**. The bare address redirects to `/demo`.

It is deployed straight from this folder with the Vercel CLI, as the project `kitbash` under the
account `michaelmazilu08-2683`. It is **not** connected to GitHub, so pushing does not redeploy it.
After changing anything here:

```bash
cd site && npx vercel --prod
```

Every production deploy is aliased to kitdemo.vercel.app on its own, because that domain belongs to
the project. To make pushes deploy it instead, open the `kitbash` project on vercel.com, then
Settings → Git, connect `michaelmazilu/cut-once` and set the Root Directory to `site`.

The CLI leaves `.vercel/` and `.env.local` in this folder; `.gitignore` here keeps both out of git.

## The simulator

`/demo` runs the whole flow in the browser, so the page demonstrates Kitbash instead of only
describing it. It plays itself when the scene scrolls into view, and again on "Play the demo".

| File | What it is |
|---|---|
| `demo/sim/pipeline.js` | The server's rules, ported: surfaces, objects, measured sizes, designs, the balance check, the placement check. No three.js in here. |
| `demo/sim/sim.js` | The kitchen scene, the ray sweep, the holograms, dragging, and the demo that plays itself. |
| `demo/vendor/` | three.js r171 and OrbitControls, vendored so the page needs no CDN. |

**Real:** the 128 × 96 ray grid cast from the camera through the view, grouping the hits into a
surface and separate objects, the sizes those rays measure, the stacking and balance rules, and
finishing a step only when the object is standing inside its hologram. Press "Shuffle" and every
object moves, so nothing is placed in advance.

**Not real, and the page says so:** the rays hit a 3D scene, so the depth is exact instead of a
noisy sensor; the names come from the scene rather than a model reading a photo; and Kit's lines
are written rather than generated.

From the browser console: `__kitbash.scanNow()`, `.twins`, `.ideas`, `.chosen`, `.play()`, and
`.snapshot()`, which renders a frame and counts how many pixels are lit, cyan and green.

## Look at it locally

```bash
python3 -m http.server 5183 --directory site
```

Then open http://127.0.0.1:5183/demo/ (locally the path needs the trailing slash; on Vercel `/demo`
works because of `cleanUrls`).

## Adding the demo video

Drop the recording in as `demo/kitbash.mp4` with a `demo/poster.jpg`, then uncomment the "See it run"
block near the bottom of `demo/index.html`.

## Keeping it honest

Every number on the page comes from the code: 128 × 96 rays (`BuildScanCapture.cs`), 1,024 rays per
frame, 60 points and 45 cm for a surface and 8 points and 1.5 cm for an object (`build/twins.ts`), up
to 24 objects named in one call (`build/label.ts`), and 2 mm where pieces meet (`build/solver.ts`).
If any of those change, change them here too.
