<#
.SYNOPSIS
    Downloads the FFmpeg build BertCut runs against, into tools/ffmpeg.

.DESCRIPTION
    Fetches BtbN's win64 LGPL *shared* n8.1 build. One download serves both engines:

      * the shared libraries (avcodec-62.dll and friends) are what FFmpeg.AutoGen 8.1
        loads for in-process decoding in the preview,
      * ffmpeg.exe / ffprobe.exe in the same folder drive import and export.

    Using one build for both is what keeps the preview and the exported file agreeing at
    the codec level.

    LGPL rather than GPL is deliberate and costs nothing here: NVENC, NVDEC, CUVID and the
    CUDA filters are all built from the MIT-licensed nv-codec-headers and are present in
    every BtbN variant, so the LGPL build is hardware-accelerated *and* redistributable.
    The only things the GPL build adds are libx264/libx265, which the NVENC path does not
    need.
#>
[CmdletBinding()]
param(
    [string]$Version = 'n8.1',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$asset  = "ffmpeg-$Version-latest-win64-lgpl-shared-8.1.zip"
$url    = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$asset"
$root   = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'tools\ffmpeg'
$marker = Join-Path $target '.version'

if ((Test-Path $marker) -and -not $Force) {
    $installed = (Get-Content $marker -Raw).Trim()
    if ($installed -eq $asset) {
        Write-Host "FFmpeg $Version already present in $target. Use -Force to reinstall."
        exit 0
    }
}

$temp = Join-Path ([System.IO.Path]::GetTempPath()) "bertcut-ffmpeg-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $zip = Join-Path $temp $asset
    Write-Host "Downloading $asset ..."

    # Invoke-WebRequest's progress bar makes large downloads roughly an order of magnitude
    # slower in Windows PowerShell; suppressing it is not cosmetic.
    $previousProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try   { Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing }
    finally { $ProgressPreference = $previousProgress }

    Write-Host 'Extracting ...'
    Expand-Archive -Path $zip -DestinationPath $temp -Force

    # The archive contains a single versioned top-level folder; flatten its bin/ into
    # tools/ffmpeg so paths are stable across version bumps.
    $extracted = Get-ChildItem -Path $temp -Directory | Where-Object { $_.Name -like 'ffmpeg-*' } | Select-Object -First 1
    if (-not $extracted) { throw "Downloaded archive did not contain the expected ffmpeg-* folder." }

    $bin = Join-Path $extracted.FullName 'bin'
    if (-not (Test-Path $bin)) { throw "Downloaded archive has no bin/ directory; is this the shared build?" }

    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    Copy-Item -Path (Join-Path $bin '*') -Destination $target -Recurse -Force

    foreach ($license in @('LICENSE.txt', 'LICENSE.md', 'COPYING.LGPLv2.1', 'COPYING.LGPLv3')) {
        $path = Join-Path $extracted.FullName $license
        if (Test-Path $path) { Copy-Item $path $target -Force }
    }

    Set-Content -Path $marker -Value $asset -NoNewline

    $exe = Join-Path $target 'ffmpeg.exe'
    if (-not (Test-Path $exe)) { throw "ffmpeg.exe is missing from $target after extraction." }

    $banner = (& $exe -hide_banner -version | Select-Object -First 1)
    Write-Host "Installed to $target"
    Write-Host $banner
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
