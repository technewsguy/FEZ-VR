# FEZ-VR

An unofficial VR mod for **FEZ** (Steam, PC). The game becomes a little **3D diorama** floating in front of you: you
see FEZ's world as the real 3D model it always was, from your own head position, while it still plays exactly like
FEZ.

> Early test version. It is playable, but expect rough edges. Please report what you find (see Feedback below).

FEZ has no VR support of its own. This mod hosts the game in its own process, renders every frame twice from your
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
- A **"FEZ VR" entry in Steam and SteamVR** with FEZ's artwork, and "FEZ VR" desktop / Start menu shortcuts. Regular
  flat FEZ stays as it was.

Tested on a Bigscreen Beyond with Valve Index controllers. Other headsets that work with SteamVR should work, but
are untested.

## Requirements

- FEZ (Steam, Windows; the current PC version 1.12)
- SteamVR and a VR headset (motion controllers or a gamepad)

## Install

1. Download the latest `FEZ-VR-v*.zip` from [Releases](../../releases) and unzip it anywhere.
2. Close FEZ. Double-click `install.cmd` (or run `install.ps1` in PowerShell).
   - It finds FEZ through Steam; otherwise run `install.ps1 -GameDir "<folder with FEZ.exe>"`.
   - It copies the mod next to `FEZ.exe` (`FEZ-VR.exe` and its libraries). FEZ's own files are not changed.
   - It adds **FEZ VR** to your Steam library (with "Include in VR Library" on and FEZ's artwork) and to SteamVR's
     library, plus desktop and Start menu shortcuts. **Steam is closed and restarted** for this
     (`-NoSteamEntry` skips it).
3. Play **FEZ VR** from Steam, SteamVR or the shortcut. It starts SteamVR if needed.

Also available from the [scoop-vr-mods](https://github.com/technewsguy/scoop-vr-mods) Scoop bucket
(`scoop install vrmods/fez-vr`, or through UniGetUI).

**Uninstall:** run `uninstall.cmd`. It removes the mod files and the shortcuts; remove the "FEZ VR" Steam entry in
Steam (right-click > Manage > Remove non-Steam game).

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

Bindings can be changed in SteamVR's controller settings under "FEZ VR".

## Feedback

Please open an [issue](../../issues) with:

- what happened and what you expected, and where in the game,
- your headset and controllers,
- `fezvr.log` from the FEZ folder, and F7 images if it is something you can see.

Known rough spots: only the first part of the game has been played in VR so far; some effects and cutscenes later
on may still need 3D treatment; the first-person view mode is untested in VR.

## Building from source

`build.ps1 -GameDir "<a FEZ folder>" [-Install]` compiles `src\*.cs` against the game's assemblies (C# 5, .NET 4,
Windows `csc`, x86). `package.ps1` builds the release zip. See `NOTES.md` for how it works and the gotchas found
along the way.

## Disclaimer

Fan-made and unofficial. Not affiliated with or endorsed by Polytron. You need your own copy of FEZ; this
repository contains no game code or assets.

## License

MIT for this mod's code (see `LICENSE`). Third-party components: see `THIRD_PARTY.md`.
