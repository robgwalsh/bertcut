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
src/BertCut.Core     timeline model, edits, export planner, audio correlation
src/BertCut.Media    ffmpeg decode, probing, preview compositing, audio playback,
                     export process driving
src/BertCut.App      WPF shell, timeline, settings screen
tests/               206 tests; the ffmpeg-dependent ones skip if it isn't installed
```

`Core` deliberately depends on nothing but the BCL, so ripple-delete arithmetic, the
timeline↔source mapping, and every ffmpeg argument are testable headlessly.

## Three things worth knowing

**Variable frame rate is handled up front.** OBS, ShareX, and the Windows capture tools
routinely emit VFR, where a frame's timestamp is *not* `index / fps`. On import a single
non-decoding `ffprobe` pass records the real timestamp of every frame, so every conversion
above the decoder is an array lookup and VFR stops existing as a concept.

**Preview and export derive from one place.** `RenderPlan` flattens the document into spans
that the ffmpeg argument builder consumes; `TimelineResolver` answers the same question
per-frame for the compositor. A property test asserts the two agree on every frame of
randomly generated projects — that test is the WYSIWYG guarantee.

**Overlays can line themselves up by ear.** Film an event from two angles, put both takes in
one recording, cut the second angle out and drop it over the first as a picture-in-picture:
press `A` and it slides into sync by correlating what the two cameras heard. The subtlety is
that both angles usually live in the *same file*, so the base window matches itself at a
perfect score as well as matching the real second angle — the useless answer outranks the
right one. `AudioSync` refuses any offset overlapping the region the reference came from,
which is the whole difference between the feature working and appearing to do nothing.

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
system input is synthesised and nothing competes with the desktop. `tools/ui/sync.bcs` does
the same for the two-angle overlay sync.

A run is also silent: preview audio goes to a sink that consumes it at real time and discards
it, so the whole playback path runs without anything coming out of the speakers. `--audio`
opts in.

Because the app restores a session by content key, each run is given its own state directory
through `BERTCUT_STATE_DIR` and cannot disturb `%LOCALAPPDATA%\BertCut`.

## Licence

MIT. See [LICENSE](LICENSE).
