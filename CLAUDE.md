# BertCut

A WPF video editor: cut, crop, overlay. .NET 10, x64 only, Windows only.

```
src/BertCut.Core     timeline model, edits, export planner, key map, audio correlation
                                                                      (net10.0, BCL only)
src/BertCut.Media    ffmpeg decode, probing, preview compositing, PNG,
                     audio decode and WASAPI playback                 (net10.0-windows)
src/BertCut.App      WPF shell, timeline, settings screen
tools/BertCut.Harness  drives the real UI offscreen — see below
tests/BertCut.Core.Tests
```

## Audio

Sound is decoded through `BertCut.Media.Decode.AudioDecoder` (the counterpart to
`VideoDecoder`: addressed by seconds rather than frames, resampled to the project's rate and
to stereo) and played by `Audio.AudioPlayer` through an `IAudioOutput` — WASAPI in the app,
`SilentAudioOutput` in the harness.

Three things are worth knowing before touching any of it:

- **The audio device is the master clock.** At 1x forward `EditorViewModel.Tick` takes the
  playhead from `AudioPlayer.PositionFrames` rather than from its stopwatch, so the picture
  follows the sound. Sound cannot be nudged a frame to catch up without a click; a video frame
  can be dropped without anyone noticing. Reverse and 2x-8x are silent and fall back to the
  stopwatch.
- **The export mixes the base track and nothing else**, and the preview plays the same thing.
  `OverlayClip.Muted` is still written and still serialized, but nothing reads it: the case
  overlays exist for is one event filmed twice, and summing two near-identical tracks combs.
  `ExportPlannerTests.An_overlay_contributes_no_audio_of_its_own` pins that.
- **Audio sync must exclude the identity match.** An overlay's source is usually the base's
  source — one recording holding two angles — so correlating the base window against that file
  finds the second angle *and* finds the window matching itself at a perfect 1.0. Taking the
  highest peak returns the useless one every time. See the class remarks on
  `BertCut.Core.Audio.AudioSync`, and the pair of tests in `AudioSyncTests` that hold both
  halves of it in place.

`AudioPeaksCache` keeps a 100 Hz min/max envelope per source under the state root's `cache`
directory, keyed by content key exactly as the filmstrip is. It feeds both the waveform lane
and the coarse correlation pass.

## Never launch the GUI

`dotnet run --project src\BertCut.App` — and `BertCut.App.exe` — put a real window on the
user's screen, take keyboard focus, and compete with whatever they are doing. They multitask
while you work. **Do not run either, ever**, including "just to check something quickly". The
only exception is the user explicitly asking you to launch it.

To exercise or see the UI, use the harness. It hosts the same `MainWindow`, in-process and
parked offscreen, and never reaches the user's screen.

## Building and testing

```powershell
dotnet build C:\Source\bertcut\BertCut.slnx
dotnet test  C:\Source\bertcut\BertCut.slnx
```

**Build the solution, not a csproj.** The solution pins `x64`, so it builds to
`bin\x64\Debug\`; building a csproj on its own defaults to `AnyCPU` and writes to `bin\Debug\`
instead. Do that and you will run a stale binary and conclude your change did nothing.

Warnings are errors (`TreatWarningsAsErrors`), code style is enforced in the build, nullable is
on. The solution is the new `.slnx` format. A new project needs a `<Platform Project="x64" />`
entry there.

FFmpeg lives in `tools/ffmpeg/` (gitignored, ~200 MB, reproduced by `tools/fetch-ffmpeg.ps1`).
It is already installed here. Tests that need it skip when it is absent.

## The harness

```powershell
$harness = "C:\Source\bertcut\tools\BertCut.Harness\bin\x64\Debug\net10.0-windows\BertCut.Harness.exe"

& $harness --script C:\Source\bertcut\tools\ui\smoke.bcs
& $harness -c "sample c.mp4 6; open c.mp4; goto 90; shot check"
```

Each command echoes `OK <command>`; captures print `SHOT <absolute path>` on their own line —
`Read` those paths to look at the UI. Exit codes: 0 pass, 1 a command or assertion failed,
2 the environment is wrong, 3 the watchdog fired.

```
sample <path> [seconds]     synthesise a testsrc2 clip (its frame number is printed in the
                            picture) over a tone with an aperiodic envelope
sample-angles <path> [sec]  one clip holding the same event twice: an angle, then the same
                            event again quieter and noisier — the audio-sync fixture
open|import|append <path>
key <gesture>               through the live key map:  key I  ·  key Ctrl+Z  ·  key >
intent <EditorIntent>       straight to the window's dispatch point
goto <frame> | play | stop | tick [n] | sleep <ms> | reset | close | settle [ms]
shot <name> [element]       PNG of the window, or of any x:Name'd element
dump-preview <name>.png     the composited video frame alone, no interface
state                       one JSON line: playhead, duration, marks, mode, crops, overlays,
                            overlaySourceStart, muted, status
assert-status <substring> | assert-timecode <text> | assert-frame <n>
assert-frame-between <a> <b> | assert-visible <Name> | assert-hidden <Name>
assert-overlay-source-start <a> [b]   where the overlay under the playhead reads from
assert-muted | assert-unmuted
assert-has-media | assert-no-media | assert-unlocked <path>   (exclusive open, so it
                                                               really tests the handle)
```

Options: `--out <dir>`, `--state-dir <dir>`, `--keep-state`, `--timeout <sec>`,
`--busy-timeout <ms>`, `--keep-going`, `--audio`, `--verbose`.

### Rules

**Prefer the cheapest tier.** Anything about *video* pixels — where a crop landed, which frame
shows after a ripple delete, whether an overlay is in the right place — belongs in
`tests/BertCut.Core.Tests` against `PreviewEngine.Canvas` with `Png.Save`, with no window at
all. See `PreviewEngineTests`. Use the harness for questions about the *interface*.

**A run is silent unless you ask for sound.** The harness injects a null audio sink, so the
whole playback path runs — decoders, segment boundaries, the clock the playhead follows — into
something that consumes it at real time and discards it. `--audio` lets it reach the speakers,
which is as intrusive as putting the window on screen; do not pass it without being asked to.

**Never dispatch a dialog intent.** `OpenFile`, `ImportSource`, `AppendSource` and `Export`
open a modal Win32 dialog, which is owned by the desktop and *would* appear on the user's
screen no matter where the window is parked. The harness refuses them and names the
replacement; use `open`/`import`/`append`. Export is covered headlessly by
`EndToEndExportTests`.

**Do not compare UI screenshots pixel for pixel.** Text rasterisation varies with font version
and DPI. Captures are for looking at; assert through `state`, `assert-status` and
`assert-visible`. Pixel-exact comparison is fine on `dump-preview` output, which is
ffmpeg-deterministic.

**Playback assertions must be bounded.** Playback runs off a real clock — the audio device's
own position at 1x forward, a stopwatch otherwise — so use `assert-frame-between`. For an exact
frame, `goto` there. And `sleep` does not advance playback on its own: it blocks the dispatcher
thread, so the composition tick cannot fire. Follow a `sleep` with `tick`.

## State

The app keeps sessions, key bindings, its thumbnail cache and its audio envelopes under
`%LOCALAPPDATA%\BertCut`,
autosaves every 750 ms, and restores by content key — so without isolation a test run would be
restored into the user's editor next time they opened the same file. `BERTCUT_STATE_DIR`
relocates that root (`BertCut.Core.Session.AppPaths`); the harness points it at a scratch
directory per run and deletes it afterwards. `--keep-state` keeps it, which is how session
restore gets tested across two runs.

FFmpeg discovery deliberately does *not* follow that variable — it is an installed tool, not
state.

## Measured behaviour of the offscreen window

Worth knowing, because each of these was checked rather than assumed:

- **Captures are real.** `RenderTargetBitmap` re-renders the visual tree in software, so
  window position, occlusion and whether the compositor ever presented it are irrelevant. The
  harness fails a `shot` that comes back a single flat colour.
- **Size is the client area**, ~1167x725 for the 1180x760 window. Measuring the window itself
  gives the outer size and leaves a blank strip where the title bar would be.
- **An element capture is re-hosted at the origin.** `RenderTargetBitmap.Render` applies a
  visual's offset within its parent, so rendering a child straight into a bitmap of that
  child's size draws it at its *window* coordinates — `shot x Timeline` came back blank
  because the strip sits 600 px down, and everything else came back with a margin on two
  sides and its far edges cropped. `Capture.AtOrigin` paints through a `VisualBrush` to
  normalise that, so it is still a software re-render of the live tree.
- **`CompositionTarget.Rendering` does fire** for a window that is never presented, so
  playback advances in real time — but only while the dispatcher is pumping. `sleep` is a
  `Thread.Sleep` on the dispatcher thread and blocks it, so `play; sleep 600` alone leaves
  the playhead where it was. `play; sleep 600; tick` moves it 18 frames at 30 fps, which is
  what `tick` is for: `Tick` derives position from elapsed time, so one call catches up.
- **The window reaches the foreground exactly once**, during the first layout pass, through no
  event this process is told about. `ShowActivated=false`, `WS_EX_NOACTIVATE`, disabling the
  window and refusing WPF focus all fail to prevent it. `ForegroundGuard` polls at 10 ms and
  hands the foreground straight back; sampling from another process at 15 ms sees no transition
  at all. If a run ever reports more than one correction, something new is activating it.
- **Animations are settled before a capture** — `MainWindow.SettleAnimations` clears the help
  and settings fades, so the same overlay captured twice is byte-identical.
