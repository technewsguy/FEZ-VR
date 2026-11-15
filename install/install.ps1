# FEZ-VR installer: FEZ as a 3D diorama in VR (SteamVR).
# Usage (PowerShell):  .\install.ps1                  finds FEZ through Steam
#                      .\install.ps1 -GameDir "D:\Games\FEZ"
#                      .\install.ps1 -NoSteamEntry    skip the "FEZ VR" Steam / SteamVR entry
#                      .\install.ps1 -Uninstall       removes the mod (FEZ's own files are never changed)
# The mod only adds files next to FEZ.exe. It also adds a "FEZ VR" game to Steam and SteamVR (with FEZ's artwork)
# and "FEZ VR" desktop / Start menu shortcuts; Steam is closed and restarted for that.
param([string]$GameDir, [switch]$Uninstall, [switch]$NoSteamEntry)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$ModFiles = @('FEZ-VR.exe', '0Harmony.dll', 'Mono.Cecil.dll', 'Mono.Cecil.Mdb.dll', 'Mono.Cecil.Pdb.dll', 'Mono.Cecil.Rocks.dll',
              'MonoMod.RuntimeDetour.dll', 'MonoMod.Utils.dll', 'openvr_api.dll', 'fezvr_input', 'FEZVR-launcher', 'fezvr.log', 'fezvr_dump')

function Find-Game {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }
    $libs = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) { $libs += (Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"').Matches | ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' } }
    foreach ($l in $libs) {
        $d = Join-Path $l 'steamapps\common\FEZ'
        if (Test-Path (Join-Path $d 'FEZ.exe')) { return $d }
    }
    return $null
}

if (-not $GameDir) { $GameDir = $env:FEZVR_GAMEDIR }
if (-not $GameDir) { $GameDir = Find-Game }
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'FEZ.exe'))) {
    throw "Could not find FEZ. Run again with -GameDir `"<folder that contains FEZ.exe>`"."
}
$GameDir = (Resolve-Path $GameDir).Path
Write-Host "Game folder: $GameDir"
if (Get-Process FEZ-VR, FEZ -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -like (Join-Path $GameDir '*')) }) { throw "FEZ is running. Close it first." }

$name = 'FEZ VR'
$links = @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs')) | ForEach-Object { Join-Path $_ "$name.lnk" }

if ($Uninstall) {
    foreach ($f in $ModFiles) {
        $t = Join-Path $GameDir $f
        if (Test-Path $t) { Remove-Item $t -Recurse -Force; Write-Host "Removed $f" }
    }
    # Shortcuts that point into this game folder
    $ws = New-Object -ComObject WScript.Shell
    foreach ($l in $links) {
        if ((Test-Path $l) -and ($ws.CreateShortcut($l).TargetPath -like (Join-Path $GameDir '*'))) { Remove-Item $l -Force; Write-Host "Removed $l" }
    }
    Write-Host "FEZ-VR removed. The 'FEZ VR' entry stays in your Steam library until you remove it there (right-click > Manage > Remove non-Steam game)."
    return
}

# 1. Mod files next to FEZ.exe (FEZ's own files are not touched)
Copy-Item (Join-Path $here 'files\*') $GameDir -Recurse -Force
Write-Host "Copied the mod (FEZ-VR.exe and its libraries)."

# 2. "FEZ VR" in Steam and SteamVR with artwork, plus desktop / Start menu shortcuts
if (-not $NoSteamEntry) {
    & (Join-Path $here 'steam-entry\add-steam-vr-shortcut.ps1') -GameDir $GameDir -BuildDir (Join-Path $here 'steam-entry')
}

Write-Host ""
Write-Host "Done. Play 'FEZ VR' from Steam, SteamVR or the desktop shortcut (it starts SteamVR if needed)."
Write-Host "Regular FEZ (flat screen) is unchanged."
Write-Host "To remove: uninstall.cmd   (or .\install.ps1 -Uninstall)"
