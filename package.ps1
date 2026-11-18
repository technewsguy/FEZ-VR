# Builds the mod and packages the release:
#   dist\FEZ-VR-Setup-v<version>.exe   the installer on its own (what most people download)
#   dist\FEZ-VR-v<version>.zip         the same installer + README and licenses (used by the Scoop bucket)
param([string]$GameDir = "T:\FEZ-VR\game")
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& (Join-Path $root 'build.ps1') -GameDir $GameDir
$ver = ([regex]'const string Version = "([^"]+)"').Match((Get-Content (Join-Path $root 'src\FezVr.cs') -Raw)).Groups[1].Value
$name = "FEZ-VR-v$ver"
$stage = Join-Path $root "dist\$name"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $root 'build\FEZ-VR-Setup.exe'), (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD_PARTY.md') $stage
$zip = Join-Path $root "dist\$name.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$exe = Join-Path $root "dist\FEZ-VR-Setup-v$ver.exe"
Copy-Item (Join-Path $root 'build\FEZ-VR-Setup.exe') $exe -Force
foreach ($f in $exe, $zip) {
    Write-Host ("Release file: {0}  ({1} MB)  SHA256 {2}" -f $f, [math]::Round((Get-Item $f).Length / 1MB, 2), (Get-FileHash $f).Hash.ToLower())
}
