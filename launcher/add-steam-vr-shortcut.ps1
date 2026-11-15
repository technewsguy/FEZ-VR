# Adds "FEZ VR" to the Steam library as a non-Steam game with "Include in VR Library" on,
# plus the game's Steam artwork (cover, header, background, logo), a SteamVR library entry, and desktop / Start menu
# shortcuts. It starts through the launcher (SteamVR closes its dashboard, no "Resume Game" menu).
# Steam must be closed while its shortcut list is edited: this script closes and restarts it.
# Usage: .\add-steam-vr-shortcut.ps1 -GameDir "<folder with FEZ.exe and the mod installed>" [-BuildDir <folder with the launcher files>]
param([Parameter(Mandatory = $true)][string]$GameDir, [string]$BuildDir = "")
$ErrorActionPreference = 'Stop'
$name = "FEZ VR"
if (-not $BuildDir) { $BuildDir = Join-Path $PSScriptRoot '..\build' }
$BuildDir = (Resolve-Path $BuildDir).Path
if (-not (Test-Path (Join-Path $GameDir 'FEZ.exe'))) { throw "FEZ.exe not found in $GameDir" }
if (-not (Test-Path (Join-Path $GameDir 'FEZ-VR.exe'))) { throw "Install the mod into $GameDir first." }
foreach ($f in 'fezvr-launch.exe', 'SteamShortcut.exe', 'openvr_api.dll') { if (-not (Test-Path (Join-Path $BuildDir $f))) { throw "$f missing in $BuildDir (run build.ps1)" } }

$steamKey = Get-ItemProperty HKCU:\Software\Valve\Steam
$steam = $steamKey.SteamPath; $steamExe = $steamKey.SteamExe
$user = (Get-ItemProperty HKCU:\Software\Valve\Steam\ActiveProcess -ErrorAction SilentlyContinue).ActiveUser
if (-not $user) { $user = (Get-ChildItem "$steam\userdata" -Directory | Select-Object -First 1).Name }
$cfg = "$steam\userdata\$user\config"
$art = "$steam\appcache\librarycache\224760"

# 1. Launcher in its own folder (it is x64 with its own openvr_api.dll; the game process is x86)
$ld = Join-Path $GameDir 'FEZVR-launcher'
New-Item -ItemType Directory -Force $ld | Out-Null
Copy-Item (Join-Path $BuildDir 'fezvr-launch.exe'), (Join-Path $BuildDir 'openvr_api.dll') $ld -Force
$launcher = Join-Path $ld 'fezvr-launch.exe'

# 2. SteamVR library entry (registered by the launcher on its first run): runs the launcher with --noop
foreach ($p in @(@('header.jpg', 'fezvr_header.jpg'), @('library_600x900.jpg', 'fezvr_capsule.jpg'))) {
    if (Test-Path "$art\$($p[0])") { Copy-Item "$art\$($p[0])" (Join-Path $ld $p[1]) -Force }
}
$esc = { param($s) $s.Replace('\', '\\') }
# SteamVR's web-based screens ("Up Next") do not load local file paths: use the game's images on Steam's CDN.
$cdn = 'https://shared.steamstatic.com/store_item_assets/steam/apps/224760'
$imgHeader = "$cdn/header.jpg"; $imgCapsule = "$cdn/library_600x900.jpg"
$manifest = @"
{
  "source": "builtin",
  "applications": [{
    "app_key": "fezvr.mod.game",
    "launch_type": "binary",
    "binary_path_windows": "$(& $esc $launcher)",
    "working_directory": "$(& $esc $ld)",
    "arguments": "--noop",
    "is_dashboard_overlay": false,
    "image_path": "$imgHeader",
    "image_path_capsule": "$imgCapsule",
    "strings": { "en_us": { "name": "$name", "description": "FEZ as a 3D diorama in VR (FEZ-VR mod)" } }
  }]
}
"@
Set-Content (Join-Path $ld 'fezvr.vrmanifest') $manifest -Encoding UTF8

# 3. Desktop + Start menu shortcuts
$ws = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $lnk = $ws.CreateShortcut((Join-Path $dir "$($name.Replace(':', '')).lnk"))
    $lnk.TargetPath = $launcher; $lnk.WorkingDirectory = $ld
    $lnk.IconLocation = "$(Join-Path $GameDir 'FEZ.exe'),0"
    $lnk.Description = "FEZ in VR (FEZ-VR mod)"
    $lnk.Save()
}

# 4. Steam library entry + artwork (Steam closed while shortcuts.vdf is edited)
if (Get-Process steam -ErrorAction SilentlyContinue) {
    Write-Host "Closing Steam..."
    Start-Process $steamExe -ArgumentList '-shutdown'
    $t = 0; while ((Get-Process steam -ErrorAction SilentlyContinue) -and $t -lt 60) { Start-Sleep 1; $t++ }
    if (Get-Process steam -ErrorAction SilentlyContinue) { throw "Steam did not close; close it and run this again." }
}
if ((Test-Path "$cfg\shortcuts.vdf") -and -not (Test-Path "$cfg\shortcuts.vdf.fezvr-backup")) { Copy-Item "$cfg\shortcuts.vdf" "$cfg\shortcuts.vdf.fezvr-backup" }
$id = & (Join-Path $BuildDir 'SteamShortcut.exe') "$cfg\shortcuts.vdf" $name $launcher $ld (Join-Path $GameDir 'FEZ.exe') '-' 1
if ($LASTEXITCODE -ne 0 -or -not $id) { throw "Adding the Steam shortcut failed." }
$id = "$id".Trim()
# The desktop shortcut hands over to this Steam entry (so Steam Input and the overlay work).
$gameId = ([uint64]$id -shl 32) -bor 0x02000000
Set-Content (Join-Path $ld 'steam_gameid.txt') $gameId
New-Item -ItemType Directory -Force "$cfg\grid" | Out-Null
$pairs = [ordered]@{ 'header.jpg' = "$id.jpg"; 'library_600x900.jpg' = "${id}p.jpg"; 'library_hero.jpg' = "${id}_hero.jpg"; 'logo.png' = "${id}_logo.png" }
foreach ($k in $pairs.Keys) {
    $dst = "$cfg\grid\$($pairs[$k])"
    if (Test-Path "$art\$k") { Copy-Item "$art\$k" $dst -Force; Write-Host "artwork: $k -> grid\$($pairs[$k])" }
    else {
        try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest "$cdn/$k" -OutFile $dst -UseBasicParsing; Write-Host "artwork: $k (Steam CDN) -> grid\$($pairs[$k])" }
        catch { Write-Host "artwork: $k not available ($($_.Exception.Message))" }
    }
}
Write-Host "Added '$name' (shortcut id $id). Starting Steam..."
Start-Process $steamExe
