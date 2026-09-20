# Quest Mac runner

The `Quest Mac build` workflow runs the repository's setup, readiness/EditMode
checks, and APK build on an already licensed Mac. It uploads logs, test results,
generated XR settings, the package lock, and the APK. It does not merge commits.

## One-time Mac setup

Use the macOS account signed into Unity Hub. Install the exact Unity version in
`apps/quest/ProjectSettings/ProjectVersion.txt`, including Android Build Support,
SDK/NDK and OpenJDK. Node.js 22+ and Git LFS must be on PATH. Open Unity once to
confirm its license works. No license credentials need to leave the Mac.

A repository administrator must obtain a registration token under
[Settings → Actions → Runners → New self-hosted runner](https://github.com/michaelmazilu/kitbash/settings/actions/runners/new).
Select macOS and the Mac's architecture. The setup script downloads the runner
itself; only the token in GitHub's `config.sh` command is needed. Tokens expire
after one hour. Enter it into the hidden terminal prompt, never into chat.

From this repository on the Mac:

```bash
git pull --ff-only origin main
bash tools/quest/setup-mac-runner.sh
```

The script downloads the official runner, verifies its published checksum, and
registers it with label `cutonce-unity`. It uses `~/actions-runner-cutonce/_work`
for a separate checkout, leaving your existing Unity project alone. Keep the
terminal open and Mac logged in, on power, with its lid open. `caffeinate` prevents
idle sleep; it does not overcome lid-close sleep. Ctrl-C stops the runner. Run the
same script again to start an already registered runner. No background service
or automatic login is installed.

## Request a build

After the workflow is on the default branch, a collaborator with write access
can dispatch a trusted branch through Actions → Quest Mac build → Run workflow,
or:

```bash
gh workflow run quest-mac.yml --repo michaelmazilu/kitbash --ref YOUR_BRANCH
gh run list --repo michaelmazilu/kitbash --workflow quest-mac.yml --limit 5
gh run view RUN_ID --repo michaelmazilu/kitbash --log-failed
gh run download RUN_ID --repo michaelmazilu/kitbash
```

Only dispatch code you trust: this is a public repository, and jobs run as your
Mac user. There are intentionally no push or pull-request triggers. Do not add
fork PR triggers or run unreviewed contributions here. Repository write access
allows manual dispatch; a dedicated build account/Mac is preferable if that
collaborator set is broader than people you trust with this machine.

Jobs are serialized, and a newer dispatch does not cancel an active Unity build.
Do not open the runner's `_work` project in Unity while a job is running. The first
run imports packages/assets and can be slow. A runner that is offline leaves jobs
queued; it cannot build while the Mac is asleep.

Review and commit generated settings/lock-file changes from diagnostics when
needed; CI does not commit them automatically. Build evidence applies to the
tested commit plus the captured setup-generated settings. A successful build is
not a headset visual test. Live depth, passthrough appearance, and performance
still require a Quest. Simulator GUI testing is not part of this batch workflow.
