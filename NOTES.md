# FEZ-VR - evaluation notes (2026-11-13)

## Target
- FEZ (Steam 224760) at `C:\Program Files (x86)\Steam\steamapps\common\FEZ`, PC 1.12 (FNA, not MonoGame/XNA).
- `FEZ.exe` + `FezEngine.dll`: .NET Framework 4 (CLR v4.0.30319), C#. Native libs (SDL2, steam_api, MojoShader) are
  x86 -> the game runs as a 32-bit process.
- `FNA.dll` 16.12 (Dec 2016): OpenGL backend built into FNA (pre-FNA3D), MojoShader for effects.
- Decompiled for study: `_decomp\FezEngine`, `_decomp\FEZ` (ilspycmd 8.2, 724 files). Never publish.

## Engine facts
- `FezEngine.Viewpoint`: Front/Right/Back/Left (orthographic) + `Perspective`.
  `DefaultCameraManager` builds `Matrix.CreateOrthographic` for side views, `CreatePerspectiveFieldOfView` for
  Perspective; `GameCameraManager.FirstPersonFov` = 75 deg; `FirstPersonView` component (fisheye RT effect).
- Many layers force their own projection (`Effect.ForcedProjectionMatrix`: SkyHost stars/background layers,
  FarawayPlaceHost, end cutscene) - screen-space-like layers that will need per-eye handling.
- 44 `SetRenderTarget` calls, 15 to `null` (back buffer); `TargetRenderingManager` hands out RTs for post effects.
- Gameplay/collision is 2D in the current view plane; LevelMaterializer culls by viewpoint.

## Prior art
- HoloFEZ (0x0ade, itch.io / github 0x0ade/holofez): Unity "FezUnity" reimplementation that loads FEZ content and
  shows the first-person view in VR. Proof of concept, not the real game / no platforming gameplay.
- HAT (FEZModding/HAT): MonoMod-based mod loader for PC 1.12 (patched exe + Mods folder, plugin DLLs).

## Approach (proposed)
- No engine VR: own OpenVR submit (NSLT OwnVR experience) of OpenGL textures from FNA RenderTarget2D handles.
- Loader: our own x86 launcher exe that loads FEZ.exe, applies Harmony patches, calls the game's Main (no game
  files changed), or a HAT plugin.
- Presentation: "diorama" - the level as a 3D model in front of the player, rendered per eye in perspective from the
  tracked head, orbiting/rotating with the game's 90-degree view turns; 2D UI on a panel; first-person mode = true
  head-tracked first person.

## Spike stage 1 (2026-11-13): frames reach the headset
- Test copy T:\FEZ-VR\game (+ steam_appid.txt 224760). `build.ps1 -Install` builds `FEZ-VR.exe` (x86, .NET 4, csc v4)
  into the game folder with HarmonyX + MonoMod (from BepInEx 5 core) and x86 openvr_api.dll. FEZ-VR.exe patches, then
  invokes FEZ.exe's entry point in-process. Log: game\fezvr.log.
- Patches: Game.BeginDraw postfix (WaitGetPoses, bind our RenderTarget2D), GraphicsDevice.SetRenderTargets prefix
  (null/empty = our RT while redirecting), Game.EndDraw prefix (Submit GL texture handle from Texture.texture ->
  OpenGLTexture.Handle, bounds vMin=1/vMax=0, then SpriteBatch mirror to the window).
- Result: OpenVR init OK (eye 2688x2688), Submit L/R = None, title screen visible in both eyes right side up
  (runs\s1_vr.png). ~70-80 submits/s; the flat image fills each eye (expected at this stage).
- Fez pauses on focus loss only if Settings.PauseOnLostFocus; drawing kept going here.


## Spike stage 2 (2026-11-13): diorama renders
- Per eye: CameraManager protected view/projection fields replaced (world = gameView * T(0,0,249.875 ortho) * S(1.4 m /
  ortho width 2/M11) * T(0,-0.15,-1.4); view = world * inv(eyeToHead * headPose); proj = PerspectiveOffCenter from
  GetProjectionRaw with -b/-t), then ONLY BaseEffect.RefreshViewProjection delegates are invoked. Firing OnViewChanged/
  OnProjectionChanged re-culls the level (LevelMaterializer.CullEverything) and crashed (NaN in FezMath.Sign).
- Right eye = Game.Draw invoked again from the EndDraw prefix; camera restored afterwards.
- Result runs\d6_vr.png: Gomez on a platform in real perspective 3D, both eyes. Sky/background black; menus/HUD still
  full-eye 2D.
- 10 fps / WaitGetPoses ~98 ms / mispresented + dropped 8 = HMD activity Idle (not worn), NOT a bug. Logged as hmd=.
- Pacing: InactiveSleepTime 0 (FNA sleeps 20 ms when unfocused); windowed 1920x1080 mirror (FEZVR_PACING env:
  'fullscreen', 'var', 'novsync'). glFlush before Submit + PostPresentHandoff. F8 = ResetZeroPose (GetAsyncKeyState).
- Gotcha: Fez debug log in %APPDATA%\FEZ\[date] Debug Log #n.txt (brackets: use -LiteralPath).


- CORRECTION 2026-11-13: the 10 fps / gray headset was SteamVR's compositor stuck after the Beyond display went
  off (vrcompositor.txt: 'LiquidVR::WaitForPresent failed!' every 100 ms from 11:06, 'Failed to wait for GPU work').
  Not idle throttling, not our code. Fix: restart SteamVR. After restart: 90 fps, 0 dropped, draw 0.8 ms, wait 2.5 ms.
  Check vrcompositor.txt for WaitForPresent failures first whenever the headset is gray.
- runs\d7_vr.png: diorama room (3D frame box, Gomez, glowing artifact) floating in black, both eyes.


## Passes (2026-11-13)
- Per frame: Sky pass (SkyHost only, game camera) -> SkyRT; Flat pass (types: SpeechBubble, HeadsUpDisplay, Intro,
  LoadingScreen, SaveIndicator, PolytronLogo, FakeDot, StarField, *Menu*; Clear forced Transparent) -> FlatRT;
  Eye passes (all other components, diorama camera). Components hidden by toggling Visible, restored after.
- GraphicsDevice.Clear(ClearOptions,Color,float,int) postfix draws the sky quad (6 m, 16 m wide) right after Fez.Draw
  clears an eye target; the flat panel (1.2 m, 1.6 m wide, alpha) is drawn on each eye after its pass. Quads use
  BasicEffect with view = inv(eyeToHead*head), eye projection.
- Result (runs\e1/e2): title on a 3D panel in both eyes; world room drawn in both eyes; 90 fps, draw 1.5 ms.
- Fez.Draw now runs 4x per frame (TimeInterpolation.ProcessCallbacks / DrawActionScheduler each time) - watch for
  double-speed effects.


## Double vision / washed out (2026-11-13) - fixed
- Toggling DrawableGameComponent.Visible to pick components per pass crashed (LiquidHost KeyNotFound: the game hides it
  during loads, our restore re-showed it). Now a transpiler on Game.Draw replaces IDrawable.Draw with VR.DrawFiltered.
- Cause of the ghost/washed-out look: frame 'hooks' (TargetRenderingManager.OnRtPrepare redirects the whole Fez.Draw into
  GammaCorrection/BurnIn targets; the component resolves it when it draws). Sky/flat passes skipped those components, so
  the hook swallowed the pass and leaked into the eye pass. OnRtPrepare is now skipped in sky/flat passes. BurnIn would
  ghost the other eye (it was Enabled=false here anyway; we disable it if enabled).
- After the fix the raw eye dump (fezvr_dump\114155_*) shows Gomez's room fully textured in 3D.
- F7 = dump raw left/right/sky/flat images (System.Drawing; FNA SaveAsPng needs SDL2_image, missing).


## THE double-vision bug (2026-11-13)
- Vertical projection offset had the wrong sign: CreatePerspectiveOffCenter(l, r, -b, -t) gave M32 = +0.151 while
  SteamVR's GetProjectionMatrix row1 col2 = -0.151. Correct: CreatePerspectiveOffCenter(l*n, r*n, t*n, b*n, n, f)
  (view-space y from t to b). Every eye image was shifted vertically by a different amount per eye.
- Found with FEZVR_ALIGNTEST=1: SteamVR overlay (red, 20 cm, seated (0,0,-1)) vs our green quad at the same spot; the
  user saw green below red. The dump-vs-raw-tangent check could not catch it (same wrong assumption). Overlays are not
  in SteamVR stereo screenshots. Log now prints SteamVR's GL projection next to ours - they must match.


- User: double vision gone. World 'blinks out when Gomez moves': the game's camera follow runs inside Fez.Draw
  (TimeInterpolation callbacks -> DefaultCameraManager.OnViewChanged) and overwrote the eye view mid-pass. Postfixes on
  DefaultCameraManager.OnViewChanged/OnProjectionChanged now adopt the game's new camera and re-apply the eye matrices.
- Dump 122659: village tower diorama with sky backdrop, both eyes - the intended look.

  First attempt (postfix on OnViewChanged) crashed: the game's interpolation re-culled with our eye view loaded (NaN).
  Final: prefix/postfix on TimeInterpolation.ProcessCallbacks and DrawActionScheduler.Process restore the game's
  view/projection during that work, then adopt the result as the diorama reference and re-apply the eye.


- Pop-in at the diorama edges (ground below appears late): LevelMaterializer.CullInstances keeps triles inside
  CameraManager.Frustum (+1). Prefix on DefaultCameraManager.RebuildFrustum: ortho views use view*proj*Scale(1/CullScale)
  (CullScale 3) so the culling volume is 3x wider/taller; rendering unchanged. Note: single-face culling (ortho shows
  one face per trile) may show missing faces when the diorama is seen off-axis.


- 'Only black': game at the title, but the seated zero pose came from the headset lying on the desk at launch, so the
  panel was behind the user (dump eyeWorld z axis pointing backwards). F8 fixes it; new build (not yet installed while
  running) auto-recenters once when the HMD first reports UserInteraction for > 1 s.


- Desktop mirror = left eye + FlatRT (menus/text) alpha-blended, so prompts are visible on the monitor.
- SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS=1 set in the launcher before SDL init (gamepad input while unfocused).


- Desktop window was black in VR runs: the runtime fullscreen->windowed ApplyChanges in the pacing patch broke the
  window's output (black even with VR drawing off). Removed (FEZVR_PACING=windowed only). Exclusive fullscreen is fine;
  the earlier 10 fps was the SteamVR compositor fault. Mirror (left eye + FlatRT) now shows on the monitor, 90 fps.
  GL bindings are saved/restored around Submit (GlState) - harmless. Fixed a crash: iterating g.Components while
  the game loads (now index loop with try/catch, once a second).



- Panel lowered (PanelDrop 0.25) for the first-room instruction text. IntroZoomIn (screen-space UV zoom 200->1 about the
  screen centre = per-eye double image): Draw prefix forces Scale 1 and shrinks the diorama by 1/Scale instead (3D zoom).


- First-room instructions are TextScroll (screen-space scroll banner, identity projection) - it was drawn in the eye pass at
  the top edge of each eye. TextScroll + WorldMap now go to the flat panel. Panel drop back to 0.05. PgUp/PgDn removed (the
  game's menus use them).


- Letters (LetterViewer, fullscreen image) were per-eye double vision -> flat panel, with AchievementSellScreen, FakeIntro,
  FakeOutro, Reboot.


- Gomez floated in front of/behind the walkway (FEZ keeps his depth; 2D collision). GomezOnGround: prefix on
  GomezHost.DoDraw_Internal/PreDraw draws PlayerMesh at the depth of Ground.First (smoothed 80 ms, held while airborne,
  reset on view turn); restored in the postfix, gameplay position untouched.


- Diorama depth flattened to 0.5 (DepthScale, view-space z scale about the ortho focus plane) - user: layers too far apart.


- DepthScale 0.35. NpcGround: per frame (eye passes only) each NpcState.Group is moved along the view axis to the depth of
  LevelManager.NearestTrile(feet - 0.5Y, vp).Surface ?? Deep; restored after the frame. (Uncle walked in mid air.)


- Pre-fez levels must look flat: DepthScale eases (0.6 s) to 0.03 while !PlayerManager.CanRotate or LevelManager.Flat,
  else 0.35. EldersHexahedron Phase.Yay sets CanRotate = true when the fez is collected.


- Water (LiquidHost): LiquidMesh = 150-block box, AlwaysOnTop in ortho views -> in the diorama its top face covered the
  islands ('water too high'). Eye passes: DoDraw/DrawLights prefix scales the box's depth axis to the level depth
  (centred on the level), and a Mesh.Draw prefix forces AlwaysOnTop=false for LiquidMesh/RaysMesh (game does the same in FPS view).


- Faraway-place transition ('world came into focus with double vision'): FarawayPlaceHost / LiquidHost transition use a
  forced world-sized ortho projection + a captured view. NoForcedOrthoInEyes: BaseEffect.Prepare(Mesh/Group) prefix drops
  forced ortho (|M11|<0.5, M34=0) and forced view-only matrices in eye passes; screen-space ones untouched.


- Water foam strip (FoamMesh) is drawn 3x offset +-48 in X and Z (tiling for wide views) at the camera-centre depth -> in
  3D three lines at different depths. FoamAtFront: Mesh.Draw prefix/postfix pins its depth to the water box's front face.


## VR controls (2026-11-14)
- src\VrInput.cs: SteamVR Input (input\actions.json + knuckles/oculus/vive bindings, installed to game\fezvr_input) read once
  per frame after WaitGetPoses; postfix on FNA GamePad.GetState(PlayerIndex[, DeadZone]) merges it into player one's pad
  (OR buttons, larger stick/trigger). Map: L stick move, R A=A jump, R B=B, L A=X grab/talk, L B=Y inventory, triggers =
  LT/RT rotate, trackpad clicks = Back (map) / Start (pause), stick clicks. Grips unused (user); recenter = Index system
  button. Map zoom (LB/RB) = right stick up/down while GameState.InMap.


