---
name: bertcut-ui
description: Run, drive, and screenshot the BertCut WPF app without it appearing on the user's screen or taking their keyboard focus. Use whenever asked to run the app, start it, launch it, screenshot it, click through it, check a UI change, or confirm something works in the real interface. Never run `dotnet run` on src/BertCut.App — that opens a real window over whatever the user is doing.
---

# Driving BertCut's interface

The user multitasks while you work. A window that pops up over what they are doing and takes
the keyboard is not a minor annoyance — their keystrokes and yours end up in the same input
queue. So the app is never launched directly. Instead the harness hosts the same
`MainWindow`, in the same process, parked at -32000,-32000 and refused activation, and you
look at PNGs of it.

## Do not

- `dotnet run --project src\BertCut.App`
- run `BertCut.App.exe`
- synthesise keystrokes or mouse input with any tool

The only exception is the user explicitly asking you to launch the app.

## Pick the cheapest tier first

**Is the question about video pixels?** Where a crop landed, which frame shows after a ripple
delete, whether an overlay covers the right region — that needs no window. Write a test in
`tests/BertCut.Core.Tests` against `PreviewEngine.Render(...)` and `PreviewEngine.Canvas`,
following `PreviewEngineTests`, and dump a frame with `Png.Save(engine.Canvas, path)` if you
want to look at it. Deterministic, fast, no UI.

**Is the question about the interface?** Layout, theming, what the timeline draws, whether an
overlay appears, what the status bar says — that is the harness.

## Running it

```powershell
$harness = "C:\Source\bertcut\tools\BertCut.Harness\bin\x64\Debug\net10.0-windows\BertCut.Harness.exe"
```

Build with `dotnet build C:\Source\bertcut\BertCut.slnx` first — **the solution, not the
csproj**, or the output lands in `bin\Debug\` and you run a stale binary.

```powershell
& $harness --script C:\Source\bertcut\tools\ui\smoke.bcs           # the canonical pass
& $harness --script C:\Source\bertcut\tools\ui\overlay-drag.bcs    # overlay clips, by pointer
& $harness --script C:\Source\bertcut\tools\ui\segment-drag.bcs    # base segments, by pointer
& $harness --script C:\Source\bertcut\tools\ui\overlay-place.bcs   # the source card, and aiming
& $harness --script C:\Source\bertcut\tools\ui\sync.bcs            # audio sync, both directions
& $harness -c "sample c.mp4 6; open c.mp4; goto 90; shot check"    # ad hoc
```

Then `Read` each path printed after `SHOT `.

Exit codes: `0` pass · `1` a command or assertion failed · `2` environment · `3` watchdog.

## Commands

```
sample <path> [seconds]     synthesise a testsrc2 clip; it prints its own frame number,
                            over a tone with an aperiodic envelope
sample-angles <path> [sec]  one clip holding the same event twice, the second time quieter
                            and noisier — the fixture the audio sync is meant for
open|import|append <path>
key <gesture>               through the live key map:  key I  ·  key Ctrl+Z  ·  key >
intent <EditorIntent>       straight to the window's single dispatch point
overlay-source range|segment|file <path>|cancel
                            take a row on the card `P` puts up. `range` and `segment` are what
                            `key 1` and `key 2` do; `file` supplies the answer the file picker
                            would have given, because that dialog belongs to the desktop and
                            would appear on the user's real screen
select-overlay <frame>      press and release on that overlay's band in the timeline strip
drag-overlay <from> <to>    press on the band at <from> and drag until the grabbed point is
                            over <to> — through the control's own hit test, in steps, as a
                            mouse would. No OS input is synthesised.
trim-overlay start|end <to> drag that end of the *selected* clip to <to>. Pressing on an end
                            trims; pressing in the middle moves. Same hit test either way.
select-segment <frame>      click the base track there, which picks that segment out. It
                            does not move the playhead — only `scrub` and `goto` do.
drag-segment <from> <to>    drag that segment along the track, which reorders the film. Goes
                            through the drag threshold rather than around it.
scrub <frame>               click the ruler above the track — seeks and deselects, which is
                            the only way to test that clicking off a clip lets go of it
goto <frame> | play | stop | tick [n] | sleep <ms> | reset | close | settle [ms]
shot <name> [element]       PNG of the window, or of any x:Name'd element
dump-preview <name>.png     the composited video frame alone, no interface around it
state                       one JSON line of everything worth asserting on
assert-status <substring> | assert-timecode <text> | assert-frame <n>
assert-mode Normal|Crop|Overlay|OverlaySource
                                      the source card is a mode, so this is what says it is
                                      up — and what says a choice has been taken
assert-frame-between <a> <b> | assert-visible <Name> | assert-hidden <Name>
assert-overlay-source-start <a> [b]   where the overlay in question reads from in its own
                                      source. A trim of the front end carries it; aiming a
                                      clip being placed must never change it
assert-overlay-start <a> [b] | assert-overlay-end <a> [b]
                                      what it covers on the timeline
assert-overlay-selected [index] | assert-no-overlay-selected | assert-overlays <n>
assert-segment-selected [index] | assert-no-segment-selected | assert-segments <n>
assert-marks <in> <out> | assert-no-marks
assert-muted | assert-unmuted
assert-has-media | assert-no-media | assert-unlocked <path>
echo <text>                 '#' at the start of a line is a comment
```

"The overlay in question" is the one being placed while the editor is in overlay mode,
otherwise the selected one, falling back to the one under the playhead. Selecting is how you
say which clip you mean — trimming a clip's front takes it out from under the playhead as
often as not.

**Placing an overlay is two steps.** `P` puts up a card asking *what*: the marked range, the
selected segment, or a video file taken whole. That settles the clip and its length. Then the
playhead answers *where* — the faint band follows it, `A` puts it where the sound matches, and
`Enter` commits. So a script that wants an overlay needs `key P` and then a choice; a bare
`key P` now only opens the card.

**Where a press lands decides what it does.** The ruler above the track and the waveform
below it are the only lanes that move the playhead; the track selects a base segment; the
green band along the bottom of the track is an overlay, whose ends trim and whose middle
moves. Selecting anything leaves the playhead exactly where it was, so a script that needs
both must say both. The four commands above aim at those lanes for you — `scrub`,
`select-segment`, `select-overlay`, `trim-overlay` — so it never has to know the geometry.

Options: `--out <dir>` · `--state-dir <dir>` · `--keep-state` · `--timeout <sec>` ·
`--busy-timeout <ms>` · `--keep-going` · `--audio` · `--verbose`

Element names come straight from `MainWindow.xaml`: `Toolbar`, `ClearButton`, `ResetButton`,
`PreviewImage`, `EmptyHint`, `Placement`, `RestoreToast`, `TransportRow`, `TransportControls`,
`PlayButton`, `StopButton`, `MuteButton`, `TimecodeLabel`, `SelectionLabel`, `TransportLabel`,
`Timeline`, `TransportIcon`, `StatusLabel`, `HintLabel`, `HelpOverlay`, `HelpCard`, `HelpList`,
`StripHelp`, `SettingsOverlay`, `Settings`.

`StripHelp` is the help sheet's section on what a click does where — it sits below the fold
of a sheet a script cannot scroll, so capture it on its own: `shot strip StripHelp`.

**A run makes no sound** unless you pass `--audio`. The harness injects a null sink, so the
whole playback path still runs — decoders, segment boundaries, the clock the playhead follows
— into something that consumes it at real time and throws it away. Letting it reach the
speakers is as intrusive as putting the window on screen, so do not pass `--audio` unless you
were asked to.

## Blocked on purpose

`OpenFile`, `ImportSource`, `AppendSource` and `Export` open modal Win32 dialogs. Those are
owned by the desktop, not by the offscreen window, so they *would* appear on the user's
screen. The harness refuses them and names the replacement — use `open`, `import`, `append`.
Export is already covered headlessly by `EndToEndExportTests`.

## Recipes

**Look at a change you just made**

```powershell
& $harness -c "sample c.mp4 4; open c.mp4; goto 45; shot after-change" --out $env:TEMP\look
```

**Before and after an edit**

```powershell
& $harness -c "sample c.mp4 6; open c.mp4; goto 30; key I; goto 90; key O; shot 1-marked; key X; assert-status ripple; shot 2-cut; dump-preview 3-frame.png; state"
```

**An overlay, settled**

```powershell
& $harness -c "intent ToggleHelp; assert-visible HelpOverlay; shot help"
```

`settle` runs automatically after every command, which waits out the ffprobe behind an open
and jumps the entrance fades to their finished state — two captures of the same overlay are
byte-identical.

## What to trust

- Captures are a software re-render of the visual tree, not a screen grab, so being offscreen
  costs nothing. A `shot` that comes back one flat colour fails rather than lying to you.
- **Do not diff UI screenshots pixel for pixel** — text rasterisation varies with font version
  and DPI. Assert through `state`, `assert-status`, `assert-visible`. Pixel comparison is fine
  on `dump-preview` output.
- **Playback assertions must be bounded** (`assert-frame-between`) because it runs off a real
  clock — the audio device's own position at 1x, a stopwatch otherwise. For an exact frame,
  `goto` there. And `sleep` does not advance playback by itself: it blocks the dispatcher
  thread, so the composition tick cannot fire. Write `play; sleep 600; tick`.
- Each run gets a scratch `BERTCUT_STATE_DIR`, so it cannot inherit the previous run's edits
  or touch the user's real sessions and key bindings. Use `--keep-state` only when you mean to
  test session restore across two runs.
- A run reporting more than one foreground correction means something new is activating the
  window — worth investigating rather than ignoring.
