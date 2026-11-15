# Builds the mod and packages a release zip in dist\ (install.ps1 + files laid out like the game folder).
param([string]$GameDir = "T:\FEZ-VR\game")
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
& (Join-Path $root 'build.ps1') -GameDir $GameDir
$ver = ([regex]'const string Version = "([^"]+)"').Match((Get-Content (Join-Path $root 'src\FezVr.cs') -Raw)).Groups[1].Value
$name = "FEZ-VR-v$ver"
$stage = Join-Path $root "dist\$name"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
$files = Join-Path $stage 'files'
New-Item -ItemType Directory -Force (Join-Path $files 'fezvr_input') | Out-Null
Copy-Item (Join-Path $root 'build\FEZ-VR.exe'), (Join-Path $root 'build\openvr_api.dll') $files
Copy-Item (Join-Path $root 'deps\harmony\*.dll') $files
Copy-Item (Join-Path $root 'input\*.json') (Join-Path $files 'fezvr_input')
Copy-Item (Join-Path $root 'install\*') $stage
# "FEZ VR" Steam / SteamVR entry (the installer runs add-steam-vr-shortcut.ps1 from here)
$se = Join-Path $stage 'steam-entry'; New-Item -ItemType Directory -Force $se | Out-Null
Copy-Item (Join-Path $root 'build\fezvr-launch.exe'), (Join-Path $root 'build\SteamShortcut.exe'), (Join-Path $root 'build\openvr_api.dll'), (Join-Path $root 'launcher\add-steam-vr-shortcut.ps1') $se
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD_PARTY.md') $stage
$zip = Join-Path $root "dist\$name.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "Packaged $zip"
