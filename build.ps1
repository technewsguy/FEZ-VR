# Builds FEZ-VR.exe (the mod: an x86 host for FEZ.exe), the Steam / SteamVR launcher and the shortcuts.vdf tool.
# -GameDir: a FEZ folder to compile against (FEZ.exe, FezEngine.dll, FNA.dll, Common.dll). -Install: also copy the
# mod into that folder.
param([string]$GameDir = "T:\FEZ-VR\game", [switch]$Install)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
$out = Join-Path $root "build"
New-Item -ItemType Directory -Force $out | Out-Null
$refs = @("$GameDir\FEZ.exe", "$GameDir\FezEngine.dll", "$GameDir\FNA.dll", "$GameDir\Common.dll", "$root\deps\harmony\0Harmony.dll", "System.Drawing.dll")
& $csc /nologo /target:exe /platform:x86 /optimize+ /debug- /warn:4 "/out:$out\FEZ-VR.exe" ($refs | ForEach-Object { "/r:$_" }) (Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object FullName) (Join-Path $root "deps\openvr\openvr_api.cs")
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $out\FEZ-VR.exe"
# Launcher for the Steam / SteamVR "FEZ VR" entry and the shortcuts (x86, uses the same x86 openvr_api.dll)
& $csc /nologo /target:winexe /platform:x86 /optimize+ "/out:$out\fezvr-launch.exe" (Join-Path $root "launcher\FezVrLaunch.cs") (Join-Path $root "deps\openvr\openvr_api.cs")
if ($LASTEXITCODE -ne 0) { throw "Launcher build failed" }
& $csc /nologo /optimize+ "/out:$out\SteamShortcut.exe" (Join-Path $root "launcher\SteamShortcut.cs")
if ($LASTEXITCODE -ne 0) { throw "SteamShortcut build failed" }
Copy-Item (Join-Path $root "deps\openvr\openvr_api_x86.dll") (Join-Path $out "openvr_api.dll") -Force
if ($Install) {
    Copy-Item "$out\FEZ-VR.exe" $GameDir -Force
    Copy-Item "$root\deps\harmony\*.dll" $GameDir -Force
    Copy-Item "$out\openvr_api.dll" $GameDir -Force
    New-Item -ItemType Directory -Force "$GameDir\fezvr_input" | Out-Null
    Copy-Item "$root\input\*.json" "$GameDir\fezvr_input" -Force
    Write-Host "Installed to $GameDir"
}
