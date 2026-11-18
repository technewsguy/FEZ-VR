using System.Collections.Generic;
// FEZ-VR: host FEZ.exe in our own x86 process, patch it with Harmony, and render it to SteamVR as a diorama.
// Each frame is drawn twice (left eye during the game's own Draw, right eye by calling Draw again before EndDraw).
// For each eye the game's camera View/Projection are replaced: the area the game camera shows becomes a model
// DioramaWidth metres wide, floating in front of the player, seen in perspective from the tracked eye.
using System;
using System.IO;
using System.Reflection;
using FezEngine.Services;
using FezEngine.Tools;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Valve.VR;

namespace FezVr
{
    // Normal use: FEZ.exe.config (written by the installer) names Boot as FEZ.exe's AppDomainManager, so .NET loads
    // this assembly into FEZ.exe and calls Boot before the game's Main. Steam's normal FEZ entry then plays in VR, and
    // none of FEZ's files are changed. FEZ-VR.exe can also be run directly (it hosts FEZ.exe), e.g. from a Linux
    // launch option where the config file may not be honoured.
    public sealed class Boot : AppDomainManager
    {
        static bool done;
        // This runs before .NET has loaded FEZ.exe as the program. Touching FEZ's types now would load FEZ.exe a second
        // time (FileLoadException at startup), so the patching waits until FEZ.exe itself has loaded, before its Main.
        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnLoad;
        }

        static void OnLoad(object sender, AssemblyLoadEventArgs e)
        {
            if (done || e.LoadedAssembly.GetName().Name != "FEZ") return;
            done = true;
            AppDomain.CurrentDomain.AssemblyLoad -= OnLoad;
            try { Launcher.Start("FEZ.exe"); } catch (Exception x) { Log.Info("start failed: " + x); }
        }
    }

    public static class Launcher
    {
        public const string Version = "0.2.0";
        public static string Dir;

        [STAThread]
        public static int Main(string[] args)
        {
            Start("FEZ-VR.exe");
            MethodInfo entry = typeof(FezGame.Fez).Assembly.EntryPoint;
            entry.Invoke(null, entry.GetParameters().Length == 1 ? new object[] { args } : null);
            return 0;
        }

        public static void Start(string how)
        {
            Dir = AppDomain.CurrentDomain.BaseDirectory;
            Environment.CurrentDirectory = Dir;
            // Steam sets these when it starts FEZ; when FEZ-VR.exe is started any other way Steamworks must still see FEZ's id.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamAppId"))) Environment.SetEnvironmentVariable("SteamAppId", "224760");
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamGameId"))) Environment.SetEnvironmentVariable("SteamGameId", "224760");
            // In VR the game window is never focused; SDL drops gamepad input for unfocused windows unless told not to.
            Environment.SetEnvironmentVariable("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            Log.Clear();
            Log.Info("FEZ-VR " + Version + " starting in " + Dir + " (via " + how + ")");
            try
            {
                var harmony = new Harmony("fezvr");
                harmony.PatchAll(typeof(Launcher).Assembly);
                Log.Info("patches applied");
            }
            catch (Exception e) { Log.Info("patching failed: " + e); }
            CheckSteamVRSection();
        }

        // Steam drops the game from SteamVR's VR library tab when Valve updates FEZ's store info. If so, the uninstaller
        // copy waits in the background for Steam to close and lists it again (see installer\SteamVRTag.cs). It is
        // started through WMI so it isn't part of the game's process tree (Steam would think the game is still running).
        static void CheckSteamVRSection()
        {
            var t = new System.Threading.Thread(() =>
            {
                try
                {
                    if (Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Wine") != null) return;
                    if (SteamVRTag.IsTagged() != false) return;
                    string helper = System.IO.Path.Combine(Dir, "FEZ-VR Uninstall.exe");
                    if (!File.Exists(helper)) return;
                    SteamVRTag.StartWaiter(helper);
                    Log.Info("Steam dropped FEZ from its VR library tab; it's listed again after Steam closes.");
                }
                catch (Exception e) { Log.Info("Steam VR section check failed: " + e.Message); }
            });
            t.IsBackground = true; t.Start();
        }
    }

    // Saves/restores the GL bindings that OpenVR's OpenGL submit path may change.
    static class GlState
    {
        const int GL_DRAW_FRAMEBUFFER_BINDING = 0x8CA6, GL_READ_FRAMEBUFFER_BINDING = 0x8CAA, GL_TEXTURE_BINDING_2D = 0x8069,
            GL_ACTIVE_TEXTURE = 0x84E0, GL_DRAW_FRAMEBUFFER = 0x8CA9, GL_READ_FRAMEBUFFER = 0x8CA8, GL_TEXTURE_2D = 0x0DE1,
            GL_RENDERBUFFER_BINDING = 0x8CA7, GL_RENDERBUFFER = 0x8D41, GL_ARRAY_BUFFER_BINDING = 0x8894, GL_ARRAY_BUFFER = 0x8892,
            GL_ELEMENT_ARRAY_BUFFER_BINDING = 0x8895, GL_ELEMENT_ARRAY_BUFFER = 0x8893, GL_CURRENT_PROGRAM = 0x8B8D;
        [System.Runtime.InteropServices.DllImport("opengl32.dll")] static extern void glGetIntegerv(int pname, int[] data);
        [System.Runtime.InteropServices.DllImport("opengl32.dll")] static extern void glBindTexture(int target, int tex);
        [System.Runtime.InteropServices.DllImport("opengl32.dll")] static extern IntPtr wglGetProcAddress(string name);
        delegate void BindFn(int target, int id);
        delegate void OneFn(int v);
        static BindFn bindFramebuffer, bindRenderbuffer, bindBuffer;
        static OneFn activeTexture, useProgram;
        static readonly int[] v = new int[1];
        static int draw, read, tex, active, rb, ab, eab, prog;
        static bool ok;

        static T Fn<T>(string name) where T : class
        {
            IntPtr p = wglGetProcAddress(name);
            return p == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }
        static int Get(int pname) { v[0] = 0; glGetIntegerv(pname, v); return v[0]; }

        public static void Save()
        {
            if (bindFramebuffer == null)
            {
                bindFramebuffer = Fn<BindFn>("glBindFramebuffer"); bindRenderbuffer = Fn<BindFn>("glBindRenderbuffer");
                bindBuffer = Fn<BindFn>("glBindBuffer"); activeTexture = Fn<OneFn>("glActiveTexture"); useProgram = Fn<OneFn>("glUseProgram");
            }
            ok = bindFramebuffer != null && activeTexture != null;
            if (!ok) return;
            draw = Get(GL_DRAW_FRAMEBUFFER_BINDING); read = Get(GL_READ_FRAMEBUFFER_BINDING); active = Get(GL_ACTIVE_TEXTURE);
            tex = Get(GL_TEXTURE_BINDING_2D); rb = Get(GL_RENDERBUFFER_BINDING); ab = Get(GL_ARRAY_BUFFER_BINDING);
            eab = Get(GL_ELEMENT_ARRAY_BUFFER_BINDING); prog = Get(GL_CURRENT_PROGRAM);
        }

        public static void Restore()
        {
            if (!ok) return;
            bindFramebuffer(GL_DRAW_FRAMEBUFFER, draw); bindFramebuffer(GL_READ_FRAMEBUFFER, read);
            activeTexture(active); glBindTexture(GL_TEXTURE_2D, tex);
            if (bindRenderbuffer != null) bindRenderbuffer(GL_RENDERBUFFER, rb);
            if (bindBuffer != null) { bindBuffer(GL_ARRAY_BUFFER, ab); bindBuffer(GL_ELEMENT_ARRAY_BUFFER, eab); }
            if (useProgram != null) useProgram(prog);
        }
    }

    public static class Log
    {
        static string Path { get { return System.IO.Path.Combine(Launcher.Dir ?? ".", "fezvr.log"); } }
        public static void Clear() { try { File.WriteAllText(Path, ""); } catch (Exception) { } }
        public static void Info(string s) { try { File.AppendAllText(Path, DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\r\n"); } catch (Exception) { } }
    }

    public static class VR
    {
        // Diorama placement in seated tracking space (metres): centre of the view DioramaDistance in front of the
        // starting head position, slightly below eye level; the game's visible width maps to DioramaWidth.
        public static float DioramaWidth = 1.4f, DioramaDistance = 1.4f, DioramaDrop = 0.15f;
        public static float Near = 0.05f, Far = 100f;
        public static float CullScale = 3f;   // culling frustum size vs the game's view (diorama shows past the edges)

        public static bool Ready, Failed, Redirect;
        static readonly bool Mono = Environment.GetEnvironmentVariable("FEZVR_MONO") == "1";
        public static int EyeIndex;
        public static readonly RenderTarget2D[] Eyes = new RenderTarget2D[2];
        static SpriteBatch mirror;
        static int frames, lastFrames;
        static System.Diagnostics.Stopwatch fpsWatch;
        static readonly System.Diagnostics.Stopwatch t = new System.Diagnostics.Stopwatch();
        static double drawMs, waitMs;
        static readonly TrackedDevicePose_t[] render = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        static readonly TrackedDevicePose_t[] gamePoses = new TrackedDevicePose_t[0];
        static readonly FieldInfo TexField = AccessTools.Field(typeof(Texture), "texture");
        static readonly FieldInfo ViewField = AccessTools.Field(typeof(CameraManager), "view");
        static readonly FieldInfo ProjField = AccessTools.Field(typeof(CameraManager), "projection");
        static readonly MethodInfo OnView = AccessTools.Method(typeof(CameraManager), "OnViewChanged");
        static readonly MethodInfo OnProj = AccessTools.Method(typeof(CameraManager), "OnProjectionChanged");
        static readonly MethodInfo DrawMethod = AccessTools.Method(typeof(Game), "Draw");
        static readonly FieldInfo GameTimeField = AccessTools.Field(typeof(Game), "gameTime");
        static PropertyInfo handleProp;
        static readonly Matrix[] eyeToHead = new Matrix[2], eyeProj = new Matrix[2];
        static Matrix headPose = Matrix.Identity;
        static CameraManager cam;
        static Matrix savedView, savedProj;
        static bool saved;

        static void Init()
        {
            if (Ready || Failed) return;
            if (Environment.GetEnvironmentVariable("FEZVR_OFF") == "1") { Failed = true; Log.Info("VR off (FEZVR_OFF)"); return; }
            var err = EVRInitError.None;
            OpenVR.Init(ref err, EVRApplicationType.VRApplication_Scene);
            if (err != EVRInitError.None) { Failed = true; Log.Info("OpenVR init failed: " + err); return; }
            OpenVR.Compositor.SetTrackingSpace(ETrackingUniverseOrigin.TrackingUniverseSeated);
            OpenVR.Chaperone.ResetZeroPose(ETrackingUniverseOrigin.TrackingUniverseSeated);
            for (int e = 0; e < 2; e++)
            {
                EVREye eye = e == 0 ? EVREye.Eye_Left : EVREye.Eye_Right;
                eyeToHead[e] = ToXna(OpenVR.System.GetEyeToHeadTransform(eye));
                float l = 0, r = 0, t = 0, b = 0;
                OpenVR.System.GetProjectionRaw(eye, ref l, ref r, ref t, ref b);
                // Matches SteamVR's own GetProjectionMatrix (checked: GL row 1 col 2 == XNA M32, e.g. -0.151): in view
                // space (y up) the frustum spans y from t to b, so bottom = t*near and top = b*near.
                eyeProj[e] = Matrix.CreatePerspectiveOffCenter(l * Near, r * Near, t * Near, b * Near, Near, Far);
                HmdMatrix44_t gm = OpenVR.System.GetProjectionMatrix(eye, Near, Far);
                Log.Info(string.Format("eye {0} SteamVR GL proj rows: [{1:0.000} {2:0.000} {3:0.000} {4:0.000}] [{5:0.000} {6:0.000} {7:0.000} {8:0.000}]  mine XNA: M11={9:0.000} M31={10:0.000} M22={11:0.000} M32={12:0.000}",
                    e, gm.m0, gm.m1, gm.m2, gm.m3, gm.m4, gm.m5, gm.m6, gm.m7, eyeProj[e].M11, eyeProj[e].M31, eyeProj[e].M22, eyeProj[e].M32));
                Log.Info("eye " + e + " tangents l=" + l + " r=" + r + " t=" + t + " b=" + b + " ipd x=" + eyeToHead[e].M41);
            }
            Ready = true;
            RemoveOldLibraryEntry();
            VrInput.Init(Launcher.Dir);
            if (Environment.GetEnvironmentVariable("FEZVR_ALIGNTEST") == "1") SetupAlignTest();
        }

        // v0.1.0 had its own "FEZ VR" SteamVR library entry. The installer removes it while SteamVR is closed; if
        // SteamVR was running then, it left the manifest file behind and the entry is removed here.
        static void RemoveOldLibraryEntry()
        {
            try
            {
                string ld = System.IO.Path.Combine(Launcher.Dir, "FEZVR-launcher");
                string manifest = System.IO.Path.Combine(ld, "fezvr.vrmanifest");
                if (!File.Exists(manifest)) return;
                // Not gated on IsApplicationInstalled: right after SteamVR starts it can report the entry as missing
                // while appconfig.json still lists the manifest.
                var r = OpenVR.Applications != null ? OpenVR.Applications.RemoveApplicationManifest(manifest) : EVRApplicationError.InvalidParameter;
                Log.Info("old SteamVR library entry: " + r);
                Directory.Delete(ld, true);
            }
            catch (Exception e) { Log.Info("old SteamVR entry cleanup failed: " + e.Message); }
        }

        // ---- Alignment test: SteamVR draws a red 20 cm square (an overlay, always correct per eye) at MarkerPos; we draw
        // a green 10 cm square at the same spot with our own eye matrices. If ours are right, green sits centred in red.
        static readonly Vector3 MarkerPos = new Vector3(0f, 0f, -1.0f);
        static bool alignTest;
        static Texture2D greenTex;
        static ulong overlay;

        static void SetupAlignTest()
        {
            try
            {
                string png = System.IO.Path.Combine(Launcher.Dir, "fezvr_red.png");
                using (var bmp = new System.Drawing.Bitmap(64, 64))
                {
                    using (var gr = System.Drawing.Graphics.FromImage(bmp)) { gr.Clear(System.Drawing.Color.Red); gr.FillRectangle(System.Drawing.Brushes.White, 31, 0, 2, 64); gr.FillRectangle(System.Drawing.Brushes.White, 0, 31, 64, 2); }
                    bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                }
                OpenVR.Overlay.CreateOverlay("fezvr.aligntest", "FEZ-VR align test", ref overlay);
                OpenVR.Overlay.SetOverlayFromFile(overlay, png);
                OpenVR.Overlay.SetOverlayWidthInMeters(overlay, 0.2f);
                var m = new HmdMatrix34_t { m0 = 1, m5 = 1, m10 = 1, m3 = MarkerPos.X, m7 = MarkerPos.Y, m11 = MarkerPos.Z };
                OpenVR.Overlay.SetOverlayTransformAbsolute(overlay, ETrackingUniverseOrigin.TrackingUniverseSeated, ref m);
                OpenVR.Overlay.ShowOverlay(overlay);
                alignTest = true;
                Log.Info("align test: SteamVR red overlay + our green quad at " + MarkerPos);
            }
            catch (Exception e) { Log.Info("align test setup failed: " + e.Message); }
        }

        static void DrawAlignMarker(GraphicsDevice gd, int eye)
        {
            if (!alignTest) return;
            if (greenTex == null) { greenTex = new Texture2D(gd, 1, 1); greenTex.SetData(new[] { Color.Lime }); }
            DrawQuad(gd, eye, greenTex, MarkerPos, 0.1f, BlendState.Opaque);
        }

        // OpenVR 3x4 (column vectors) -> XNA row-vector matrix. Both right-handed, metres.
        static Matrix ToXna(HmdMatrix34_t m)
        {
            return new Matrix(m.m0, m.m4, m.m8, 0f, m.m1, m.m5, m.m9, 0f, m.m2, m.m6, m.m10, 0f, m.m3, m.m7, m.m11, 1f);
        }

        static CameraManager Cam()
        {
            if (cam == null) cam = ServiceHelper.Get<IDefaultCameraManager>() as CameraManager;
            return cam;
        }

        // Game world -> eye view for the diorama.
        static void SetEye(int e)
        {
            CameraManager c = Cam();
            if (c == null) return;
            if (!saved) { savedView = (Matrix)ViewField.GetValue(c); savedProj = (Matrix)ProjField.GetValue(c); saved = true; }
            ApplyEye(c, e);
        }

        static bool inGameWork;

        public static void BeginGameCameraWork()
        {
            CameraManager c = Cam();
            if (!Redirect || Pass != PassKind.Eye || !saved || c == null) return;
            inGameWork = true;
            ViewField.SetValue(c, savedView);
            ProjField.SetValue(c, savedProj);
        }

        public static void EndGameCameraWork()
        {
            CameraManager c = Cam();
            if (!inGameWork || c == null) return;
            inGameWork = false;
            savedView = (Matrix)ViewField.GetValue(c);
            savedProj = (Matrix)ProjField.GetValue(c);
            ApplyEye(c, EyeIndex);
        }

        static Matrix lastApplied;
        public static float ZoomFactor = 1f;
        public static float DepthScale = 0.35f;   // flatten the diorama front-to-back (user: layers too far apart)
        public const float Depth3D = 0.35f, DepthFlat = 0.03f;
        static FezGame.Services.IPlayerManager depthPm;
        static FezEngine.Services.ILevelManager depthLm;
        static double depthTime = -1;
        static bool depthLogged, depthWasFlat;
        // Before the cube gives Gomez the fez (and in 2D-only levels) FEZ is meant to look flat: rotation is locked then
        // (PlayerManager.CanRotate false). Ease the diorama depth between flat and 3D.
        static void UpdateDepthScale()
        {
            double now = Time();
            float dt = depthTime < 0 ? 10f : (float)Math.Min(now - depthTime, 0.1);
            depthTime = now;
            bool flat = false;
            try
            {
                if (depthPm == null) depthPm = ServiceHelper.Get<FezGame.Services.IPlayerManager>();
                if (depthLm == null) depthLm = ServiceHelper.Get<FezEngine.Services.ILevelManager>();
                flat = (depthPm != null && !depthPm.CanRotate) || (depthLm != null && depthLm.Flat);
            }
            catch { }
            float target = flat ? DepthFlat : Depth3D;
            float k = (float)(1 - Math.Exp(-dt / 0.6));
            DepthScale += (target - DepthScale) * k;
            if (Math.Abs(target - DepthScale) < 0.001f) DepthScale = target;
            if (!depthLogged || flat != depthWasFlat) { depthLogged = true; depthWasFlat = flat; Log.Info("diorama depth -> " + (flat ? "flat" : "3D")); }
        }
        public static double zoomStamp = -10;
        public static double Now() { return Time(); }
        public static bool EyeCameraActive { get { return Redirect && Pass == PassKind.Eye && saved; } }
        public static Matrix GameView { get { return savedView; } }

        static void ApplyEye(CameraManager c, int e)
        {
            // Game view space: x right, y up, camera at the origin looking down -z, the view centre ~249.875 away
            // (orthographic side views); the orthographic width is 2 / M11.
            float width = savedProj.M11 != 0f ? Math.Abs(2f / savedProj.M11) : 30f;
            float depthToCentre = savedProj.M34 == 0f ? 249.875f : 30f;   // perspective views: centre roughly ahead
            float s = DioramaWidth / Math.Max(width, 1f);
            if (Time() - zoomStamp < 0.5) s *= ZoomFactor;   // intro zoom, done in 3D (see IntroZoomIn3D)
            Matrix world = savedView * Matrix.CreateTranslation(0f, 0f, depthToCentre) * Matrix.CreateScale(1f, 1f, DepthScale) * Matrix.CreateScale(s)
                           * Matrix.CreateTranslation(0f, -DioramaDrop, -DioramaDistance);
            Matrix eyeWorld = eyeToHead[e] * headPose;
            lastApplied = world * Matrix.Invert(eyeWorld);
            ViewField.SetValue(c, lastApplied);
            ProjField.SetValue(c, eyeProj[e]);
            RefreshEffects(c);
        }

        // Only the shaders take the per-eye matrices. Firing the camera's own change events would also re-cull the
        // level (LevelMaterializer.CullEverything) and update gameplay listeners; visibility stays the game's own.
        static readonly FieldInfo ViewEvent = AccessTools.Field(typeof(CameraManager), "ViewChanged");
        static readonly FieldInfo ProjEvent = AccessTools.Field(typeof(CameraManager), "ProjectionChanged");

        static void RefreshEffects(CameraManager c)
        {
            foreach (FieldInfo ev in new[] { ViewEvent, ProjEvent })
            {
                Delegate d = ev != null ? ev.GetValue(c) as Delegate : null;
                if (d == null) continue;
                foreach (Delegate h in d.GetInvocationList())
                    if (h.Target is FezEngine.Effects.BaseEffect && h.Method.Name == "RefreshViewProjection") h.DynamicInvoke();
            }
        }

        static void RestoreCamera()
        {
            CameraManager c = Cam();
            if (c == null || !saved) return;
            ViewField.SetValue(c, savedView);
            ProjField.SetValue(c, savedProj);
            RefreshEffects(c);
            saved = false;
        }

        static void EnsureTargets(GraphicsDevice gd)
        {
            PresentationParameters pp = gd.PresentationParameters;
            for (int e = 0; e < 2; e++)
                if (Eyes[e] == null || Eyes[e].Width != pp.BackBufferWidth || Eyes[e].Height != pp.BackBufferHeight)
                {
                    if (Eyes[e] != null) Eyes[e].Dispose();
                    // Back-buffer sized: the game sizes its own post-effect targets from the back buffer.
                    Eyes[e] = new RenderTarget2D(gd, pp.BackBufferWidth, pp.BackBufferHeight, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8, 0, RenderTargetUsage.PreserveContents);
                    Log.Info("eye target " + e + ": " + Eyes[e].Width + "x" + Eyes[e].Height);
                }
        }

        static int componentDump;
        static void DumpComponents(Game g)
        {
            var list = new System.Collections.Generic.List<string>();
            foreach (IGameComponent comp in g.Components)
            {
                var d = comp as DrawableGameComponent;
                if (d != null) list.Add(d.DrawOrder.ToString("00000") + " " + d.GetType().FullName + (d.Visible ? "" : " (hidden)"));
            }
            list.Sort();
            Log.Info("drawable components (" + list.Count + "): " + string.Join(" | ", list.ToArray()));
        }

        // ---- Passes ----------------------------------------------------------------------------------------------
        // Per frame: Sky (SkyHost only, game camera) -> SkyRT; Flat (menus/HUD/text, game camera, transparent) -> FlatRT;
        // then each eye (everything else, diorama camera). The sky image is a far backdrop and the flat image a panel,
        // both real quads in 3D so they fuse in both eyes (a full-screen blit would not, the eye frustums differ).
        public enum PassKind { None, Sky, Flat, Eye }
        public static PassKind Pass;
        public static RenderTarget2D Target;          // what "the back buffer" means during the current pass
        public static RenderTarget2D SkyRT, FlatRT;
        public static bool SkyDrawnThisPass;
        static Game theGame;

        static readonly string[] FlatTypes = { "SpeechBubble", "HeadsUpDisplay", "Intro", "LoadingScreen", "SaveIndicator", "PolytronLogo", "FakeDot", "StarField", "TextScroll", "WorldMap", "LetterViewer", "AchievementSellScreen", "FakeIntro", "FakeOutro", "Reboot" };
        static bool IsFlat(DrawableGameComponent d)
        {
            string n = d.GetType().Name;
            if (n.IndexOf("Menu", StringComparison.Ordinal) >= 0) return true;
            foreach (string s in FlatTypes) if (n == s) return true;
            return false;
        }
        static bool IsSky(DrawableGameComponent d) { return d.GetType().Name == "SkyHost"; }
        static bool IsScreenPost(DrawableGameComponent d) { return d.GetType().Name == "GammaCorrection"; }

        // Called by Game.Draw (transpiled) instead of IDrawable.Draw: which components draw in which pass. The game's
        // own Visible flags are never touched (it changes them itself, e.g. during level loads).
        public static void DrawFiltered(IDrawable d, GameTime gameTime)
        {
            var c = d as DrawableGameComponent;
            if (c != null && Redirect && Pass != PassKind.None)
            {
                bool keep;
                if (Pass == PassKind.Sky) keep = IsSky(c);
                else if (Pass == PassKind.Flat) keep = IsFlat(c);
                else keep = !IsSky(c) && !IsFlat(c);
                if (!keep) return;
            }
            d.Draw(gameTime);
        }

        static void RunPass(Game g, PassKind kind, RenderTarget2D rt, int eye)
        {
            GraphicsDevice gd = g.GraphicsDevice;
            Redirect = false; gd.SetRenderTarget(null);
            Pass = kind; Target = rt; EyeIndex = eye; SkyDrawnThisPass = false;
            if (kind == PassKind.Eye) SetEye(eye); else RestoreCamera();
            Redirect = true; gd.SetRenderTarget(rt);
        }

        static void EnsureFlatTargets(GraphicsDevice gd)
        {
            PresentationParameters pp = gd.PresentationParameters;
            if (SkyRT == null || SkyRT.Width != pp.BackBufferWidth || SkyRT.Height != pp.BackBufferHeight)
            {
                if (SkyRT != null) SkyRT.Dispose();
                if (FlatRT != null) FlatRT.Dispose();
                SkyRT = new RenderTarget2D(gd, pp.BackBufferWidth, pp.BackBufferHeight, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8, 0, RenderTargetUsage.PreserveContents);
                FlatRT = new RenderTarget2D(gd, pp.BackBufferWidth, pp.BackBufferHeight, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8, 0, RenderTargetUsage.PreserveContents);
            }
        }

        static void DrawExtraPass(Game g, PassKind kind, RenderTarget2D rt)
        {
            RunPass(g, kind, rt, 0);
            try { DrawMethod.Invoke(g, new object[] { GameTimeField.GetValue(g) }); }
            catch (Exception e) { Log.Info(kind + " pass failed: " + (e.InnerException ?? e)); }
        }

        public static void BeginFrame(Game g)
        {
            if ((componentDump == 0 && frames > 300) || (componentDump == 1 && frames > 3000)) { componentDump++; try { DumpComponents(g); } catch (Exception) { } }
            Init();
            if (!Ready) return;
            theGame = g;
            // Burn-in blends the previous frame in; with several passes per frame that is the other eye (ghosting).
            // (The component list can change while the game loads in the background: check once a second, by index.)
            if (frames % 90 == 0)
            {
                try
                {
                    for (int i = 0; i < g.Components.Count; i++)
                    {
                        var gc = g.Components[i] as GameComponent;
                        if (gc != null && gc.Enabled && gc.GetType().Name == "BurnInPostProcess") { gc.Enabled = false; Log.Info("disabled BurnInPostProcess (ghosts the other eye)"); }
                    }
                }
                catch (Exception) { }
            }
            GraphicsDevice gd = g.GraphicsDevice;
            EnsureTargets(gd);
            EnsureFlatTargets(gd);
            t.Reset(); t.Start();
            bool f8 = (GetAsyncKeyState(0x77) & 0x8000) != 0;   // F8 (works without window focus)
            if (f8 && !f8Was) { OpenVR.Chaperone.ResetZeroPose(ETrackingUniverseOrigin.TrackingUniverseSeated); Log.Info("recentered (F8)"); }
            f8Was = f8;
            // First time the headset is worn after launch: recenter (at launch it is usually lying somewhere).
            if (!autoCentered && OpenVR.System.GetTrackedDeviceActivityLevel(0) == EDeviceActivityLevel.k_EDeviceActivityLevel_UserInteraction)
            {
                if (wornSince < 0) wornSince = Time();
                else if (Time() - wornSince > 1.0) { OpenVR.Chaperone.ResetZeroPose(ETrackingUniverseOrigin.TrackingUniverseSeated); autoCentered = true; Log.Info("recentered (headset put on)"); }
            }
            else if (!autoCentered) wornSince = -1;
            bool f7 = (GetAsyncKeyState(0x76) & 0x8000) != 0;   // F7: save the raw eye / sky / panel images
            if (f7 && !f7Was) dumpNext = true;
            f7Was = f7;
            OpenVR.Compositor.WaitGetPoses(render, gamePoses);
            VrInput.Update(Time());
            waitMs = t.Elapsed.TotalMilliseconds; t.Reset(); t.Start();
            if (render[0].bPoseIsValid) headPose = ToXna(render[0].mDeviceToAbsoluteTracking);
            DrawExtraPass(g, PassKind.Sky, SkyRT);
            DrawExtraPass(g, PassKind.Flat, FlatRT);
            UpdateDepthScale();
            NpcGround.Apply(g, Cam(), (Matrix)ViewField.GetValue(Cam()));
            RunPass(g, PassKind.Eye, Eyes[0], 0);   // the game's own Draw call renders the left eye
        }

        public static void EndFrame(Game g)
        {
            if (!Ready || !Redirect) return;
            GraphicsDevice gd = g.GraphicsDevice;
            DrawPanel(gd, 0);
            try
            {
                RunPass(g, PassKind.Eye, Eyes[1], 1);
                DrawMethod.Invoke(g, new object[] { GameTimeField.GetValue(g) });
                DrawPanel(gd, 1);
            }
            catch (Exception e) { Log.Info("right eye draw failed: " + (e.InnerException ?? e)); }
            Redirect = false;
            Pass = PassKind.None;
            gd.SetRenderTarget(null);
            RestoreCamera();
            NpcGround.Restore();
            drawMs = t.Elapsed.TotalMilliseconds;
            if (dumpNext) { dumpNext = false; try { DumpImages(); } catch (Exception e) { Log.Info("dump failed: " + e.Message); } }
            try
            {
                glFlush();   // push the eye renders to the GPU before the compositor reads them
                GlState.Save();
                bool noSubmit = Environment.GetEnvironmentVariable("FEZVR_NOSUBMIT") == "1";
                EVRCompositorError l = noSubmit ? EVRCompositorError.None : Submit(EVREye.Eye_Left, Eyes[0]);
                EVRCompositorError r = noSubmit ? EVRCompositorError.None : Submit(EVREye.Eye_Right, Eyes[1]);
                frames++;
                if (fpsWatch == null) fpsWatch = System.Diagnostics.Stopwatch.StartNew();
                if (fpsWatch.Elapsed.TotalSeconds >= 2)
                {
                    var ft = new Compositor_FrameTiming { m_nSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Compositor_FrameTiming)) };
                    OpenVR.Compositor.GetFrameTiming(ref ft, 0);
                    Log.Info(string.Format("fps {0:0.0} (L={1} R={2}) draw {3:0.0} ms wait {4:0.0} ms | presents={5} mispresented={6} dropped={7} hmd={8}",
                        (frames - lastFrames) / fpsWatch.Elapsed.TotalSeconds, l, r, drawMs, waitMs, ft.m_nNumFramePresents, ft.m_nNumMisPresented, ft.m_nNumDroppedFrames, OpenVR.System.GetTrackedDeviceActivityLevel(0)));
                    lastFrames = frames; fpsWatch.Reset(); fpsWatch.Start();
                }
            }
            catch (Exception e) { Log.Info("submit failed: " + e); Failed = true; Ready = false; }
            // SteamVR's OpenGL submit changes GL bindings behind FNA's back (FNA caches GL state), so the window got
            // nothing. Put back exactly what was bound before the submit.
            GlState.Restore();
            if (mirror == null) mirror = new SpriteBatch(gd);
            // Desktop window: the left eye plus the menu / text layer on top, so everything is visible on the monitor.
            var full = new Rectangle(0, 0, gd.PresentationParameters.BackBufferWidth, gd.PresentationParameters.BackBufferHeight);
            if (Environment.GetEnvironmentVariable("FEZVR_MIRRORTEST") == "1") gd.Clear(Color.Magenta);
            if (!mirrorLogged) { mirrorLogged = true; var rts = gd.GetRenderTargets(); Log.Info("mirror: target count " + rts.Length + " viewport " + gd.Viewport.Bounds + " backbuffer " + full + " scissor " + gd.ScissorRectangle); }
            mirror.Begin(SpriteSortMode.Immediate, BlendState.Opaque);
            mirror.Draw(Eyes[0], full, Color.White);
            mirror.End();
            if (FlatRT != null)
            {
                mirror.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend);
                mirror.Draw(FlatRT, full, Color.White);
                mirror.End();
            }
        }

        // ---- Quads in tracking space (metres, seated origin) -------------------------------------------------------
        public static float SkyDistance = 6f, SkyWidth = 16f, PanelDistance = 1.2f, PanelWidth = 1.6f, PanelDrop = LoadDrop();
        const string DropFile = "fezvr_panel.txt";
        static float LoadDrop() { try { return float.Parse(System.IO.File.ReadAllText(DropFile).Trim(), System.Globalization.CultureInfo.InvariantCulture); } catch { return 0.05f; } }
        static bool pgUpWas, pgDnWas;
        static void PanelKeys()
        {
            bool up = (GetAsyncKeyState(0x21) & 0x8000) != 0, dn = (GetAsyncKeyState(0x22) & 0x8000) != 0;   // Page Up / Page Down
            float d = 0f;
            if (up && !pgUpWas) d = -0.05f;
            if (dn && !pgDnWas) d = 0.05f;
            pgUpWas = up; pgDnWas = dn;
            if (d == 0f) return;
            PanelDrop = Math.Max(-0.5f, Math.Min(1.2f, PanelDrop + d));
            try { System.IO.File.WriteAllText(DropFile, PanelDrop.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
            Log.Info("panel drop " + PanelDrop.ToString("0.00") + " m");
        }
        static BasicEffect quadFx;
        static readonly VertexPositionTexture[] quad = new VertexPositionTexture[4];

        static void DrawQuad(GraphicsDevice gd, int eye, Texture2D tex, Vector3 centre, float width, BlendState blend)
        {
            if (quadFx == null) { quadFx = new BasicEffect(gd); quadFx.TextureEnabled = true; quadFx.LightingEnabled = false; quadFx.VertexColorEnabled = false; }
            float hw = width / 2f, hh = hw * tex.Height / Math.Max(1, tex.Width);
            quad[0] = new VertexPositionTexture(centre + new Vector3(-hw, hh, 0f), new Vector2(0, 0));
            quad[1] = new VertexPositionTexture(centre + new Vector3(hw, hh, 0f), new Vector2(1, 0));
            quad[2] = new VertexPositionTexture(centre + new Vector3(-hw, -hh, 0f), new Vector2(0, 1));
            quad[3] = new VertexPositionTexture(centre + new Vector3(hw, -hh, 0f), new Vector2(1, 1));
            quadFx.World = Matrix.Identity;
            quadFx.View = Matrix.Invert(eyeToHead[eye] * headPose);
            quadFx.Projection = eyeProj[eye];
            quadFx.Texture = tex;
            gd.BlendState = blend;
            gd.DepthStencilState = DepthStencilState.None;
            gd.RasterizerState = RasterizerState.CullNone;
            gd.SamplerStates[0] = SamplerState.LinearClamp;
            foreach (EffectPass p in quadFx.CurrentTechnique.Passes) { p.Apply(); gd.DrawUserPrimitives(PrimitiveType.TriangleStrip, quad, 0, 2); }
        }

        // Called right after the game clears the eye target: the sky backdrop goes in first, the world on top.
        public static void DrawSkyBackdrop(GraphicsDevice gd)
        {
            if (Pass != PassKind.Eye || SkyDrawnThisPass || SkyRT == null) return;
            SkyDrawnThisPass = true;
            DrawQuad(gd, EyeIndex, SkyRT, new Vector3(0f, -DioramaDrop, -SkyDistance), SkyWidth, BlendState.Opaque);
        }

        static void DrawPanel(GraphicsDevice gd, int eye)
        {
            if (FlatRT == null) return;
            Redirect = false; gd.SetRenderTarget(Eyes[eye]);
            DrawQuad(gd, eye, FlatRT, new Vector3(0f, -PanelDrop, -PanelDistance), PanelWidth, BlendState.AlphaBlend);
            DrawAlignMarker(gd, eye);
            Redirect = true;
        }
        [System.Runtime.InteropServices.DllImport("opengl32.dll")] static extern void glFlush();
        [System.Runtime.InteropServices.DllImport("opengl32.dll")] static extern void glFinish();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        static bool f8Was, f7Was, dumpNext, autoCentered, mirrorLogged;
        static double wornSince = -1;
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static double Time() { return clock.Elapsed.TotalSeconds; }

        static void SavePng(RenderTarget2D rt, string path)
        {
            int w = rt.Width, h = rt.Height;
            var px = new Color[w * h];
            rt.GetData(px);
            using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var bytes = new byte[w * h * 4];
                for (int i = 0; i < px.Length; i++) { bytes[i * 4] = px[i].B; bytes[i * 4 + 1] = px[i].G; bytes[i * 4 + 2] = px[i].R; bytes[i * 4 + 3] = px[i].A; }
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
                bmp.UnlockBits(data);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        static void DumpImages()
        {
            string dir = System.IO.Path.Combine(Launcher.Dir, "fezvr_dump");
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("HHmmss");
            var all = new[] { Eyes[0], Eyes[1], SkyRT, FlatRT };
            string[] names = { "left", "right", "sky", "flat" };
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                SavePng(all[i], System.IO.Path.Combine(dir, stamp + "_" + names[i] + ".png"));
            }
            Log.Info("dumped eye/sky/flat images to " + dir + " (" + stamp + ")");
            for (int e = 0; e < 2; e++)
            {
                Matrix ew = eyeToHead[e] * headPose;
                Log.Info(string.Format("dump eye {0}: eyeWorld rows R=({1:0.0000},{2:0.0000},{3:0.0000}|{4:0.0000},{5:0.0000},{6:0.0000}|{7:0.0000},{8:0.0000},{9:0.0000}) T=({10:0.0000},{11:0.0000},{12:0.0000}) rt={13}x{14}",
                    e, ew.M11, ew.M12, ew.M13, ew.M21, ew.M22, ew.M23, ew.M31, ew.M32, ew.M33, ew.M41, ew.M42, ew.M43, Eyes[e].Width, Eyes[e].Height));
            }
        }

        static EVRCompositorError Submit(EVREye eye, RenderTarget2D rt)
        {
            object glTex = TexField.GetValue(rt);
            if (handleProp == null) handleProp = glTex.GetType().GetProperty("Handle");
            uint handle = (uint)handleProp.GetValue(glTex, null);
            var tex = new Texture_t { handle = (IntPtr)handle, eType = ETextureType.OpenGL, eColorSpace = EColorSpace.Auto };
            var bounds = new VRTextureBounds_t { uMin = 0, uMax = 1, vMin = 1, vMax = 0 };   // GL textures are bottom-up
            return OpenVR.Compositor.Submit(eye, ref tex, ref bounds, EVRSubmitFlags.Submit_Default);
        }
    }

    // FNA sleeps 20 ms per frame while its window is unfocused (always, in VR), syncs to the monitor and runs a fixed
    // 60 Hz step. SteamVR's WaitGetPoses paces the frames instead.
    [HarmonyPatch(typeof(Game), "Update")]
    static class Pacing
    {
        static bool done;
        static void Prefix(Game __instance)
        {
            if (done) return;
            done = true;
            if (Environment.GetEnvironmentVariable("FEZVR_NOPACING") == "1") { Log.Info("pacing: disabled"); return; }
            try
            {
                __instance.InactiveSleepTime = TimeSpan.Zero;
                string mode = Environment.GetEnvironmentVariable("FEZVR_PACING") ?? "";
                if (mode.Contains("var")) __instance.IsFixedTimeStep = false;
                var gdm = __instance.Services.GetService(typeof(IGraphicsDeviceManager)) as GraphicsDeviceManager;
                if (gdm != null)
                {
                    // Exclusive fullscreen gets throttled by Windows/the driver while unfocused (always, in VR).
                    // (Switching fullscreen -> windowed at runtime left the desktop window black; only on request.)
                    if (mode.Contains("windowed")) { gdm.IsFullScreen = false; gdm.PreferredBackBufferWidth = 1920; gdm.PreferredBackBufferHeight = 1080; }
                    if (mode.Contains("novsync")) gdm.SynchronizeWithVerticalRetrace = false;
                    if (mode.Contains("windowed") || mode.Contains("novsync")) gdm.ApplyChanges();
                }
                Log.Info("pacing: no inactive sleep; mode='" + mode + "' fixedStep=" + __instance.IsFixedTimeStep + " vsync=" + (gdm != null && gdm.SynchronizeWithVerticalRetrace));
            }
            catch (Exception e) { Log.Info("pacing failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(Game), "BeginDraw")]
    static class HookBeginDraw
    {
        static void Postfix(Game __instance, bool __result) { if (__result) VR.BeginFrame(__instance); }
    }

    [HarmonyPatch(typeof(Game), "EndDraw")]
    static class HookEndDraw
    {
        static void Prefix(Game __instance) { VR.EndFrame(__instance); }
    }

    // Effects like GammaCorrection / BurnIn "hook" the whole frame into their own target at the start of Fez.Draw
    // (TargetRenderingManager.OnRtPrepare) and hand it back when they draw. The sky and flat passes skip those
    // components, so a hook would swallow the pass and leak into the next one: hooks only apply in eye passes.
    [HarmonyPatch(typeof(FezEngine.Services.TargetRenderingManager), "OnRtPrepare")]
    static class NoHooksInFlatPasses
    {
        static bool Prefix() { return !(VR.Redirect && (VR.Pass == VR.PassKind.Sky || VR.Pass == VR.PassKind.Flat)); }
    }

    // The level only keeps what the game camera's frustum covers (+1 trile). The diorama shows past the screen
    // edges, so ground below / ahead popped in late. Use a frustum CullScale times wider and taller for culling.
    [HarmonyPatch(typeof(DefaultCameraManager), "RebuildFrustum")]
    static class WiderCulling
    {
        static readonly FieldInfo View = AccessTools.Field(typeof(CameraManager), "view");
        static readonly FieldInfo Proj = AccessTools.Field(typeof(CameraManager), "projection");
        static bool Prefix(DefaultCameraManager __instance)
        {
            if (VR.CullScale <= 1f || __instance.Frustum == null) return true;
            Matrix v = (Matrix)View.GetValue(__instance), p = (Matrix)Proj.GetValue(__instance);
            if (p.M34 != 0f) return true;   // perspective views: leave as is
            __instance.Frustum.Matrix = v * p * Matrix.CreateScale(1f / VR.CullScale, 1f / VR.CullScale, 1f);
            return false;
        }
    }

    // The opening zoom scales a flat image of the room around the screen centre (UV matrix, Scale 200 -> 1). The centre
    // differs per eye, so two copies converged. In VR draw the room unscaled and shrink the diorama instead.
    [HarmonyPatch]
    static class IntroZoomIn3D
    {
        static readonly Type T = typeof(FezGame.Fez).Assembly.GetType("FezGame.Components.IntroZoomIn");
        static readonly FieldInfo ScaleField = T != null ? AccessTools.Field(T, "Scale") : null;
        static MethodBase TargetMethod() { return AccessTools.Method(T, "Draw"); }
        static void Prefix(object __instance, out float __state)
        {
            __state = (float)ScaleField.GetValue(__instance);
            VR.ZoomFactor = 1f / Math.Max(__state, 1f);
            VR.zoomStamp = VR.Now();
            ScaleField.SetValue(__instance, 1f);
        }
        static void Postfix(object __instance, float __state) { ScaleField.SetValue(__instance, __state); }
    }

    // FEZ is 2D: Gomez stands on whatever looks like ground from this side, but keeps his own depth, so in the diorama he
    // floats in front of / behind the walkway. Draw him (not move him) at the depth of the block he stands on.
    [HarmonyPatch]
    static class GomezOnGround
    {
        static readonly Type T = typeof(FezGame.Fez).Assembly.GetType("FezGame.Components.GomezHost");
        static readonly PropertyInfo MeshProp = AccessTools.Property(T, "PlayerMesh");
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods() { yield return AccessTools.Method(T, "DoDraw_Internal"); yield return AccessTools.Method(T, "PreDraw"); }
        static FezGame.Services.IPlayerManager pm;
        static Vector3 lastAxis;
        static float drawnDepth;
        static double lastTime = -1;
        static void Prefix(object __instance, out Vector3? __state)
        {
            __state = null;
            if (!VR.EyeCameraActive) return;
            var mesh = MeshProp.GetValue(__instance, null) as FezEngine.Structure.Mesh;
            if (mesh == null) return;
            if (pm == null) pm = ServiceHelper.Get<FezGame.Services.IPlayerManager>();
            if (pm == null) return;
            Matrix v = VR.GameView;
            var axis = new Vector3(v.M13, v.M23, v.M33);
            if (axis.LengthSquared() < 1e-6f) return;
            axis.Normalize();
            Vector3 pos = mesh.Position;
            float actual = Vector3.Dot(pos, axis);
            double now = VR.Now();
            if (Vector3.Dot(axis, lastAxis) < 0.99f || lastTime < 0 || now - lastTime > 0.5) drawnDepth = actual;   // view turned / new level
            lastAxis = axis;
            var ground = pm.Ground.First;
            if (ground != null && now != lastTime)
            {
                float target = Vector3.Dot(ground.Center, axis);
                float k = (float)(1 - Math.Exp(-(now - Math.Max(lastTime, now - 0.1)) / 0.08));
                drawnDepth += (target - drawnDepth) * k;
            }
            lastTime = now;
            __state = pos;
            mesh.Position = pos + axis * (drawnDepth - actual);
        }
        static void Postfix(object __instance, Vector3? __state)
        {
            if (!__state.HasValue) return;
            var mesh = MeshProp.GetValue(__instance, null) as FezEngine.Structure.Mesh;
            if (mesh != null) mesh.Position = __state.Value;
        }
    }

    // Same for NPCs (Gomez's uncle walked in mid air): for the eye passes only, move each NPC's quad to the depth of the
    // visible block under its feet; put it back after the frame (game logic never sees it).
    static class NpcGround
    {
        static readonly FieldInfo StatesField = AccessTools.Field(typeof(FezEngine.Components.NpcHost), "NpcStates");
        static readonly FieldInfo GroupField = AccessTools.Field(typeof(FezEngine.Components.NpcState), "Group");
        static readonly List<KeyValuePair<FezEngine.Structure.Group, Vector3>> moved = new List<KeyValuePair<FezEngine.Structure.Group, Vector3>>();
        static FezEngine.Services.ILevelManager lm;
        public static void Apply(Game g, CameraManager cam, Matrix view)
        {
            moved.Clear();
            try
            {
                var dcm = cam as FezEngine.Services.IDefaultCameraManager;
                if (dcm == null || dcm.Viewpoint == FezEngine.Viewpoint.Perspective || dcm.Viewpoint == FezEngine.Viewpoint.None) return;
                if (lm == null) lm = ServiceHelper.Get<FezEngine.Services.ILevelManager>();
                if (lm == null) return;
                var axis = new Vector3(view.M13, view.M23, view.M33);
                if (axis.LengthSquared() < 1e-6f) return;
                axis.Normalize();
                for (int i = 0; i < g.Components.Count; i++)
                {
                    var host = g.Components[i] as FezEngine.Components.NpcHost;
                    if (host == null) continue;
                    var states = StatesField.GetValue(host) as List<FezEngine.Components.NpcState>;
                    if (states == null) continue;
                    foreach (var s in states)
                    {
                        var grp = GroupField.GetValue(s) as FezEngine.Structure.Group;
                        if (grp == null) continue;
                        Vector3 p = grp.Position;
                        var hit = lm.NearestTrile(p - 0.5f * Vector3.UnitY, FezEngine.Services.QueryOptions.None, dcm.Viewpoint);
                        var t = hit.Surface ?? hit.Deep;
                        if (t == null) continue;
                        float d = Vector3.Dot(t.Center - p, axis);
                        if (Math.Abs(d) < 0.01f) continue;
                        moved.Add(new KeyValuePair<FezEngine.Structure.Group, Vector3>(grp, p));
                        grp.Position = p + axis * d;
                    }
                }
            }
            catch (Exception e) { Restore(); if (!warned) { warned = true; Log.Info("NpcGround: " + e.Message); } }
        }
        static bool warned;
        public static void Restore()
        {
            foreach (var kv in moved) kv.Key.Position = kv.Value;
            moved.Clear();
        }
    }

    // Water is a 150-block box drawn "always on top" (in flat FEZ only its front face shows). In the diorama its top face
    // reached far toward the viewer and was painted over the islands. Eye passes: trim the box to the level's depth and
    // draw it with the depth test, as the game itself does for its first-person view.
    [HarmonyPatch]
    static class WaterInDiorama
    {
        static readonly Type T = typeof(FezGame.Fez).Assembly.GetType("FezGame.Components.LiquidHost");
        static readonly FieldInfo LiquidF = AccessTools.Field(T, "LiquidMesh"), RaysF = AccessTools.Field(T, "RaysMesh"), FoamF = AccessTools.Field(T, "FoamMesh");
        public static FezEngine.Structure.Mesh Liquid, Rays, Foam;
        public static Vector3 DepthAxis; public static float FrontDepth;   // foam strip pinned to the water's front face
        static FezEngine.Services.ILevelManager lm;
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods() { yield return AccessTools.Method(T, "DoDraw"); yield return AccessTools.Method(T, "DrawLights"); }
        static void Prefix(object __instance, out Vector3[] __state)
        {
            __state = null;
            if (!VR.EyeCameraActive) return;
            var liq = LiquidF.GetValue(__instance) as FezEngine.Structure.Mesh;
            if (liq == null) return;
            if (lm == null) lm = ServiceHelper.Get<FezEngine.Services.ILevelManager>();
            Matrix v = VR.GameView;
            var a = new Vector3(Math.Abs(v.M13), Math.Abs(v.M23), Math.Abs(v.M33));
            a = new Vector3(a.X > 0.7f ? 1f : 0f, 0f, a.Z > 0.7f ? 1f : 0f);   // depth axis: world X or Z
            if (a == Vector3.Zero || lm == null) return;
            __state = new[] { liq.Position, liq.Scale };
            float depth = Vector3.Dot(lm.Size, a) + 2f;
            liq.Scale = Vector3.One + a * (depth / 150f - 1f);
            liq.Position = liq.Position - a * Vector3.Dot(liq.Position, a) + a * (Vector3.Dot(lm.Size, a) / 2f);
            Liquid = liq;
            Rays = RaysF.GetValue(__instance) as FezEngine.Structure.Mesh;
            Foam = FoamF.GetValue(__instance) as FezEngine.Structure.Mesh;
            DepthAxis = a;
            float toViewer = Vector3.Dot(new Vector3(v.M13, v.M23, v.M33), a);   // view +z (toward the viewer) along the depth axis
            FrontDepth = toViewer > 0f ? Vector3.Dot(lm.Size, a) + 1f : -1f;
        }
        static void Postfix(object __instance, Vector3[] __state)
        {
            if (__state == null) return;
            var liq = LiquidF.GetValue(__instance) as FezEngine.Structure.Mesh;
            if (liq != null) { liq.Position = __state[0]; liq.Scale = __state[1]; }
            Liquid = null; Rays = null; Foam = null;
        }
    }

    [HarmonyPatch(typeof(FezEngine.Structure.Mesh), "Draw", new Type[0])]
    static class WaterDepthTest
    {
        static void Prefix(FezEngine.Structure.Mesh __instance)
        {
            if (WaterInDiorama.Liquid == null) return;
            if (ReferenceEquals(__instance, WaterInDiorama.Liquid) || ReferenceEquals(__instance, WaterInDiorama.Rays)) __instance.AlwaysOnTop = false;
        }
    }

    [HarmonyPatch(typeof(FezEngine.Structure.Mesh), "Draw", new Type[0])]
    static class FoamAtFront
    {
        static void Prefix(FezEngine.Structure.Mesh __instance, out Vector3? __state)
        {
            __state = null;
            if (WaterInDiorama.Foam == null || !ReferenceEquals(__instance, WaterInDiorama.Foam)) return;
            Vector3 p = __instance.Position, a = WaterInDiorama.DepthAxis;
            __state = p;
            __instance.Position = p - a * Vector3.Dot(p, a) + a * WaterInDiorama.FrontDepth;
        }
        static void Postfix(FezEngine.Structure.Mesh __instance, Vector3? __state)
        {
            if (__state.HasValue) __instance.Position = __state.Value;
        }
    }

    // Transitions (faraway places, water during them) draw world content with their own orthographic camera
    // (ForcedProjectionMatrix / ForcedViewMatrix), which ignores the eye cameras -> double vision. In eye passes, drop a
    // forced world-sized orthographic projection (and a forced view) so the content uses the eye camera. Screen-space
    // overlays (identity / small ortho / perspective) are left alone.
    [HarmonyPatch]
    static class NoForcedOrthoInEyes
    {
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(FezEngine.Effects.BaseEffect), "Prepare", new[] { typeof(FezEngine.Structure.Mesh) });
            yield return AccessTools.Method(typeof(FezEngine.Effects.BaseEffect), "Prepare", new[] { typeof(FezEngine.Structure.Group) });
        }
        static bool logged;
        static void Prefix(FezEngine.Effects.BaseEffect __instance, out Matrix?[] __state)
        {
            __state = null;
            if (!VR.EyeCameraActive) return;
            Matrix? p = __instance.ForcedProjectionMatrix, v = __instance.ForcedViewMatrix;
            if (!p.HasValue && !v.HasValue) return;
            bool worldOrtho = p.HasValue && p.Value.M34 == 0f && p.Value.M11 != 0f && Math.Abs(p.Value.M11) < 0.5f;
            if (p.HasValue && !worldOrtho) return;
            __state = new[] { p, v };
            __instance.ForcedProjectionMatrix = null;
            __instance.ForcedViewMatrix = null;
            if (!logged) { logged = true; Log.Info("eye pass: dropped forced camera on " + __instance.GetType().Name); }
        }
        static void Postfix(FezEngine.Effects.BaseEffect __instance, Matrix?[] __state)
        {
            if (__state == null) return;
            __instance.ForcedProjectionMatrix = __state[0];
            __instance.ForcedViewMatrix = __state[1];
        }
    }

    // Fez.Draw runs camera work (TimeInterpolation callbacks: the camera following Gomez, zoom interpolation; scheduled
    // draw actions). Let it run on the game's own matrices, then adopt the result and put the eye view back.
    [HarmonyPatch(typeof(FezEngine.Tools.TimeInterpolation), "ProcessCallbacks")]
    static class CameraWorkOnGameMatrices
    {
        static void Prefix() { VR.BeginGameCameraWork(); }
        static void Postfix() { VR.EndGameCameraWork(); }
    }

    [HarmonyPatch(typeof(FezEngine.Tools.DrawActionScheduler), "Process")]
    static class DrawActionsOnGameMatrices
    {
        static void Prefix() { VR.BeginGameCameraWork(); }
        static void Postfix() { VR.EndGameCameraWork(); }
    }

    [HarmonyPatch(typeof(Game), "Draw")]
    static class FilterComponentDraws
    {
        static System.Collections.Generic.IEnumerable<CodeInstruction> Transpiler(System.Collections.Generic.IEnumerable<CodeInstruction> code)
        {
            MethodInfo draw = AccessTools.Method(typeof(IDrawable), "Draw");
            MethodInfo filtered = AccessTools.Method(typeof(VR), "DrawFiltered");
            int n = 0;
            foreach (CodeInstruction ins in code)
            {
                if (ins.Calls(draw)) { n++; yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, filtered); }
                else yield return ins;
            }
            Log.Info("Game.Draw: filtered " + n + " component draw call(s)");
        }
    }

    [HarmonyPatch(typeof(GraphicsDevice), "Clear", new[] { typeof(ClearOptions), typeof(Color), typeof(float), typeof(int) })]
    static class PassClear
    {
        static void Prefix(GraphicsDevice __instance, ref Color color)
        {
            if (VR.Redirect && VR.Pass == VR.PassKind.Flat && IsPassTarget(__instance)) color = Color.Transparent;
        }
        static void Postfix(GraphicsDevice __instance)
        {
            if (VR.Redirect && VR.Pass == VR.PassKind.Eye && IsPassTarget(__instance)) VR.DrawSkyBackdrop(__instance);
        }
        static bool IsPassTarget(GraphicsDevice gd)
        {
            RenderTargetBinding[] b = gd.GetRenderTargets();
            return b.Length == 1 && b[0].RenderTarget == VR.Target;
        }
    }

    // While an eye is being drawn, "draw to the back buffer" means "draw to that eye's target".
    [HarmonyPatch(typeof(GraphicsDevice), "SetRenderTargets", new[] { typeof(RenderTargetBinding[]) })]
    static class RedirectBackbuffer
    {
        static void Prefix(ref RenderTargetBinding[] renderTargets)
        {
            if (VR.Redirect && (renderTargets == null || renderTargets.Length == 0))
            {
                RenderTarget2D t = VR.Target;
                if (t != null) renderTargets = new[] { new RenderTargetBinding(t) };
            }
        }
    }
}
