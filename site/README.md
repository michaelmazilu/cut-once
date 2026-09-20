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
