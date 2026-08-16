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
- **Sync runs in two directions, and `BertCut.Media.Audio.OverlaySync` is the seam.** Which of
  the two things moves depends on which is still free. `Solve` is for a *committed* clip: it is
  where the user put it, so the free variable is which of its source's frames it reads.
  `SolveTimelinePosition` is for one being *placed*: its content is what the user just chose on
  the card, so what moves is the clip, along the timeline. Reference and candidate simply swap,
  and both go through the same private `Correlate` — one copy, so the identity exclusion cannot
  be fixed in one direction and forgotten in the other. Only the second can fail with
  `MatchNotOnTimeline`, which means the sound was found on footage that has been cut away.

`AudioPeaksCache` keeps a 100 Hz min/max envelope per source under the state root's `cache`
directory, keyed by content key exactly as the filmstrip is. It feeds both the waveform lane
and the coarse correlation pass.

## Where a frame comes from

Nothing decodes on the UI thread. `BertCut.Media.PreviewPump` owns the `PreviewEngine`, runs
it on a thread of its own, and reads ahead of the playhead; `EditorViewModel` posts a request
and carries on. Four things follow, and each was got wrong first.

- **Requests are latest-wins and the pump never blocks anybody.** A scrub costs one seek per
  place the pointer rested rather than one per pixel it crossed. A real seek is 115 ms at
  1280x768 and 195 ms at 1080p; paid inline on the thread that also repaints, that was the
  window freezing for the length of a drag.
- **What gets displayed is the nearest frame, not the playhead's.** During playback they are
  the same thing, which is what made the mistake so easy: the ring holds the exact frame, so
  demanding it looks right everywhere. A *drag* outruns the decoder by construction — every
  position is a seek and the playhead has moved again by the time one lands — so an exact
  match threw away every frame that arrived while the pointer was moving, and the picture
  only changed when the hand paused. Dragging backwards, where nothing read ahead helps, that
  was 0.3 fps; taking whatever is closer than what is on screen makes it 33. Driving it needs
  a pointer that stays down: `drag-playhead` in the harness, not a run of `scrub`s.
- **The ring is what makes a stall invisible.** Having served the frame it was asked for, the
  pump keeps decoding until it runs out of buffers or something new is asked for, so a GC
  pause, a layout pass or the seek at a cut boundary lands on frames decoded before anyone
  wanted them. A 250 ms hiccup now costs 250 ms of playback and nothing else. The ring is
  budgeted in **bytes**, not frames — a frame is 1 MB at 640x384 and 24 MB at 4K, so a count
  would be either trivial or most of a gigabyte.
- **Reverse is served a window at a time, not a frame at a time.** A backwards step has no
  route but the preceding keyframe, so it is a seek — longer than a frame lasts, which means
  serving one at a time never converges: the seek outlasts the frame period, the playhead has
  moved on before it lands, and the speculative fill behind it never gets a turn. Frames are
  quantised into fixed windows half the ring wide and a window is filled in one *ascending*
  run — one seek, then a sequential decode — while the window before it is filled behind. That
  took reverse from 12 fps to 29. Quantising rather than centring on the playhead is what
  stops the window sliding a frame at a time and reintroducing the seek it exists to avoid.
- **Eviction is ranked against the playhead and its direction**, not against the frame being
  written. Ranking by plain distance throws out the far end of the read-ahead — the frame
  wanted next — and leaves the ones already passed, so the ring churns and never gets ahead
  of anything. Frames the playhead has passed go first; then the ones ahead, furthest first.

A buffer is **leased**, not copied: the producer will not write over a slot that is out on
loan, and the shell holds one for exactly as long as it is on screen. `WaitForIdle` reports
when *the frame asked for* is ready and deliberately not when the speculative ones behind it
are, so a scripted run does not wait out a dozen frames nobody asked about.

`PreviewEngine` itself is unchanged in kind — synchronous, single-threaded, driven directly by
`PreviewEngineTests` with no window. Everything that touches it, including `SetOutput`,
`Reset` and `Dispose`, is a command the pump thread drains: freeing a decoder from the UI
thread mid-`sws_scale` is a use-after-free rather than a race with a wrong answer.

## The preview's own resolution

The preview is composited at the size it is **displayed** at, not the project's output size.
`MainWindow.ApplyPreviewSize` divides the output by the smallest integer that still meets the
pane's physical pixels, capped at 4. A 3840x2160 project in this window renders at 1280x720 —
a ninth of the pixels — and a 1280x768 one on a 200%-scaled 4K display correctly stays at full
size, because the pane really does have 2286x1019 pixels to fill.

Snapping to an integer divisor rather than tracking the pane exactly is the point: every
cached decoder scales on output, so following a window edge would rebuild all of them and
their scaler contexts continuously. There are four possible sizes and a resize usually changes
nothing.

Two things this is *not*. It is not a change to geometry — crop and overlay rectangles stay in
output space and are mapped onto the canvas on the way in, which is why `RectEditor` needed no
change. And it is not a loss of quality: it is sharper, because ffmpeg's scaler does the
reduction instead of WPF resampling an oversized bitmap on every frame.

The size the preview is actually running at is in the harness `state` line as `previewSize`.
Nothing else in a run would show it, and a divisor that quietly stopped being applied looks
exactly like one that was.

## Decoding a frame

`VideoDecoder` is addressed by exact frame index, and landing on one means decoding forward
to it from somewhere, discarding what comes first. There are two somewheres — the preceding
keyframe, or wherever the decoder already stands — and it takes whichever is fewer frames
away. Advancing by one never seeks, which is the playback path.

That comparison is the whole of it, but it is worth knowing why it is not just "seek unless
sequential", which is what it was:

- **The playhead follows wall-clock time, so a late frame is recovered by skipping one.**
  Seeking to serve the skip cost half a GOP — on a 1280x768 recording with the 250-frame GOP
  a screen recorder writes, 115 ms against 1.8 ms for a sequential frame. The recovery was
  60x dearer than what it recovered from, so it left the decoder further behind than it
  started, and playback never caught up. One 40 ms hiccup took a clip from 30 fps to 13 for
  as long as it played. The cost scales with resolution times GOP length, which is why only
  the big files showed it.
- **Backwards is still a seek**, because there is no route to an earlier frame but the
  keyframe. Nothing here can fix that; it is fixed a level up, by the pump filling a whole
  window behind the playhead in one forward run.
- **Standing on the frame already is not a decode.** The last picture is still in `_frame` —
  nothing unrefs it between calls — so delivering it into a *different* buffer is one scale.
  Without that, the comparison above would see a target it is not ahead of and seek to the
  preceding keyframe to redeliver a frame already in hand, which is how a pool of read-ahead
  buffers turns into a seek per frame.
- The two `VideoDecoderTests` around `SeekCount` pin the first two. They count seeks rather
  than timing them: the cost is real, but a stopwatch in a test fails on a busy machine.

Frame-level threading is **on** (`FF_THREAD_FRAME | FF_THREAD_SLICE`, `thread_count = 0`). It
used to be off, on the grounds that reordering complicated the discard loop for no benefit —
but `avcodec_receive_frame` returns frames in presentation order either way, so there was
nothing to complicate. Re-measured on that same file it is worth 2.5x on a sequential frame
(1.52 → 0.59 ms) and 3x on a seek (84 → 28 ms). What it genuinely costs is a few frames of
latency after every flush, which is why it only became clearly right alongside a read-ahead
that hides it.

## The timeline strip

Three lanes, and which one a press lands in decides what it means. `TimelineControl` owns
that decision because it is a question about pixels; `EditorViewModel` owns what happens next
because the answer is an edit.

- **The ruler** (the top 16px) and **the waveform lane** are the only things that move the
  playhead. They are also where a press lets go of a selection. A press there stops the
  transport for as long as it lasts and hands it back on the drop — `BeginScrub`/`EndScrub`,
  in the view model because it is a question about the transport, remember the rate the press
  found. Stopping is a side effect of pointing at a frame rather than an instruction to stop,
  and a click resumes on the same rule as a drag: whether the pointer happened to travel is
  not a distinction the user makes.
- **The track** selects a base segment and nothing else — clicking a clip does not seek, so
  picking one up never costs you your place. Dragging one past the middle of its neighbour
  reorders the running order; that waits for the pointer to travel `DragThreshold`, so
  selecting a segment survives a shaky hand. With a single segment there is nothing to
  reorder and the drag simply ends.
- **The green band** along the bottom of the track is an overlay clip: body moves it, either
  end trims it. The band takes the press before the segment under it. A faint band in the same
  lane is an overlay being placed — the span `Enter` would commit. It starts at the playhead and
  follows it, so aiming a clip is just moving the playhead, by key or by the ruler.

One lane for time, one for clips, and no exceptions in either direction — a track that seeked
when it happened to have nothing to select would be a rule you could only learn by tripping
over it.

Two things are only right because they were got wrong first:

- **The hit test decides which clip, not the frame.** Ranges are half-open, so the last pixel
  column of a clip belongs to a frame the clip does not contain — and that column is exactly
  where its out-point is grabbed. The control passes the index it hit.
- **The selected item wins a shared boundary column.** Two clips that touch both answer to a
  press on the seam. Without this, the front of a clip could never be trimmed once something
  abutted it.

Selections are indices, and `OnDocumentChanged` drops them on every edit — an index means
nothing against a document that has been renumbered. The two drags are the exception: they
keep their own index in step with what they are rewriting, which is also why they are the
only callers allowed to survive a change.

## Placing an overlay

Two questions, deliberately separated, because one keypress used to answer both silently.

**What** is answered by a card — `EditorMode.OverlaySource`, a mode rather than a panel that
swallows keystrokes, so its digits resolve through the key map like every other key and a
scripted run presses them exactly as a user does. Three choices, all of which collapse to the
same `OverlayContent` — which source, from which of its frames, for how long — after which the
kind is forgotten. The arithmetic is `BertCut.Core.Edits.OverlayPlacement`, in Core so it is
testable without a window.

**Where** is answered by the playhead. `PendingRange` is a function of it, recomputed in the
`Playhead` setter, so moving the playhead by any means — a key, the ruler, an audio sync —
carries the clip and its ghost band along. Two rules follow:

- **The content never changes while the clip is being aimed.** It is what the user chose. This
  is why `Alt+←/→` is no longer bound in overlay mode, and why a sync during placement moves the
  clip rather than sliding its in-point.
- **A clip stops against what is in the way rather than being cut down by it.** The length was
  settled by the choice, and a clip that quietly came out shorter would break that promise.
  Truncation survives only for a gap smaller than the content, where no position fits. The
  payoff is that this path can never overlap an existing clip, so `AddOverlay`'s truncation of
  its neighbours cannot fire from here — a hazard removed by construction rather than by a
  special case. `OverlayPlacementTests` pins both as properties over every playhead position.

Marks name *what*, never *where*. A leftover mark cannot drag a placement away from where the
user is looking, because the placement always starts at the playhead. They are not spent on
commit either: a crop *is* the range they named, but an overlay only borrowed them.

`IsPlacing` asks for `Crop or Overlay` by name rather than "not Normal" — `RectEditor` watches
it, and a box appearing behind the card would answer a question nobody had asked yet.

## Never launch the GUI

`dotnet run --project src\BertCut.App` — and `BertCut.exe` — put a real window on the
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

## Releasing

**The tag is the release.** No file carries a version: `git push origin vX.Y.Z` and
`.github/workflows/release.yml` tests, publishes self-contained, packs with Velopack, uploads six
assets to a GitHub Release and opens a winget PR. `unstable.yml` does the same off every push to
`main`, on its own channel and its own rolling pre-release, and the two cannot reach each other.
Installed copies update themselves from that feed through `UpdateService`; the app's own `Main`
runs `VelopackApp.Build().Run()` first, which is why `App.xaml` is a `Page` rather than the
`ApplicationDefinition`.

Two things about this repo in particular, both of which fail silently:

- **A package is only as good as `tools/ffmpeg` was at publish time.** It is gitignored, and the
  `None` glob that carries it into the output matches nothing when it is absent — so the publish
  succeeds and the installer has no decoder in it. CI fetches it before the *tests*, not just
  before the publish, because the ffmpeg-dependent tests skip silently too.
- **Nothing may be written to `%LOCALAPPDATA%\BertCut`.** That is the Velopack install directory
  and the installer empties it on every update. See **State** below.

Full detail, including the winget manifest and how to exercise the update path locally, is in
[docs/build-and-release.md](docs/build-and-release.md). The `release` skill is the procedure.

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
overlay-source range|segment|file <path>|cancel
                            take a row on the card `P` puts up. range and segment are what
                            `key 1` and `key 2` do; `file` supplies the answer the file picker
                            would have given, since that dialog belongs to the desktop
select-overlay <frame>      press and release on that overlay's band in the strip
drag-overlay <from> <to>    press on the band at <from> and drag until the grabbed point is
                            over <to>, in steps, as a mouse would
trim-overlay start|end <to> drag that end of the *selected* clip to <to>, which trims it
select-segment <frame>      click the base track there, which picks that segment out
drag-segment <from> <to>    drag that segment along the track, reordering as it goes
scrub <frame>               click the ruler above the track — seeks, and deselects
drag-playhead <from> <to>   press on the ruler and drag, so the playhead outruns the decoder
                            the way it does under a hand. Not the same test as a run of
                            scrubs: the pointer never lifts.
goto <frame> | play | stop | tick [n] | sleep <ms> | reset | close | settle [ms]
shot <name> [element]       PNG of the window, or of any x:Name'd element
dump-preview <name>.png     the composited video frame alone, no interface
state                       one JSON line: playhead, duration, marks, mode, hasMedia, crops,
                            overlays, segments, selectedSegment, overlaySourceStart,
                            selectedOverlay, overlayStart, overlayEnd, overlayLength, muted,
                            canUndo, previewSize, status
assert-status <substring> | assert-timecode <text> | assert-frame <n>
assert-mode Normal|Crop|Overlay|OverlaySource   the card is a mode, and this is what says
                                                a choice has been taken
assert-frame-between <a> <b> | assert-visible <Name> | assert-hidden <Name>
assert-overlay-source-start <a> [b]   where the overlay in question reads from
assert-overlay-start <a> [b] | assert-overlay-end <a> [b]
                                      what it covers on the timeline. "The overlay in
                                      question" is the one being placed, else the selected
                                      one, else the one under the playhead — a trim moves a
                                      clip out from under it.
assert-overlay-selected [index] | assert-no-overlay-selected | assert-overlays <n>
assert-segment-selected [index] | assert-no-segment-selected | assert-segments <n>
assert-marks <in> <out> | assert-no-marks    an overlay of the marked range borrows the marks
                                             without spending them, and nothing else shows it
assert-muted | assert-unmuted
assert-has-media | assert-no-media | assert-unlocked <path>   (exclusive open, so it
                                                               really tests the handle)
```

Options: `--out <dir>`, `--state-dir <dir>`, `--keep-state`, `--timeout <sec>`,
`--busy-timeout <ms>`, `--keep-going`, `--audio`, `--verbose`.

### Rules

**The mouse is not synthesised.** No cursor is over an offscreen window and the only real one
belongs to the user, so `select-overlay` and `drag-overlay` drive `TimelineControl`'s own
`PointerDown`/`PointerMove`/`PointerUp` — the three methods its mouse handlers call — at points
the control works out from a frame number. Everything above the OS input layer is therefore
under test: the hit test, the grab offset, the pixel-to-frame arithmetic. Keep new pointer
gestures in that shape rather than in the event handlers, or they become undrivable.

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
thread, so the composition tick cannot fire. Follow a `sleep` with `tick`. `tools/ui/playback.bcs`
is the script for this ground, including a run of scrubs — the gesture that used to freeze the
window for the length of a seek apiece.

**`settle` waits for the picture, and it loops.** Frames are composited on the pump's thread,
so the dispatcher going quiet no longer means the frame is ready; `Settle` waits for the pump
too, and every command that changes anything ends in a `Settle`, so captures need nothing of
their own. It loops because presenting a frame can ask for another: the preview pane sizes
itself to the picture, so the first frame of a newly opened video is what settles how much
detail is worth compositing, and that answer sends it back to be rendered again at the size it
should have been. The stopping condition is `EditorViewModel.PreviewSettled` — the frame on
screen is the playhead's — not "the pump is idle".

## State

The app keeps sessions, key bindings, its thumbnail cache and its audio envelopes under
`%USERPROFILE%\.bertcut`,
autosaves every 750 ms, and restores by content key — so without isolation a test run would be
restored into the user's editor next time they opened the same file. `BERTCUT_STATE_DIR`
relocates that root (`BertCut.Core.Session.AppPaths`); the harness points it at a scratch
directory per run and deletes it afterwards. `--keep-state` keeps it, which is how session
restore gets tested across two runs.

**It is the profile rather than `%LOCALAPPDATA%\BertCut` because that is now the Velopack install
directory, and the installer deletes it** on install and on every update. State kept there would
survive exactly until the first update landed. `AppPaths.MigrateLegacyData` moves what earlier
builds left behind, once, and is skipped entirely when `BERTCUT_STATE_DIR` is set — a scripted run
must not reach into the real profile in either direction. Nothing new goes under `%LOCALAPPDATA%`.

FFmpeg discovery deliberately does *not* follow that variable — it is an installed tool, not
state. It probes `%USERPROFILE%\.bertcut\ffmpeg` ahead of the old `%LOCALAPPDATA%` path for the
same reason as above: a copy installed into the install directory would not survive an update.

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
