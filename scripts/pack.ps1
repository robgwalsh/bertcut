# Builds the Velopack installer locally. Prereq: dotnet tool install -g vpk
# Output lands in Releases\ (Setup.exe, full/delta packages, portable zip).
# Running it again with a higher version against the same Releases\ dir produces a delta, which
# is how the delta path gets exercised without publishing anything.
param([Parameter(Mandatory)][string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# Before the tests, not just before the publish: the ffmpeg-dependent tests skip silently when it
# is absent, and the None glob in BertCut.App.csproj would match nothing and pack an installer
# with no decoder in it. A no-op once tools\ffmpeg\.version is there.
& "$root\tools\fetch-ffmpeg.ps1"
if ($LASTEXITCODE -ne 0) { exit 1 }

dotnet test "$root\BertCut.slnx" -c Release
if ($LASTEXITCODE -ne 0) { exit 1 }

# -p:Platform=x64 because this publishes a csproj rather than the solution, and the solution is
# where x64 is pinned. The FFmpeg DLLs are 64-bit only.
dotnet publish "$root\src\BertCut.App\BertCut.App.csproj" -c Release `
    -r win-x64 --self-contained true -p:Platform=x64 -p:Version=$Version -o "$root\publish"
if ($LASTEXITCODE -ne 0) { exit 1 }

# The one failure this whole script exists to catch, and one that produces no error of its own:
# a package that installs cleanly and cannot decode a frame.
if (-not (Test-Path "$root\publish\ffmpeg\ffmpeg.exe")) {
    Write-Error "publish\ffmpeg\ffmpeg.exe is missing - the package would have no decoder in it."
    exit 1
}

# Conditional so a package can be built before the icon lands; vpk fails on a path that is not there.
$icon = if (Test-Path "$root\src\BertCut.App\Assets\app.ico") {
    @('--icon', "$root\src\BertCut.App\Assets\app.ico")
} else { @() }

vpk pack --packId BertCut --packVersion $Version --packDir "$root\publish" `
    --mainExe BertCut.exe --packTitle BertCut --packAuthors "Rob Walsh" `
    @icon --outputDir "$root\Releases"
exit $LASTEXITCODE
