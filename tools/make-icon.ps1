<#
.SYNOPSIS
    Draws the BertCut application icon and writes src/BertCut.App/Assets/app.ico.

.DESCRIPTION
    The icon is the timeline strip reduced to the three marks the app draws it with: two
    clips, the red playhead between them, and the green overlay band along the bottom of one.
    Same palette as TimelineControl, so the icon and the thing it stands for cannot drift.

    It is authored here rather than checked in as one bitmap because the mark is not one
    drawing scaled down. A playhead two pixels wide at 256 is invisible at 16, so strokes are
    size-relative with a floor in device pixels, and the two details that turn to mush below
    32px -- the playhead's grip and the tile's border -- are simply not drawn there. Every
    size in the .ico is composed for the size it is displayed at.

    Sizes up to 64 are stored as DIBs and the two large ones as PNG. PNG-compressed entries
    are fine for Windows itself, but every tool that reads an .ico understands a DIB, and this
    file is handed to vpk as well as to the shell.
#>
[CmdletBinding()]
param(
    [string] $Out = (Join-Path $PSScriptRoot '..\src\BertCut.App\Assets\app.ico'),

    # Written alongside for anywhere a PNG is wanted -- a README, a store listing.
    [string] $PngOut = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

# The window's own colours. See BertCut.App/TimelineControl.cs.
$TileTop     = '#FF2B3543'
$TileBottom  = '#FF141A21'
$TileEdge    = '#FF445266'
$ClipNear    = '#FFDCE6F3'
$ClipFar     = '#FFB7C6D9'
$OverlayBand = '#FF6DD48B'
$PlayheadRed = '#FFFF5C5C'

function New-Brush([string] $hex) {
    $b = [System.Windows.Media.SolidColorBrush]::new(
        [System.Windows.Media.ColorConverter]::ConvertFromString($hex))
    $b.Freeze()
    $b
}

function New-Rect([double] $x, [double] $y, [double] $w, [double] $h) {
    [System.Windows.Rect]::new($x, $y, $w, $h)
}

# Renders the mark at one size. Everything is a fraction of the tile so the proportions hold,
# with floors where a fraction would fall below a device pixel.
function New-Frame([int] $s) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()

    # --- the tile -----------------------------------------------------------------
    $tile = [System.Windows.Media.LinearGradientBrush]::new(
        [System.Windows.Media.ColorConverter]::ConvertFromString($TileTop),
        [System.Windows.Media.ColorConverter]::ConvertFromString($TileBottom),
        90)
    $tile.Freeze()

    # Below 32px the border eats a whole pixel off each edge and reads as a smudge.
    $edge = $null
    if ($s -ge 32) {
        $edge = [System.Windows.Media.Pen]::new(
            (New-Brush $TileEdge), [Math]::Max(1.0, $s * 0.008))
        $edge.Freeze()
    }

    $inset = if ($edge) { $edge.Thickness / 2 } else { 0 }
    $radius = $s * 0.215
    $dc.DrawRoundedRectangle($tile, $edge,
        (New-Rect $inset $inset ($s - 2 * $inset) ($s - 2 * $inset)), $radius, $radius)

    # --- two clips, the playhead between them --------------------------------------
    #
    # Every edge here lands on a whole pixel. Left to the fractions, a 16px clip edge falls
    # mid-pixel and comes back as two grey rows instead of one white one -- at that size the
    # whole clip is five pixels wide, so a soft edge is a fifth of it.
    $clipTop = [Math]::Round($s * 0.275)
    $clipBottom = [Math]::Round($s * 0.725)
    $clipRadius = [Math]::Max(0.5, $s * 0.035)

    $left = New-Rect ([Math]::Round($s * 0.115)) $clipTop `
        ([Math]::Round($s * 0.460) - [Math]::Round($s * 0.115)) ($clipBottom - $clipTop)
    $right = New-Rect ([Math]::Round($s * 0.540)) $clipTop `
        ([Math]::Round($s * 0.885) - [Math]::Round($s * 0.540)) ($clipBottom - $clipTop)

    $dc.DrawRoundedRectangle((New-Brush $ClipNear), $null, $left, $clipRadius, $clipRadius)
    $dc.DrawRoundedRectangle((New-Brush $ClipFar), $null, $right, $clipRadius, $clipRadius)

    # The overlay band lies along the bottom of the far clip, exactly as the strip draws it,
    # clipped to that clip's rounded corners so it does not square them off.
    $bandHeight = [Math]::Max(1.0, [Math]::Round($s * 0.105))
    $dc.PushClip([System.Windows.Media.RectangleGeometry]::new($right, $clipRadius, $clipRadius))
    $dc.DrawRectangle((New-Brush $OverlayBand), $null,
        (New-Rect $right.X ($right.Bottom - $bandHeight) $right.Width $bandHeight))
    $dc.Pop()

    # --- the playhead ---------------------------------------------------------------
    #
    # An even width centred on a whole pixel, so the stem is solid columns rather than a
    # smear over three of them. Two pixels is the floor: one disappears against the tile.
    $red = New-Brush $PlayheadRed
    $stemWidth = [Math]::Max(2, 2 * [Math]::Round($s * 0.0175))
    $stemLeft = [Math]::Round($s / 2) - $stemWidth / 2
    $stemTop = [Math]::Round($s * 0.115)
    $stem = New-Rect $stemLeft $stemTop $stemWidth ([Math]::Round($s * 0.885) - $stemTop)
    $dc.DrawRoundedRectangle($red, $null, $stem, $stemWidth / 2, $stemWidth / 2)

    # The cursor's grip. At 24px and below it is a pixel either side of the stem and only
    # thickens it, so it is left off entirely.
    if ($s -ge 32) {
        $gripWidth = [Math]::Max(2, 2 * [Math]::Round($s * 0.058))
        $grip = New-Rect ([Math]::Round($s / 2) - $gripWidth / 2) $stemTop `
            $gripWidth ([Math]::Round($s * 0.105))
        $gripRadius = $s * 0.026
        $dc.DrawRoundedRectangle($red, $null, $grip, $gripRadius, $gripRadius)
    }

    $dc.Close()

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $s, $s, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $bitmap
}

function Get-PngBytes($bitmap) {
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    $stream.ToArray()
}

# A 32bpp DIB as an .ico stores it: a BITMAPINFOHEADER with the height doubled, then the
# colour rows bottom-up, then a 1bpp AND mask that is all zeros -- the alpha channel is what
# actually decides transparency, but the mask must be there and padded to 4 bytes a row.
function Get-DibBytes($bitmap) {
    $s = $bitmap.PixelWidth
    $straight = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new(
        $bitmap, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $stride = $s * 4
    $pixels = [byte[]]::new($stride * $s)
    $straight.CopyPixels($pixels, $stride, 0)

    $stream = [System.IO.MemoryStream]::new()
    $w = [System.IO.BinaryWriter]::new($stream)
    $w.Write([uint32] 40)         # biSize
    $w.Write([int32] $s)          # biWidth
    $w.Write([int32] ($s * 2))    # biHeight: colour rows plus mask rows
    $w.Write([uint16] 1)          # biPlanes
    $w.Write([uint16] 32)         # biBitCount
    $w.Write([uint32] 0)          # BI_RGB
    $w.Write([uint32] 0)          # biSizeImage
    foreach ($i in 1..4) { $w.Write([uint32] 0) }   # resolution and palette counts

    for ($y = $s - 1; $y -ge 0; $y--) {
        $w.Write($pixels, $y * $stride, $stride)
    }

    $maskStride = [int][Math]::Ceiling($s / 32.0) * 4
    $w.Write([byte[]]::new($maskStride * $s))

    $w.Flush()
    $stream.ToArray()
}

# 20 and 24 are sizes the Windows 11 taskbar and Explorer really ask for; leaving them out
# makes the shell downscale 32 and the playhead goes soft.
$sizes = 16, 20, 24, 32, 48, 64, 128, 256

$entries = foreach ($size in $sizes) {
    $frame = New-Frame $size

    # The cast matters: a byte[] returned from a function comes back unrolled, and
    # BinaryWriter then picks an overload that writes one element of it.
    $bytes = if ($size -ge 128) { Get-PngBytes $frame } else { Get-DibBytes $frame }
    [pscustomobject]@{
        Size  = $size
        Bytes = [byte[]] $bytes
    }
}

$out = [System.IO.Path]::GetFullPath($Out)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($out)) | Out-Null

$stream = [System.IO.File]::Create($out)
try {
    $w = [System.IO.BinaryWriter]::new($stream)
    $w.Write([uint16] 0)                  # reserved
    $w.Write([uint16] 1)                  # ICO
    $w.Write([uint16] $entries.Count)

    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $w.Write([byte] ($e.Size % 256))  # 256 is stored as 0
        $w.Write([byte] ($e.Size % 256))
        $w.Write([byte] 0)                # palette size
        $w.Write([byte] 0)                # reserved
        $w.Write([uint16] 1)              # planes
        $w.Write([uint16] 32)             # bits per pixel
        $w.Write([uint32] $e.Bytes.Length)
        $w.Write([uint32] $offset)
        $offset += $e.Bytes.Length
    }
    foreach ($e in $entries) { $w.Write($e.Bytes) }
    $w.Flush()
} finally {
    $stream.Dispose()
}

Write-Host "wrote $out ($((Get-Item $out).Length) bytes, $($sizes -join '/'))"

if ($PngOut) {
    $png = [System.IO.Path]::GetFullPath($PngOut)
    [System.IO.File]::WriteAllBytes($png, (Get-PngBytes (New-Frame 256)))
    Write-Host "wrote $png"
}
