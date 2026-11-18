# FEZ-VR

An unofficial VR mod for **FEZ** (Steam, PC). The game becomes a little **3D diorama** floating in front of you: you
see FEZ's world as the real 3D model it always was, from your own head position, while it still plays exactly like
FEZ.

> Early test version. It is playable, but expect rough edges. Please report what you find (see Feedback below).

FEZ has no VR support of its own. This mod loads into the game when it starts, renders every frame twice from your
tracked eyes, and sends the images to SteamVR:

- **Diorama view** - the current part of the level sits in front of you at arm's length, in true stereo 3D, with the
  sky behind it. Depth is gently flattened so layers don't look too far apart.
- **Flat before the fez** - the opening levels stay flat as a pancake, as intended, until the cube gives Gomez the
  fez; then the world eases into 3D.
- **Menus, text and speech bubbles** on a floating panel in front of you (no double vision).
- **Characters stand on the ground** they walk on (FEZ is 2D underneath; the mod draws Gomez and NPCs at the depth of
  the block they stand on). Water, transitions and the opening zoom are adapted to 3D as well.
- **Motion controllers** through SteamVR Input (default bindings for Valve Index, Oculus Touch and Vive wands), mapped
  to FEZ's gamepad controls. A regular gamepad keeps working too.
- **One-click installer.** Afterwards you play from Steam's normal **FEZ** entry, which also shows under **VR** in
  the SteamVR library. FEZ's own files are never changed.

Tested on a Bigscreen Beyond with Valve Index controllers. Other headsets that work with SteamVR should work, but
are untested.

## Requirements

- FEZ (Steam; the current PC version 1.12), on Windows, or on Linux through Proton (untested)
- SteamVR and a VR headset (motion controllers or a gamepad)

## Install

### Windows

1. Download `FEZ-VR-Setup-v*.exe` from [Releases](../../releases).
2. Close FEZ and double-click the installer. It finds FEZ in your Steam libraries (or use **Browse...**) and installs
   with one click. If Windows SmartScreen warns about an unknown app, click **More info > Run anyway**.
3. If Steam is open, the installer offers to restart it so FEZ shows up in SteamVR's **VR** library tab. If you say
   no, it's listed the next time Steam closes.
4. Play **FEZ** from Steam, on the desktop or in the headset. It starts in VR and starts SteamVR if needed.

The installer only adds files next to `FEZ.exe`: `FEZ-VR.exe` with its libraries, and `FEZ.exe.config`, which tells
.NET to load the mod into FEZ when it starts.

Upgrading from v0.1.0: just run the installer. It removes the old launcher, shortcuts and SteamVR entry. Remove the old
**FEZ VR** entry from your Steam library yourself (right-click > Manage > Remove non-Steam game).

**Uninstall:** run **FEZ-VR Uninstall.exe** in the FEZ folder (or the setup exe again and click **Uninstall**). It
removes everything the mod added. Deleting `FEZ.exe.config` alone also turns the mod off.

### Linux / Steam Deck (untested)

1. In Steam, right-click **FEZ > Properties > Compatibility**, tick **Force the use of a specific Steam Play
   compatibility tool**, and pick Proton. FEZ has a native Linux version, but the mod needs the Windows one.
2. Add the downloaded `FEZ-VR-Setup-v*.exe` to Steam with **Add a Non-Steam Game**, set it to Proton the same way, and
   start it from Steam. It finds FEZ in your Linux Steam libraries.
3. Set FEZ's **Launch Options** to (the installer has a **Copy launch option** button):
   ```
   bash -c 'exec "${@/FEZ.exe/FEZ-VR.exe}"' -- %command%
   ```
   This starts the mod's own host, `FEZ-VR.exe`, in place of `FEZ.exe`. Proton's .NET may not use `FEZ.exe.config`.

SteamVR for Linux is needed. The VR library tab listing is Windows only.

### With UniGetUI or Scoop

FEZ-VR is in the [scoop-vr-mods](https://github.com/technewsguy/scoop-vr-mods) bucket (`scoop install vrmods/fez-vr`,
or through UniGetUI). It runs the same installer silently; uninstalling the package removes the mod.

### Command line

`FEZ-VR-Setup.exe /S` installs silently (log in `%TEMP%\fezvr-setup.log`). Add `/UNINSTALL` to remove the mod, and
`/GAMEDIR="<folder with FEZ.exe>"` if FEZ isn't found.

## Controls (Valve Index)

| Control | FEZ action (gamepad button) |
|---|---|
| Left stick | Walk, climb, menus |
| Right A / B | Jump, accept (A) / cancel, back (B) |
| Left A / B | Grab, throw, talk, open (X) / inventory (Y) |
| Left / right trigger | Rotate the world left / right (LT / RT) |
| Left trackpad press | Map (Back) |
| Right trackpad press | Pause (Start) |
| Right stick | Look around in first-person view |
| Left / right stick click | First-person view / clamp look |
| Right stick up / down (map open) | Map zoom in / out (RB / LB) |
| Index system button (hold) | Recenter the view (SteamVR) |
| F8 (keyboard) | Recenter the view |
| F7 (keyboard) | Save the eye images to `fezvr_dump` next to `FEZ.exe` |

Bindings can be changed in SteamVR's controller settings under "FEZ".

## Feedback

Please open an [issue](../../issues) with:

- what happened and what you expected, and where in the game,
- your headset and controllers,
- `fezvr.log` from the FEZ folder, and F7 images if it is something you can see.

Known rough spots: only the first part of the game has been played in VR so far; some effects and cutscenes later
on may still need 3D treatment; the first-person view mode is untested in VR.

## Building from source

`build.ps1 -GameDir "<a FEZ folder>" [-Install]` compiles `src\*.cs` against the game's assemblies (C# 5, .NET 4,
Windows `csc`, x86) and builds `build\FEZ-VR-Setup.exe` with the mod embedded. `package.ps1` makes the release files
in `dist\`. See `NOTES.md` for how it works and the gotchas found
along the way.

## Disclaimer

Fan-made and unofficial. Not affiliated with or endorsed by Polytron. You need your own copy of FEZ; this
repository contains no game code or assets.

## License

MIT for this mod's code (see `LICENSE`). Third-party components: see `THIRD_PARTY.md`.
