<#
.SYNOPSIS
    Downloads the Windows builds of the emulators the RomM Playnite plugin sets up, named and
    sorted for uploading to RomM as firmware, so the plugin installs them from your server.

.DESCRIPTION
    Each emulator is saved as  <OutDir>\<platform folder>\emulator-<id>-<version>.<zip|7z>
    The plugin looks for firmware files named "emulator-<id>..." (newest name wins), on the
    platform being set up first, then on any platform.

    Sources (the same the plugin uses):
      duckstation  github.com/stenzek/duckstation             -> psx
      pcsx2        github.com/PCSX2/pcsx2                      -> ps2
      ppsspp       ppsspp.org (version from GitHub tags)       -> psp
      dolphin      dolphin-emu.org/download                    -> ngc, wii
      rpcs3        github.com/RPCS3/rpcs3-binaries-win         -> ps3
      xenia        github.com/xenia-canary (newest of both)    -> xbox360
      flycast      github.com/flyinghead/flycast               -> dc
      retroarch    buildbot.libretro.com (stable)              -> -RetroArchPlatforms

    A file already in place is not downloaded again. Run it again later to pick up new versions
    (old ones are kept; delete them if you like, the plugin takes the newest name).

.PARAMETER OutDir
    Where to put the folders. Point it at RomM's bios folder (library\bios) to skip uploading,
    then scan the platforms in RomM.

.PARAMETER Platforms
    Override folder names when your RomM uses others (e.g. -Platforms @{ dolphin = @('gc','wii') }).

.PARAMETER DolphinUrl
    A Dolphin .7z link to use when its download page cannot be read.

.EXAMPLE
    .\Get-RommEmulators.ps1 -OutDir D:\romm-emulators
.EXAMPLE
    .\Get-RommEmulators.ps1 -Only dolphin,xenia -DolphinUrl https://dl.dolphin-emu.org/releases/2512/dolphin-2512-x64.7z
#>
[CmdletBinding()]
param(
    [string]   $OutDir = (Join-Path (Get-Location) 'romm-emulators'),
    [string[]] $Only,
    [hashtable]$Platforms = @{},
    [string[]] $RetroArchPlatforms = @('nds'),
    [string]   $DolphinUrl,
    [string]   $GitHubToken = $env:GITHUB_TOKEN
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times slower with its progress bar
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# RomM's default platform folder names; -Platforms overrides them.
$defaultPlatforms = @{
    duckstation = @('psx')
    pcsx2       = @('ps2')
    ppsspp      = @('psp')
    dolphin     = @('ngc', 'wii')
    rpcs3       = @('ps3')
    xenia       = @('xbox360')
    flycast     = @('dc')
    retroarch   = $RetroArchPlatforms
}
foreach ($k in $Platforms.Keys) { $defaultPlatforms[$k] = @($Platforms[$k]) }

$headers = @{ 'User-Agent' = 'RomM-Playnite-emulators'; 'Accept' = 'application/vnd.github+json' }
if ($GitHubToken) { $headers['Authorization'] = "Bearer $GitHubToken" }   # 60 requests/hour without one

function Get-GitHubAsset {
    # The newest release (by publish date) across the repositories with an asset whose name
    # matches $Include and not $Exclude. Returns @{ Url; Name; Version } or $null.
    param([string[]]$Repos, [string]$Include, [string]$Exclude, [bool]$AllowPrerelease = $true)
    $found = @()
    foreach ($repo in $Repos) {
        try {
            $releases = Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$repo/releases?per_page=15"
        } catch {
            Write-Warning "Could not read $repo's releases: $($_.Exception.Message)"
            continue
        }
        foreach ($r in $releases) {
            if ($r.draft) { continue }
            $asset = $r.assets | Where-Object { $_.name -match $Include -and $_.name -notmatch $Exclude } | Select-Object -First 1
            if ($asset) {
                $found += [pscustomobject]@{
                    Published  = [datetime]$r.published_at
                    Prerelease = [bool]$r.prerelease
                    Url        = $asset.browser_download_url
                    Name       = $asset.name
                    Version    = $r.tag_name
                    Repo       = $repo
                }
            }
        }
    }
    $pick = $found | Where-Object { $AllowPrerelease -or -not $_.Prerelease } | Sort-Object Published -Descending | Select-Object -First 1
    if (-not $pick) { $pick = $found | Sort-Object Published -Descending | Select-Object -First 1 }
    if ($pick) { Write-Host ("  newest in {0}: {1} ({2:yyyy-MM-dd})" -f $pick.Repo, $pick.Version, $pick.Published) }
    return $pick
}

function Get-LatestTag([string]$Repo) {
    (Invoke-RestMethod -Headers $headers -Uri "https://api.github.com/repos/$Repo/releases/latest").tag_name
}

$sources = [ordered]@{
    duckstation = { Get-GitHubAsset @('stenzek/duckstation') 'windows.*x64.*\.(zip|7z)$' 'symbols|debug|arm64|installer|sse2|pdb' $true }
    pcsx2       = { Get-GitHubAsset @('PCSX2/pcsx2') 'windows.*x64.*\.(7z|zip)$' 'symbols|debug|arm64|installer|pdb' $false }
    ppsspp      = {
        $tag = Get-LatestTag 'hrydgard/ppsspp'
        if ($tag -match '(\d+)\.(\d+)(?:\.(\d+))?') {
            $v = (@($Matches[1], $Matches[2], $Matches[3]) | Where-Object { $_ }) -join '_'
            [pscustomobject]@{ Url = "https://www.ppsspp.org/files/$v/ppsspp_win.zip"; Name = 'ppsspp_win.zip'; Version = $tag }
        }
    }
    dolphin     = {
        $url = $DolphinUrl
        if (-not $url) {
            $page = (Invoke-WebRequest -UseBasicParsing -Headers @{ 'User-Agent' = 'Mozilla/5.0' } -Uri 'https://dolphin-emu.org/download/').Content
            $links = [regex]::Matches($page, 'https?://[\w.-]*dolphin-emu\.org/[^"''\s<>]*?dolphin-[\w.-]+?-x64\.7z') | ForEach-Object { $_.Value }
            $url = ($links | Where-Object { $_ -match '/releases/' } | Select-Object -First 1)
            if (-not $url) { $url = $links | Select-Object -First 1 }
            if (-not $url) { throw "No Windows x64 .7z link on Dolphin's download page; pass one with -DolphinUrl." }
        }
        $name = [IO.Path]::GetFileName(([uri]$url).AbsolutePath)
        $ver = if ($name -match 'dolphin-(.+?)-x64') { $Matches[1] } else { 'latest' }
        [pscustomobject]@{ Url = $url; Name = $name; Version = $ver }
    }
    rpcs3       = { Get-GitHubAsset @('RPCS3/rpcs3-binaries-win') 'win64.*\.7z$' 'symbols|debug|pdb' $true }
    xenia       = { Get-GitHubAsset @('xenia-canary/xenia-canary', 'xenia-canary/xenia-canary-releases') 'windows.*\.(zip|7z)$' 'netplay|symbols|pdb|linux' $true }
    flycast     = { Get-GitHubAsset @('flyinghead/flycast') 'win.*64.*\.(zip|7z)$' 'arm|uwp|libretro|symbols|pdb' $false }
    retroarch   = {
        $tag = Get-LatestTag 'libretro/RetroArch'
        if ($tag -match '\d+\.\d+\.\d+') {
            [pscustomobject]@{ Url = "https://buildbot.libretro.com/stable/$($Matches[0])/windows/x86_64/RetroArch.7z"; Name = 'RetroArch.7z'; Version = $Matches[0] }
        }
    }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$results = @()

foreach ($id in $sources.Keys) {
    if ($Only -and $Only -notcontains $id) { continue }
    Write-Host "$id" -ForegroundColor Cyan
    try {
        $src = & $sources[$id]
        if (-not $src -or -not $src.Url) { throw "no Windows download found" }

        $ext = if ($src.Name -match '\.7z$') { '.7z' } else { '.zip' }
        $version = ($src.Version -replace '^v', '') -replace '[^\w.-]', '_'
        $fileName = "emulator-$id-$version$ext"

        $folders = @($defaultPlatforms[$id])
        $first = Join-Path $OutDir $folders[0]
        New-Item -ItemType Directory -Force -Path $first | Out-Null
        $target = Join-Path $first $fileName

        if (Test-Path $target) {
            Write-Host "  already here: $target"
        } else {
            Write-Host "  downloading $($src.Url)"
            $partial = "$target.part"
            Invoke-WebRequest -UseBasicParsing -Headers @{ 'User-Agent' = 'RomM-Playnite-emulators' } -Uri $src.Url -OutFile $partial
            Move-Item -Force $partial $target
            Write-Host ("  saved {0} ({1:N0} MB)" -f $target, ((Get-Item $target).Length / 1MB))
        }

        # The same file for the emulator's other platforms (Dolphin: GameCube and Wii).
        foreach ($folder in $folders | Select-Object -Skip 1) {
            $dir = Join-Path $OutDir $folder
            New-Item -ItemType Directory -Force -Path $dir | Out-Null
            $copy = Join-Path $dir $fileName
            if (-not (Test-Path $copy)) { Copy-Item $target $copy; Write-Host "  copied to $copy" }
        }
        $results += [pscustomobject]@{ Emulator = $id; File = $fileName; Folders = ($folders -join ', ') }
    } catch {
        Write-Warning "  $id failed: $($_.Exception.Message)"
        $results += [pscustomobject]@{ Emulator = $id; File = 'FAILED'; Folders = ($defaultPlatforms[$id] -join ', ') }
    }
}

Write-Host ''
$results | Format-Table -AutoSize
Write-Host "Done. Upload each folder's files to that platform's firmware in RomM, or, if -OutDir is RomM's bios folder, scan the platforms."
