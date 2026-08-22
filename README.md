# BertCut

[![Latest release](https://img.shields.io/github/v/release/robgwalsh/bertcut?label=release&color=1f6feb)](https://github.com/robgwalsh/bertcut/releases/latest)
[![Unstable](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fapi.github.com%2Frepos%2Frobgwalsh%2Fbertcut%2Freleases%2Ftags%2Funstable&query=%24.name&label=unstable&color=d29922)](https://github.com/robgwalsh/bertcut/releases/tag/unstable)
[![Unstable build](https://img.shields.io/github/actions/workflow/status/robgwalsh/bertcut/unstable.yml?branch=main&label=build)](https://github.com/robgwalsh/bertcut/actions/workflows/unstable.yml)

This application endeavors to be the fastest way to cut, crop, and/or overlay video. I'm making this because I couldn't find a free application where the usability of these basic features weren't unbearably burdened by a bloated feature set.

## Install

```powershell
winget install RobWalsh.BertCut
```

* **Or** grab the [latest installer](https://github.com/robgwalsh/bertcut/releases/latest/download/BertCut-win-Setup.exe) and run it. It installs per-user, with no admin prompt.
* **Or** download the [latest portable build](https://github.com/robgwalsh/bertcut/releases/latest/download/BertCut-win-Portable.zip) if you'd rather not install anything.

FFmpeg is inside the package — there is nothing else to install, and BertCut decodes and exports
with exactly the build it was tested against. That is most of why the download is large.

Once installed, BertCut keeps itself up to date from
[GitHub Releases](https://github.com/robgwalsh/bertcut/releases): it checks on startup and applies
the update when you close the app. Your sessions and key bindings live in `%USERPROFILE%\.bertcut`
and are untouched by an update.

### Unstable

The latest code in `main`, rebuilt and published on every push. It gets the same test suite a
release gets and none of the settling time.

* Grab the [unstable installer](https://github.com/robgwalsh/bertcut/releases/download/unstable/BertCut-unstable-Setup.exe), or the [unstable portable build](https://github.com/robgwalsh/bertcut/releases/download/unstable/BertCut-unstable-Portable.zip).

It **replaces an installed copy** rather than sitting beside it, and from then on that copy updates
along unstable instead of along releases. The title bar says which you are on — `BertCut 1.1.3-unstable.42`
against `BertCut 1.1.2`. Your data is untouched either way, and running the
[stable installer](https://github.com/robgwalsh/bertcut/releases/latest/download/BertCut-win-Setup.exe)
over the top puts you back.

## Layout

```
src/BertCut.Core     timeline model, edits, export planner, audio correlation
src/BertCut.Media    ffmpeg decode, probing, preview compositing, audio playback,
                     export process driving
src/BertCut.App      WPF shell, timeline, settings screen
tests/               303 tests; the ffmpeg-dependent ones skip if it isn't installed
```

`Core` deliberately depends on nothing but the BCL, so ripple-delete arithmetic, the
timeline↔source mapping, and every ffmpeg argument are testable headlessly.

## Building and running from source

Windows and the .NET 10 SDK. `tools/ffmpeg/` is not in the repository — it is ~200 MB and
`tools/fetch-ffmpeg.ps1` reproduces it.

```powershell
.\tools\fetch-ffmpeg.ps1          # one-time, ~200 MB
dotnet build .\BertCut.slnx
dotnet test  .\BertCut.slnx       # the ffmpeg-dependent tests skip if fetch-ffmpeg.ps1 has not been run
dotnet run --project src\BertCut.App
```

Build the solution rather than a csproj: the solution pins `x64`, which the 64-bit-only FFmpeg
libraries require.

You can also pass a file directly, or associate `.mp4` with the installed `BertCut.exe`:

```powershell
dotnet run --project src\BertCut.App -- "C:\recordings\demo.mp4"
```

See [docs/build-and-release.md](docs/build-and-release.md) for packaging, the tag-driven release
workflow, and how updates reach installed copies.

## Testing the interface

`dotnet test` covers everything below the window. The window itself is driven by a harness
that hosts the real `MainWindow` in-process, parked offscreen and refused activation, so a
run cannot appear over what you are doing or take the keyboard from you:

```powershell
dotnet build .\BertCut.slnx
.\tools\BertCut.Harness\bin\x64\Debug\net10.0-windows\BertCut.Harness.exe --script .\tools\ui\smoke.bcs
```

It opens a synthesised clip, marks a range, ripples it away, and writes a PNG at each step —
each one printed as `SHOT <path>`. Scripts are a line per command (`open`, `key I`, `goto 90`,
`shot`, `state`, `assert-status …`); `--help` lists them. Keystrokes are resolved through the
real key map and dispatched through the same entry point the toolbar uses, so no operating
system input is synthesised and nothing competes with the desktop. `tools/ui/sync.bcs` does
the same for the two-angle overlay sync.

A run is also silent: preview audio goes to a sink that consumes it at real time and discards
it, so the whole playback path runs without anything coming out of the speakers. `--audio`
opts in.

Because the app restores a session by content key, each run is given its own state directory
through `BERTCUT_STATE_DIR` and cannot disturb `%USERPROFILE%\.bertcut`.

## Licence

MIT. See [LICENSE](LICENSE).

The installer and the portable build bundle [FFmpeg](https://ffmpeg.org), unmodified, under the
LGPL: BtbN's `win64-lgpl-shared` build of the n8.1 line, whose licence travels alongside the
binaries as `ffmpeg\LICENSE.txt`. Sources and the build recipe are at
[BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), and `tools/fetch-ffmpeg.ps1` is the
exact fetch this repository performs.
