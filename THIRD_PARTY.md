# Third-party components

- **OpenVR SDK** (Valve Corporation) - `deps/openvr/openvr_api.cs` (C# bindings) and the native x86
  `openvr_api.dll` (`deps/openvr/openvr_api_x86.dll`). BSD 3-Clause License,
  https://github.com/ValveSoftware/openvr/blob/master/LICENSE
- **HarmonyX** (BepInEx team, based on Harmony by Andreas Pardeike) - `deps/harmony/0Harmony.dll`. MIT,
  https://github.com/BepInEx/HarmonyX
- **MonoMod** (0x0ade and contributors) - `MonoMod.RuntimeDetour.dll`, `MonoMod.Utils.dll`. MIT,
  https://github.com/MonoMod/MonoMod
- **Mono.Cecil** (Jb Evain) - `Mono.Cecil*.dll`. MIT, https://github.com/jbevain/cecil

The libraries in `deps/harmony` (HarmonyX 2.9, MonoMod 22.1.29.1, Mono.Cecil 0.10.4) are the copies shipped with BepInEx 5.

FEZ and its assets belong to Polytron Corporation. This repository contains none of them; the Steam artwork used for
the "FEZ VR" library entry is copied from the user's own Steam cache or Steam's public store CDN at install time.
