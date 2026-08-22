# Build and release

How BertCut is built, packaged, and shipped. The short version: **push a tag, CI does the rest.**
Everything below is the detail behind that.

## Prerequisites

| For | Install |
|---|---|
| Building and testing | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then `tools\fetch-ffmpeg.ps1` |
| Building an installer locally | `dotnet tool install -g vpk` (Velopack CLI) |
| Publishing a release | Nothing — GitHub Actions does it |
| Updating the winget package by hand | `winget install wingetcreate` |

## Build and test

```powershell
dotnet build C:\Source\bertcut\BertCut.slnx
dotnet test  C:\Source\bertcut\BertCut.slnx
```

Three things about this build worth knowing:

- **Build the solution, not a csproj.** The solution pins `x64`, so it builds to `bin\x64\Debug\`;
  building a csproj on its own defaults to `AnyCPU` and writes to `bin\Debug\`. Anything that has
  to publish a csproj directly — the workflows, `pack.ps1` — passes `-p:Platform=x64` for the same
  reason.
- **`Directory.Build.props` sets `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild`** for every
  project, so a clean build is a warning-free build. A warning that CI hits is a failed release,
  not a nag.
- **Never `dotnet run` the app** — see the rule in [CLAUDE.md](../CLAUDE.md). The harness hosts the
  same window offscreen.

If a build fails with MSB3021/MSB3026 because a running instance is holding `bin\x64\Debug`, kill
it and rebuild:

```powershell
Get-Process BertCut | Stop-Process -Force
```

## What a release actually is

`dotnet publish -r win-x64 --self-contained` produces a build with the .NET runtime inside it, and
[Velopack](https://velopack.io) (`vpk`) turns that directory into the release artifacts:

| Asset | What it is |
|---|---|
| `BertCut-win-Setup.exe` | The installer. Per-user, into `%LOCALAPPDATA%\BertCut`, no admin prompt to install. |
| `BertCut-win-Portable.zip` | The same build, unzip and run, nothing installed. |
| `BertCut-<version>-full.nupkg` | Full update package, used by installed copies with no matching delta. |
| `BertCut-<version>-delta.nupkg` | Only what changed since the previous release — the usual update path, a few MB instead of the whole thing. |
| `releases.win.json`, `RELEASES` | The update feed installed copies read. |

**The version comes from the tag and nothing else.** No project file carries a version; CI strips
the leading `v` and passes it as `-p:Version=` to `dotnet publish` and `--packVersion` to `vpk`.
There is nothing to bump before tagging.

Versioning is semver as users would read it: a feature release bumps the minor, a fix-only release
bumps the patch.

## FFmpeg is inside the package

This is the part that is not like a normal WPF app, and the part that will bite.

`BertCut.App.csproj` copies `tools\ffmpeg\**` to `ffmpeg\` beside the executable, which is
`FfmpegRuntime.CandidateDirectories`' first probe — so a publish is already the right shape and
`vpk pack --packDir publish` picks the whole thing up with no extra step. One build serves both
engines: the shared libraries are what the in-process decoder loads, `ffmpeg.exe` and `ffprobe.exe`
are what import and export shell out to, and using one for both is what keeps the preview and the
exported file agreeing at the codec level.

Consequences, all of them load-bearing:

- **`tools/ffmpeg/` is gitignored, so CI has to fetch it.** Without it the `None` glob matches
  nothing, the publish *succeeds*, and the installer that comes out has no decoder in it. There is
  no error anywhere in that sequence. `pack.ps1` asserts on `publish\ffmpeg\ffmpeg.exe` afterwards
  for exactly this reason.
- **The fetch runs before `dotnet test`, not just before the publish.** The ffmpeg-dependent tests
  skip silently when it is absent, so a release would otherwise be cut against a fraction of the
  suite and look green doing it.
- **The workflows cache `tools/ffmpeg` on a hand-versioned key** (`ffmpeg-n8.1-win64-lgpl-shared-v1`).
  BtbN publishes to a floating `releases/download/latest/` tag, so there is no content hash to key
  on; bump the `-v1` suffix to take a newer build of the same line. `fetch-ffmpeg.ps1` is a no-op
  on a cache hit, which is what its `.version` marker is for.
- **The package is ~140 MB of which most is FFmpeg**, and those DLLs are byte-identical between
  releases. So a working delta is a few MB and a broken one is the whole package — which makes
  `vpk download` finding the previous release matter far more here than it would elsewhere. Check
  the delta size on every release.
- **The build is LGPL-shared, deliberately**, and redistributable: NVENC/NVDEC/CUVID come from the
  MIT-licensed nv-codec-headers and are in every BtbN variant, so the LGPL build is
  hardware-accelerated *and* shippable. `LICENSE.txt` is copied alongside the binaries by
  `fetch-ffmpeg.ps1` and travels into the package with them. The source and build recipe are at
  <https://github.com/BtbN/FFmpeg-Builds>.

## Ship a new version

```powershell
git tag v1.2.3
git push origin v1.2.3
```

Tag a commit that's already pushed to `main` — the tag is what the workflow builds, so anything not
on the branch simply isn't in the release.

**Run `dotnet test` before you tag.** The workflow runs the suite and a single red test fails the
release *after* the tag exists, which is the annoying case (see [Recovering a failed
release](#recovering-a-failed-release)).

That push triggers `.github/workflows/release.yml`, which on `windows-latest`:

1. Derives the version from the tag (`v1.2.3` → `1.2.3`).
2. Restores or fetches `tools/ffmpeg`.
3. `dotnet test -c Release` — a failure stops everything here.
4. `dotnet publish -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:Version=…`.
5. `vpk download github` — pulls the **previous** release so the next step can build a delta
   against it. This fails harmlessly on the very first release; the script logs it and carries on
   with a full package only.
6. `vpk pack` — builds the installer, portable zip, and packages.
7. `vpk upload github --publish` — creates the GitHub Release, tagged with the tag you pushed and
   named `BertCut <version>`, and uploads every asset.

A second job then opens the [winget](#winget) pull request for the version just published.

Watch it and confirm:

```powershell
gh run watch --exit-status                 # or: gh run list --workflow=release.yml
gh release view v1.2.3 --json assets -q '.assets[].name'
```

A healthy release has six assets, and the delta package should be small. A delta the same size as
the full package means step 5 didn't find the previous release.

### Release notes

`vpk upload` publishes with an empty body, so notes are added afterwards:

```powershell
gh release edit v1.2.3 --notes-file notes.md
```

Write them from what changed for a *user*, grouped by feature area rather than by commit.
`git log --oneline v1.1.0..v1.2.3` and the compare link
(`https://github.com/robgwalsh/bertcut/compare/v1.1.0...v1.2.3`) are the starting point.

### Recovering a failed release

The tag is the trigger, so a failed run is fixed by fixing the problem and re-tagging the same
version — as long as nothing was published yet:

```powershell
git tag -d v1.2.3
git push origin :refs/tags/v1.2.3     # delete the remote tag
# fix, commit, push to main, then tag again
```

If a run failed *after* the release was created, delete the release too
(`gh release delete v1.2.3 --cleanup-tag`) rather than leaving a half-uploaded one for installed
apps to find. For a run that failed on something transient, `gh run rerun <id>` is enough.

## The unstable channel

`main` also ships. `.github/workflows/unstable.yml` runs on **every push to `main`** (and on
`workflow_dispatch`), and does what the release workflow does with four differences: the version,
the channel, the release it publishes to, and no winget.

| | Release | Unstable |
|---|---|---|
| Trigger | push tag `v*` | push to `main` |
| Version | the tag, minus `v` | last tag + `0.0.1`, plus `-unstable.<run number>` |
| Velopack channel | `win` (the default) | `unstable` |
| Feed file | `releases.win.json` | `releases.unstable.json` |
| Installer | `BertCut-win-Setup.exe` | `BertCut-unstable-Setup.exe` |
| GitHub release | one per version, tag `vX.Y.Z` | one rolling **pre-release**, tag `unstable` |
| Deltas | yes | yes |
| winget | yes | no |

**The two cannot reach each other**, which is the property worth protecting, and it is guarded twice
over. A release copy passes `prerelease: false`, so the unstable pre-release is not even in the list
of releases it considers; and Velopack asks each release in that list for `releases.<channel>.json`
and **skips any release that hasn't got one**, so the feeds would not cross even if a release did
turn up. That second guard is what makes the reverse direction safe too — an unstable copy lists
stable releases and skips every one of them, rather than being confused by a newer stable release.
On top of that, GitHub's `latest` excludes pre-releases, so the README's stable download links never
resolve to an unstable build, and this workflow has no winget job, so nothing off `main` is ever
offered to winget.

`1.1.3-unstable.42` reads as "heading for 1.1.3, build 42". It sorts *below* `1.1.3` — a SemVer
pre-release always does — and above `1.1.3-unstable.7`, because SemVer compares dot-separated
numeric identifiers numerically. That is why the run number is a separate identifier rather than
glued on.

Things about the **rolling tag** worth knowing before changing any of it:

- **The release is deleted and recreated on every run**, rather than merged into: `vpk upload`
  refuses to add a second `releases.unstable.json` to a release that already has one, and GitHub
  ignores `target_commitish` for a tag that already exists — so the tag has to be deleted with it
  or the release keeps pointing at the old commit. The download URL 404s for the couple of minutes
  an upload takes; a copy that checks in that window finds no feed and quietly does nothing.
- **Deletion goes by release id, not by tag, and pushes queue rather than cancel.** Both guard the
  same failure: a run interrupted mid-upload strands a *draft* release holding the `unstable` tag.
  A draft has no tag, so `gh release delete unstable` cannot see it — while vpk's own collision
  check can, and refuses to publish. Left alone that wedges the channel until someone clears it by
  hand.
- **Deltas still work**, and matter more here than on a release, since an unstable copy updates on
  every push and most of the package is FFmpeg that did not change. The delta is built at pack time
  against the package `vpk download` pulls from the release that is still up, and a client applies
  it against its own local package — neither has anything to do with the release being replaced
  afterwards. `--pre` on that download is what makes it find anything at all, the feed living on a
  pre-release; without it the delta silently comes out full-sized.
- **The fixed tag is the only way the README can link to "the current unstable build".** There is
  no `releases/latest/download/...` form for a pre-release.

The badge is the fiddly part, and two obvious approaches are both dead ends. `github/v/release` with
`?include_prereleases` renders the **tag name** — for a rolling tag that is the constant string
`unstable`, no version in it at all. And shields' `endpoint` route **blocks `github.com` outright**,
so serving it a `badge.json` uploaded as a release asset renders `domain is blocked`, permanently.
What works is `dynamic/json` against `api.github.com`, reading the release's `name` — which is why
the workflow names the release the bare version rather than `BertCut <version>`.

### The app knows which channel it is on

`ReleaseChannel.IsUnstable` (in Core, with tests) reads it off the build's own version string, and
`UpdateService` uses it for the one flag that differs — whether `GithubSource` looks at
pre-releases. Both directions are load-bearing: an unstable build that refused pre-releases could
never see its own feed, and a release build that accepted them would be handed the unstable
pre-release, which carries no `releases.win.json`. The version is also what the title bar shows, so
which channel a copy is on is visible without digging.

**Unstable replaces a release install rather than sitting beside it** — same `packId`, so same
install directory. That is deliberate: two copies would share `%USERPROFILE%\.bertcut` and fight
over the same sessions and caches. Running the release `Setup.exe` over an unstable install puts it
back on the release channel.

## Build an installer locally

```powershell
scripts\pack.ps1 -Version 1.2.3     # fetches ffmpeg, tests, publishes, packs; output in Releases\
```

Same steps CI runs, minus the upload. Running it again with a higher version against the same
`Releases\` directory produces a delta against what's already there, which is how the delta path
gets exercised without publishing anything.

`publish\` and `Releases\` are both gitignored, anchored with a leading slash so neither pattern can
match a source folder deeper in the tree.

`pack.ps1` packs the release channel. To exercise the unstable one, pass `vpk` the two extra
arguments CI passes — a pre-release version and the channel:

```powershell
vpk pack --packId BertCut --packVersion 1.1.3-unstable.42 --packDir publish `
  --mainExe BertCut.exe --channel unstable --delta None
```

That is also how to check the channel plumbing without publishing anything: pack `-unstable.1` and
`-unstable.2` into a `Releases\` directory, install the first, point `BERTCUT_UPDATE_URL` at that
directory, and confirm it finds the second.

## How updates reach users

`UpdateService` (in `src/BertCut.App`) checks GitHub Releases on startup, on a background thread,
and swallows every failure — a broken check must never take the editor down mid-edit, and the next
launch retries. Behaviour worth remembering:

- **Updates are mandatory.** A newer release is downloaded and staged with
  `WaitExitThenApplyUpdates`, so it applies when the app closes whether or not the user takes the
  "Restart now?" prompt. An editing session is long, and that prompt can sit unanswered for an hour.
- **A release build ignores pre-releases**, an unstable build reads them — that one flag is the
  whole difference, and it comes from the build's own version (see [The unstable
  channel](#the-unstable-channel)). Note the consequence: a GitHub pre-release is **not** a quiet
  way to stage something, because the rolling `unstable` pre-release is what every unstable copy is
  watching. Use a draft release for that.
- **Dev builds and harness runs never update.** `_manager.IsInstalled` is false under `dotnet run`
  and in the harness, so the check returns immediately. `VelopackApp.Build().Run()` is only reached
  through the app's own `Main`, which the harness never calls.
- **This is the app's only network access** apart from what the user's own media pulls in.

To exercise the real update flow against a local build, point the *installed* app at your
`Releases\` directory:

```powershell
$env:BERTCUT_UPDATE_URL = "C:\Source\bertcut\Releases"
```

Any static file host works as the value; it's read once, in the `UpdateService` constructor.

### Data must survive an update

The Velopack install directory is `%LOCALAPPDATA%\BertCut`, and **the installer deletes it** on
install and on every update. That is exactly where BertCut's sessions, key bindings and caches used
to live, so state now lives in `%USERPROFILE%\.bertcut\` (`sessions\`, `controls.json`, `cache\`)
and never in the install directory. `AppPaths.MigrateLegacyData` moves what builds before this left
behind; don't remove it, and don't add anything new under `%LOCALAPPDATA%`.

`BERTCUT_STATE_DIR` still relocates the whole root, which is how the harness stays out of the user's
editor — and the migration is skipped entirely when it is set, so a scripted run cannot reach into
the real profile in either direction.

`FfmpegRuntime` probes `%USERPROFILE%\.bertcut\ffmpeg` for a copy the user installed themselves,
ahead of the old `%LOCALAPPDATA%\BertCut\ffmpeg`, for the same reason: an ffmpeg put in the install
directory would survive exactly until the next update.

## winget

The package is `RobWalsh.BertCut`, published in
[`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs).

**This is automated from the second version on.** The `winget` job in `release.yml` runs after the
release is published and opens a pull request at `microsoft/winget-pkgs` for the new version. The PR
is merged by winget's own validation pipeline, usually within a few hours.

**The first version is not**, because the action updates an existing package rather than creating
one: with nothing upstream to build from it fails with `Package RobWalsh.BertCut does not exist in
the winget-pkgs repository`, which is what happened on v0.1.0. Submit that one by hand — the
manifests as sent are kept in `manifests/r/RobWalsh/BertCut/`, and `winget validate --manifest <dir>`
checks them before they go.

The job needs two things:

1. A fork of `microsoft/winget-pkgs` named exactly `winget-pkgs` under the account — the action
   pushes manifest branches to it. Create with `gh repo fork microsoft/winget-pkgs --clone=false`.
2. A **classic** PAT with only the `public_repo` scope, as the `WINGET_PAT` secret — fine-grained
   PATs can't open PRs against repos you don't own. Create one
   [here](https://github.com/settings/tokens/new?scopes=public_repo&description=winget-releaser),
   then `gh secret set WINGET_PAT --repo robgwalsh/bertcut`. **A classic PAT expires**, and the
   symptom is this job failing on a release that otherwise went fine.

A failure here never affects the release — the assets are already published and installed copies
update themselves regardless. `gh run rerun <id>` is usually enough; winget lag only delays
first-time installers.

### The first manifest is the template for every one after it

The action generates each version's manifest from **the newest one already published upstream**,
changing the version, URL and hash. So anything wrong up there propagates forward instead of being
corrected, and anything worth having has to be put there once, by hand.

Get these right on the first submission:

- `InstallerType: exe`, `Scope: user` (per-user install into `%LOCALAPPDATA%`, no elevation),
  `InstallerSwitches.Silent: --silent` (Velopack's flag), `UpgradeBehavior: install`. Describing a
  Velopack `Setup.exe` as `InstallerType: portable` makes winget shim the installer as if it were
  the app.
- `AppsAndFeaturesEntries` with `ProductCode: BertCut` — the HKCU uninstall key Velopack writes —
  plus DisplayName `BertCut` and Publisher `Rob Walsh`. Without it `winget upgrade` can't match an
  installed copy. **No `DisplayVersion`**, deliberately: Velopack's ARP version always equals the
  package version, so leaving it out lets winget compare against `PackageVersion` rather than a
  field that would go stale on every automated update.
- A filled-in locale manifest (publisher/package URLs, license URL, description, tags).

`C:\Source\bertbrowser\manifests\r\RobWalsh\BertBrowser\1.1.1\` is a worked example of all of that.

Submitting a version by hand (if the job is broken, or to change metadata):

```powershell
wingetcreate update RobWalsh.BertCut --version 1.2.3 `
  --urls https://github.com/robgwalsh/bertcut/releases/download/v1.2.3/BertCut-win-Setup.exe `
  --submit
```

Validate anything hand-written before submitting — `winget validate --manifest <dir>` catches schema
errors that would otherwise come back as a failed check on the PR.

## Release checklist

- [ ] `main` is green: `dotnet test BertCut.slnx -c Release`
- [ ] Working tree clean, `main` pushed
- [ ] Pick the version (semver against the last tag)
- [ ] `git tag vX.Y.Z && git push origin vX.Y.Z`
- [ ] Both jobs succeed; release has six assets and a small delta
- [ ] Release notes written and attached
- [ ] Install or upgrade a real copy, launch it, and open a video — the FFmpeg-in-the-package
      failure looks fine until you do
- [ ] The winget PR is open at `microsoft/winget-pkgs` (the `winget` job opens it; nothing to do
      unless it failed)
