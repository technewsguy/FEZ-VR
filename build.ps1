# Builds FEZ-VR.exe (the mod: loaded into FEZ.exe through FEZ.exe.config, or run directly to host FEZ.exe) and
# FEZ-VR-Setup.exe (the installer, with the mod files embedded).
# -GameDir: a FEZ folder to compile against (FEZ.exe, FezEngine.dll, FNA.dll, Common.dll). -Install: also copy the
# mod into that folder (development; normally run the installer).
param([string]$GameDir = "T:\FEZ-VR\game", [switch]$Install)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
$out = Join-Path $root "build"
New-Item -ItemType Directory -Force $out | Out-Null
$version = [regex]::Match((Get-Content "$root\src\FezVr.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value

$refs = @("$GameDir\FEZ.exe", "$GameDir\FezEngine.dll", "$GameDir\FNA.dll", "$GameDir\Common.dll", "$root\deps\harmony\0Harmony.dll", "System.Drawing.dll")
$src = @(Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object FullName)
$src += Join-Path $root "deps\openvr\openvr_api.cs"
$src += Join-Path $root "installer\SteamVRTag.cs"   # SteamVR VR-tab check (shared with the installer)
& $csc /nologo /target:exe /platform:x86 /optimize+ /debug- /warn:4 "/out:$out\FEZ-VR.exe" ($refs | ForEach-Object { "/r:$_" }) $src
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $out\FEZ-VR.exe"
Copy-Item (Join-Path $root "deps\openvr\openvr_api_x86.dll") (Join-Path $out "openvr_api.dll") -Force

# Installer: everything that goes into the game folder, embedded as resources "payload/<path in the game folder>".
$payload = Join-Path $out "payload"
Remove-Item $payload -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$payload\fezvr_input" | Out-Null
Copy-Item "$out\FEZ-VR.exe", "$out\openvr_api.dll", "$root\installer\FEZ.exe.config" $payload
Copy-Item "$root\deps\harmony\*.dll" $payload
Copy-Item "$root\input\*.json" "$payload\fezvr_input"
$resArgs = Get-ChildItem $payload -Recurse -File | ForEach-Object {
    "/resource:$($_.FullName),payload/" + $_.FullName.Substring($payload.Length + 1).Replace('\', '/')
}
"static partial class FezVrSetup { internal const string ModVersion = `"$version`"; }" | Set-Content "$out\SetupVersion.cs" -Encoding ASCII
@"
using System.Reflection;
[assembly: AssemblyTitle("FEZ-VR $version Setup")]
[assembly: AssemblyProduct("FEZ-VR")]
[assembly: AssemblyDescription("Installs the FEZ-VR mod into FEZ (Steam)")]
[assembly: AssemblyFileVersion("$version.0")]
"@ | Set-Content "$out\SetupAssemblyInfo.cs" -Encoding ASCII
& $csc /nologo /target:winexe /optimize+ /main:FezVrSetup /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/out:$out\FEZ-VR-Setup.exe" (Join-Path $root "installer\Setup.cs") (Join-Path $root "installer\SteamVRTag.cs") "$out\SetupVersion.cs" "$out\SetupAssemblyInfo.cs" $resArgs
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
Write-Host "Built $out\FEZ-VR-Setup.exe ($([math]::Round((Get-Item "$out\FEZ-VR-Setup.exe").Length / 1MB, 2)) MB)"

if ($Install) {
    Get-ChildItem $payload | Copy-Item -Destination $GameDir -Recurse -Force
    Write-Host "Installed to $GameDir"
}
