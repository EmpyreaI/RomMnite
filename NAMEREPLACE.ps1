foreach ($f in 'README.md','extension.yaml','manifests\addon.yaml','manifests\installer.yaml','Settings\RomMnitePage.cs') {
  $p = Join-Path $PWD $f
  $bytes = [IO.File]::ReadAllBytes($p)
  $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
  $t = [IO.File]::ReadAllText($p)
  $t = $t -creplace 'Empyreal','Empyreai' -creplace 'empyreal','Empyreai'
  [IO.File]::WriteAllText($p, $t, [Text.UTF8Encoding]::new($bom))
}
Select-String -Pattern 'empyreal' -Path README.md,extension.yaml,manifests\*.yaml,Settings\RomMnitePage.cs