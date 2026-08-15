# BertCut

This application endeavors to be the fastest way to cut, crop, and/or overlay video. I'm making this because I couldn't find a free application where the usability of these basic features weren't unbearably burdened by a bloated feature set.

## Running it

```powershell
.\tools\fetch-ffmpeg.ps1          # one-time, ~200 MB
dotnet run --project src\BertCut.App
```

You can also pass a file directly, or associate `.mp4` with `BertCut.App.exe`:

```powershell
dotnet run --project src\BertCut.App -- "C:\recordings\demo.mp4"
```

## Layout

```
src/BertCut.Core     timeline model, edits, export planner
src/BertCut.Media    ffmpeg decode, probing, preview compositing, export process driving
src/BertCut.App      WPF shell, timeline, settings screen
tests/               158 tests; the ffmpeg-dependent ones skip if it isn't installed
```

`Core` deliberately depends on nothing but the BCL, so ripple-delete arithmetic, the
timeline↔source mapping, and every ffmpeg argument are testable headlessly.

## Two things worth knowing

**Variable frame rate is handled up front.** OBS, ShareX, and the Windows capture tools
routinely emit VFR, where a frame's timestamp is *not* `index / fps`. On import a single
non-decoding `ffprobe` pass records the real timestamp of every frame, so every conversion
above the decoder is an array lookup and VFR stops existing as a concept.

**Preview and export derive from one place.** `RenderPlan` flattens the document into spans
that the ffmpeg argument builder consumes; `TimelineResolver` answers the same question
per-frame for the compositor. A property test asserts the two agree on every frame of
randomly generated projects — that test is the WYSIWYG guarantee.

## Building

Windows and the .NET 10 SDK. `tools/ffmpeg/` is not in the repository — it is ~200 MB and
`tools/fetch-ffmpeg.ps1` reproduces it exactly.

```powershell
dotnet build
dotnet test        # the ffmpeg-dependent tests skip if fetch-ffmpeg.ps1 has not been run
```

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
system input is synthesised and nothing competes with the desktop.

Because the app restores a session by content key, each run is given its own state directory
through `BERTCUT_STATE_DIR` and cannot disturb `%LOCALAPPDATA%\BertCut`.

## Licence

MIT. See [LICENSE](LICENSE).
