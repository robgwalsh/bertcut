<#
.SYNOPSIS
    Installs BertCut: the latest release, the unstable build, or this checkout's own build.

.DESCRIPTION
    From anywhere, in Windows PowerShell 5.1 or PowerShell 7:

        irm https://raw.githubusercontent.com/robgwalsh/bertcut/main/install.ps1 | iex
        iex "& { $(irm https://raw.githubusercontent.com/robgwalsh/bertcut/main/install.ps1) } -Channel unstable"
        iex "& { $(irm https://raw.githubusercontent.com/robgwalsh/bertcut/main/install.ps1) } -Version 0.1.0"

    From a checkout, its own build:

        pwsh .\install.ps1 -Local            # publish\ into the installed copy
        pwsh .\install.ps1 -Local -Build     # fetch FFmpeg and publish first, as scripts\pack.ps1 does
        pwsh .\install.ps1 -Local -Watch     # and again whenever a publish changes it

    Every source lands in the same place, Velopack's per-user install in %LOCALAPPDATA%\BertCut (no
    admin prompt), so anything looking for an installed BertCut finds whichever went in last. Data in
    %USERPROFILE%\.bertcut is never touched.

    A local build is its own Velopack channel, "local": the first one is packed with vpk and installed
    by its Setup.exe like any release, and from then on an install copies only the files that changed.
    A -local version never updates itself from GitHub. Installing a release or unstable over it puts
    that copy back on its channel. Setup starts the app when it finishes; the copying path never does.
    See docs/build-and-release.md, "Installing from the command line".

.PARAMETER Channel
    stable (default) or unstable. Also read from $env:BERTCUT_CHANNEL, for the `| iex` form.
.PARAMETER Version
    A specific release (0.1.0 or v0.1.0) instead of the latest.
.PARAMETER Local
    Install this checkout's publish\ instead of a download.
.PARAMETER Build
    With -Local: fetch FFmpeg and publish first.
.PARAMETER Watch
    With -Local: keep running and install again each time a publish changes publish\.
.PARAMETER Repack
    With -Local: pack and run Setup even when a local install exists (refreshes shortcuts and the
    Apps & features entry, and drops files the build no longer has; the fast path only adds and replaces).
.PARAMETER InstallRoot
    Where to install. Only for testing: everything else expects the default.
#>
param(
    [ValidateSet('stable', 'unstable')] [string] $Channel,
    [string] $Version,
    [switch] $Local,
    [switch] $Build,
    [switch] $Watch,
    [switch] $Repack,
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'BertCut')
)

# A child scope, so that run through `| iex` nothing below leaks into the caller's session - and no
# `exit`, which would close it.
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Windows PowerShell's progress bar slows downloads tenfold
    $repo = 'https://github.com/robgwalsh/bertcut'
    $defaultRoot = Join-Path $env:LOCALAPPDATA 'BertCut'
    $current = Join-Path $InstallRoot 'current'
    $root = $PSScriptRoot   # empty when run through iex
    if (-not $Channel) { $Channel = if ($env:BERTCUT_CHANNEL) { $env:BERTCUT_CHANNEL } else { 'stable' } }

    function Get-InstalledVersion {
        $file = Join-Path $current 'sq.version'
        if (-not (Test-Path $file)) { return $null }
        $m = [regex]::Match((Get-Content $file -Raw), '<version>\s*([^<\s]+)\s*</version>')
        if ($m.Success) { $m.Groups[1].Value } else { $null }
    }

    # Closes the installed copy, not one running from a bin\ folder. Politely first, so an open
    # session is saved as on any close.
    function Stop-InstalledApp {
        $prefix = $current.TrimEnd('\') + '\'
        $running = @(Get-Process BertCut -ErrorAction SilentlyContinue |
                Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
        if (-not $running) { return }
        Write-Host 'Closing the installed BertCut...'
        foreach ($p in $running) { [void]$p.CloseMainWindow() }
        foreach ($p in $running) { if (-not $p.WaitForExit(5000)) { Stop-Process -Id $p.Id -Force } }
    }

    function Invoke-Setup([string] $setup) {
        Stop-InstalledApp
        $log = Join-Path ([IO.Path]::GetTempPath()) "bertcut-setup-$PID.log"
        $arguments = "--silent --log `"$log`""
        if ($InstallRoot -ne $defaultRoot) { $arguments += " --installto `"$InstallRoot`"" }
        # Process.WaitForExit, not Start-Process -Wait: Setup starts the app when it finishes, and
        # -Wait would wait for that too.
        $p = [Diagnostics.Process]::Start($setup, $arguments)
        $p.WaitForExit()
        if ($p.ExitCode -ne 0) { throw "Setup failed with exit code $($p.ExitCode); its log is $log" }
        if (-not (Test-Path (Join-Path $current 'BertCut.exe'))) { throw "Setup finished but $current\BertCut.exe is missing; its log is $log" }
    }

    function Install-FromGitHub {
        $tag = $Version.Trim().TrimStart('v')
        if ($tag) {
            $url = "$repo/releases/download/v$tag/BertCut-win-Setup.exe"; $what = "BertCut $tag"
        }
        elseif ($Channel -eq 'unstable') {
            $url = "$repo/releases/download/unstable/BertCut-unstable-Setup.exe"; $what = 'the unstable build'
        }
        else {
            $url = "$repo/releases/latest/download/BertCut-win-Setup.exe"; $what = 'the latest release'
        }
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $dir = Join-Path ([IO.Path]::GetTempPath()) ('bertcut-install-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $dir | Out-Null
        try {
            $setup = Join-Path $dir 'BertCut-Setup.exe'
            Write-Host "Downloading $what..."
            Invoke-WebRequest $url -OutFile $setup -UseBasicParsing
            Write-Host 'Installing...'
            Invoke-Setup $setup
        }
        finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }

    function Get-NextPatch {
        $tag = git -C $root tag --list 'v[0-9]*' --sort=-v:refname | Select-Object -First 1
        if (-not $tag) { $tag = 'v0.0.0' }
        $major, $minor, $patch = $tag.TrimStart('v').Split('.')
        "$major.$minor.$([int]$patch + 1)"
    }

    # The publish pack.ps1 and CI make, minus the tests. The version is fixed per checkout rather
    # than per build, so an unchanged project stays incremental, and it is what the title bar shows.
    function Invoke-Publish {
        & (Join-Path $root 'tools\fetch-ffmpeg.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'fetch-ffmpeg.ps1 failed' }
        dotnet publish (Join-Path $root 'src\BertCut.App\BertCut.App.csproj') -c Release `
            -r win-x64 --self-contained true -p:Platform=x64 "-p:Version=$(Get-NextPatch)-local" -o $publish
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    }

    # The failure scripts\pack.ps1 exists to catch: an install that cannot decode a frame.
    function Assert-Publish {
        if (-not (Test-Path (Join-Path $publish 'BertCut.exe'))) { throw "$publish has no BertCut.exe: publish first (-Build)" }
        if (-not (Test-Path (Join-Path $publish 'ffmpeg\ffmpeg.exe'))) { throw "$publish\ffmpeg\ffmpeg.exe is missing - the install would have no decoder. Run tools\fetch-ffmpeg.ps1 and publish again (-Build)." }
    }

    # Once per machine (or with -Repack): a real Velopack package, so the install has its stub,
    # shortcuts and Apps & features entry exactly as a release's would be.
    function Install-LocalPackage {
        if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
            throw 'The first local install packs with vpk: dotnet tool install -g vpk'
        }
        $version = "$(Get-NextPatch)-local.$(Get-Date -Format yyyyMMddHHmmss)"
        $out = Join-Path $root 'Releases\local-install'
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }
        $icon = Join-Path $root 'src\BertCut.App\Assets\app.ico'
        $iconArgs = if (Test-Path $icon) { @('--icon', $icon) } else { @() }
        Write-Host "Packing $version (first local install; later ones copy only what changed)..."
        vpk pack --packId BertCut --packVersion $version --packDir $publish --mainExe BertCut.exe `
            --packTitle BertCut --packAuthors 'Rob Walsh' @iconArgs `
            --channel local --delta None --outputDir $out | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with exit code $LASTEXITCODE" }
        Invoke-Setup (Join-Path $out 'BertCut-local-Setup.exe')
    }

    # Every later install: copy whichever files differ. A file still running (an ffmpeg.exe export the
    # app left behind) cannot be overwritten but can be renamed, so it is moved aside and cleared next time.
    function Sync-LocalBuild {
        $base = (Resolve-Path $publish).Path.TrimEnd('\') + '\'
        $changed = @(Get-ChildItem $publish -File -Recurse | Where-Object {
                $dst = Join-Path $current $_.FullName.Substring($base.Length)
                -not (Test-Path $dst) -or $_.Length -ne (Get-Item $dst).Length -or
                (Get-FileHash $_.FullName).Hash -ne (Get-FileHash $dst).Hash
            })
        Get-ChildItem $current -Filter '*.old-*' -File -Recurse | Remove-Item -ErrorAction SilentlyContinue
        if (-not $changed) { Write-Host 'The installed copy already matches the build.'; return }
        Stop-InstalledApp
        foreach ($file in $changed) {
            $dst = Join-Path $current $file.FullName.Substring($base.Length)
            New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
            try { Copy-Item $file.FullName $dst -Force }
            catch {
                Move-Item $dst "$dst.old-$([DateTime]::UtcNow.Ticks)"
                Copy-Item $file.FullName $dst -Force
            }
        }
        $names = @($changed | Select-Object -First 5 | ForEach-Object Name)
        if ($changed.Count -gt 5) { $names += "and $($changed.Count - 5) more" }
        Write-Host ("Installed {0} -> {1}" -f ($names -join ', '), $current)
    }

    function Install-Local {
        Assert-Publish
        $installed = Get-InstalledVersion
        if ($Repack -or -not $installed -or $installed -notmatch '-local(\.|$)') { Install-LocalPackage }
        else { Sync-LocalBuild }
    }

    if (-not $Local) {
        if ($Build -or $Watch -or $Repack) { throw '-Build, -Watch and -Repack go with -Local.' }
        Install-FromGitHub
        Write-Host "Installed BertCut $(Get-InstalledVersion) to $current"
        return
    }

    if (-not $root -or -not (Test-Path (Join-Path $root 'BertCut.slnx'))) {
        throw '-Local installs a checkout''s build: run .\install.ps1 -Local from the repository.'
    }
    $publish = Join-Path $root 'publish'
    if ($Build) { Invoke-Publish }
    if (-not $Watch) {
        Install-Local
        return
    }

    # Polls rather than using a FileSystemWatcher: a publish writes hundreds of files, and what
    # matters is the moment the set stops changing, which a poll sees directly.
    $stamp = {
        if (-not (Test-Path $publish)) { return '' }
        (Get-ChildItem $publish -File -Recurse | ForEach-Object { "$($_.FullName):$($_.LastWriteTimeUtc.Ticks):$($_.Length)" }) -join '|'
    }
    Write-Host "Watching $publish - every publish that changes it is installed. Ctrl+C to stop."
    $last = $null
    while ($true) {
        $now = & $stamp
        if ($now -ne $last) {
            Start-Sleep -Seconds 2
            if ((& $stamp) -eq $now) {
                $last = $now
                $started = Get-Date
                try { Install-Local; Write-Host ("  ({0:N1} s, {1:HH:mm:ss})" -f ((Get-Date) - $started).TotalSeconds, (Get-Date)) }
                catch { Write-Warning $_ }
            }
            continue
        }
        Start-Sleep -Seconds 1
    }
}
