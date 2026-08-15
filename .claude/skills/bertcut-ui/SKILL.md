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
& $harness -c "sample c.mp4 6; open c.mp4; goto 90; shot check"    # ad hoc
```

Then `Read` each path printed after `SHOT `.

Exit codes: `0` pass · `1` a command or assertion failed · `2` environment · `3` watchdog.

## Commands

```
sample <path> [seconds]     synthesise a testsrc2 clip; it prints its own frame number
open|import|append <path>
key <gesture>               through the live key map:  key I  ·  key Ctrl+Z  ·  key >
intent <EditorIntent>       straight to the window's single dispatch point
goto <frame> | play | stop | tick [n] | sleep <ms> | reset | settle [ms]
shot <name> [element]       PNG of the window, or of any x:Name'd element
dump-preview <name>.png     the composited video frame alone, no interface around it
state                       one JSON line of everything worth asserting on
assert-status <substring> | assert-timecode <text> | assert-frame <n>
assert-frame-between <a> <b> | assert-visible <Name> | assert-hidden <Name> | assert-has-media
echo <text>                 '#' at the start of a line is a comment
```

Options: `--out <dir>` · `--state-dir <dir>` · `--keep-state` · `--timeout <sec>` ·
`--busy-timeout <ms>` · `--keep-going` · `--verbose`

Element names come straight from `MainWindow.xaml`: `Toolbar`, `PreviewImage`, `EmptyHint`,
`Timeline`, `Placement`, `TimecodeLabel`, `SelectionLabel`, `StatusLabel`, `TransportIcon`,
`TransportLabel`, `HelpOverlay`, `HelpCard`, `SettingsOverlay`, `Settings`, `RestoreToast`,
`ResetButton`, `HintLabel`.

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
  stopwatch. For an exact frame, `goto` there.
- Each run gets a scratch `BERTCUT_STATE_DIR`, so it cannot inherit the previous run's edits
  or touch the user's real sessions and key bindings. Use `--keep-state` only when you mean to
  test session restore across two runs.
- A run reporting more than one foreground correction means something new is activating the
  window — worth investigating rather than ignoring.
