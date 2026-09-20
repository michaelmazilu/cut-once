# kitbash.vercel.app

The public page that explains how Kitbash works, for judges and for the Devpost link. Plain HTML, CSS
and SVG: no build step, no API calls, no login. It cannot reach the Director, the uploads or any key.

- `demo/index.html` — the page, served at `/demo`
- `index.html` — sends anyone who lands on the bare domain to `/demo`
- `vercel.json` — the redirect, clean URLs and two safety headers

## Deploy

On vercel.com: **Add New → Project**, import `michaelmazilu/cut-once`, then set

| Setting | Value |
|---|---|
| Framework Preset | Other |
| Root Directory | `site` |
| Build Command | leave empty |
| Output Directory | leave empty |

Deploy, then rename the project to `kitbash` under Settings → General so the address is
`kitbash.vercel.app`. Every push to `main` redeploys it.

With the Vercel CLI instead: `cd site && npx vercel --prod`.

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
