# BertCut

A fast, keyboard-first video editor for software demos. Three operations, nothing else:
**cut out ranges**, **crop/zoom a range**, **overlay a clip picture-in-picture**.

## Running it

```powershell
.\tools\fetch-ffmpeg.ps1          # one-time, ~200 MB
dotnet run --project src\BertCut.App
```

You can also pass a file directly, or associate `.mp4` with `BertCut.App.exe`:

```powershell
dotnet run --project src\BertCut.App -- "C:\recordings\demo.mp4"
```

## The core loop

Mark a range and delete it — three keystrokes, no modifiers:

| Key | |
|---|---|
| `I` / `O` | mark in / mark out |
| `X` | ripple delete the marked range |
| `Ctrl+Z` | undo |

`Space` plays, `J`/`K`/`L` shuttle, `←`/`→` step a frame, `Shift+←`/`→` step a second,
`↑`/`↓` jump between cuts, `Ctrl+E` exports. `F1` lists everything.

`<` and `>` also step a frame, and hold them down to run the video that way at normal
speed — one key for both nudging and crawling. `J` and `L` double their speed each time you
press them while already going that way, up to 8x. The status bar says which it is doing: a
red cross when it is stopped, and a green chevron per doubling pointing the way it is
moving.

### Crop and overlay

Mark a range, then:

| Key | |
|---|---|
| `C` | crop that range — it zooms to fill for its duration, then reverts |
| `P` | overlay a clip on that range, picture-in-picture |
| `Ctrl+I` | import another video to overlay (a webcam take, say) |

Both drop you into a placement box: arrows move it, `Shift+↑`/`↓` resize, `1`–`5` snap to a
corner or the centre, `Enter` applies, `Esc` cancels. You can also drag the box, or drag
anywhere outside it to draw a new one. While placing an overlay, `Alt+←`/`→` slides its
content against the base track to sync it.

Both boxes are aspect-locked — the crop to the output ratio, the overlay to its own
source's — so a crop never needs letterboxing and an overlay is never stretched. With no
second file imported, `P` overlays the base video on itself, which is how you get a zoomed
inset of one moment on top of the full view.

### Adding another video

`Ctrl+A` joins a file onto the end of the timeline as a new segment. Its length is
converted into output frames on the way in, so a 60 fps clip on a 30 fps recording does
not drift; its picture is scaled to the project's frame.

A toolbar across the top is the mouse-reachable version of the same thing: **Add video**,
**Export**, **Reset everything** — which asks first, and is still one `Ctrl+Z` away from
coming back — and **How it works**, which opens the help sheet that `F1` toggles.

There is no Save. Edits autosave against the video's content hash and come back when you
reopen it — including the undo history.

## Export

Two paths, chosen automatically:

- **Cut-only** — video is stream-copied, so export runs at roughly file-copy speed. Audio
  is still re-encoded: AAC frames are 1024 samples and never align to video frames, so
  copying it would leave every cut up to 20 ms out of sync.
- **Crop or overlay present** — one NVENC pass per kept segment, then a packet-level join.
  Input-side `-ss` makes this frame-accurate *and* means only the kept frames are decoded,
  so cutting five minutes out of a two-hour recording decodes five minutes.

## Layout

```
src/BertCut.Core     no UI, no GPU, no ffmpeg — the timeline model, edits, export planner
src/BertCut.Media    ffmpeg decode, probing, preview compositing, export process driving
src/BertCut.App      WPF shell, timeline, key map
tests/               134 tests; the ffmpeg-dependent ones skip if it isn't installed
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

## Licence

MIT. See [LICENSE](LICENSE).
