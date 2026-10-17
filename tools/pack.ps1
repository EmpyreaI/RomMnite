# Packs the built extension (bin\Release\net462) into a .pext with Playnite's Toolbox, then
# removes from it the copies in lib\ of libraries that are also at its top.
#
# Every dependency is copied into lib\ as well (RomM.csproj, CopyDependenciesToLib), because the
# Toolbox leaves out of the top of a package the libraries Playnite ships itself, and the plugin
# loads those from lib\ (PluginDependencies.cs). The ones the Toolbox keeps at the top are then
# packed twice; only the top copy is ever loaded.
#
#   .\tools\pack.ps1 -Toolbox "G:\Playnite\Toolbox.exe"
param(
    [string]$Toolbox = "Toolbox.exe",
    [string]$Output = "PackingOutput"
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$built = Join-Path $root 'bin\Release\net462'
if (-not [IO.Path]::IsPathRooted($Output)) { $Output = Join-Path $root $Output }
New-Item -ItemType Directory -Force $Output | Out-Null

& $Toolbox pack $built $Output

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
foreach ($pext in Get-ChildItem $Output -Filter *.pext) {
    $zip = [IO.Compression.ZipFile]::Open($pext.FullName, 'Update')
    try {
        $top = @{}
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName -replace '/', '\'
            if ($name -notmatch '\\') { $top[$name.ToLowerInvariant()] = $true }
        }
        $twice = @($zip.Entries | Where-Object {
            $name = $_.FullName -replace '/', '\'
            ($name -match '^lib\\([^\\]+)$') -and $top.ContainsKey($Matches[1].ToLowerInvariant())
        })
        foreach ($entry in $twice) { $entry.Delete() }
        Write-Host "$($pext.Name): $($twice.Count) libraries packed twice, removed from lib\"
    }
    finally {
        $zip.Dispose()
    }
}
