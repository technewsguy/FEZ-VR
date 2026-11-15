// VR controllers as an Xbox gamepad. FEZ reads player one's pad through FNA's GamePad.GetState; a postfix merges the
// SteamVR controllers (input\actions.json, rebindable in SteamVR's controller settings) into that state, so menus, the
// game's prompts and all its controls work as with a real pad (a real pad keeps working too).
//
//   Left stick       -> left stick (walk, climb, menus)      Right stick       -> right stick (look, first person)
//   Right A          -> A (jump, accept)                     Right B           -> B (cancel, back)
//   Left A           -> X (grab, throw, talk, open)          Left B            -> Y (inventory)
//   Left trigger     -> LT (rotate the world left)           Right trigger     -> RT (rotate the world right)
//   Right stick up / down while the map is open -> RB / LB (map zoom in / out)
//   Left trackpad    -> Back (map)                           Right trackpad    -> Start (pause)
//   Stick clicks     -> stick clicks (first-person view / clamp look)
//   (Recentering: the Index controllers' own system-button hold, or F8.)
using System;
using System.IO;
using System.Runtime.InteropServices;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Valve.VR;

namespace FezVr
{
    static class VrInput
    {
        static bool ok;
        static ulong set, stickL, stickR, clickL, clickR, trigL, trigR, aL, aR, bL, bR, padL, padR;
        static VRActiveActionSet_t[] active;
        static readonly uint DigitalSize = (uint)Marshal.SizeOf(typeof(InputDigitalActionData_t));
        static readonly uint AnalogSize = (uint)Marshal.SizeOf(typeof(InputAnalogActionData_t));
        static readonly uint ActiveSetSize = (uint)Marshal.SizeOf(typeof(VRActiveActionSet_t));

        public static bool Active;            // a controller is on and bound
        public static GamePadState State;     // last read state
        static FezGame.Services.IGameStateManager gs;
        static bool InMap()
        {
            try { if (gs == null) gs = FezEngine.Tools.ServiceHelper.Get<FezGame.Services.IGameStateManager>(); return gs != null && gs.InMap; }
            catch { return false; }
        }

        public static void Init(string dir)
        {
            try
            {
                string manifest = Path.Combine(dir, Path.Combine("fezvr_input", "actions.json"));
                if (OpenVR.Input == null || !File.Exists(manifest)) { Log.Info("VR controllers unavailable (no " + manifest + ")"); return; }
                var e = OpenVR.Input.SetActionManifestPath(manifest);
                if (e != EVRInputError.None) { Log.Info("SteamVR Input: SetActionManifestPath failed: " + e); return; }
                var inp = OpenVR.Input;
                inp.GetActionSetHandle("/actions/fezvr", ref set);
                inp.GetActionHandle("/actions/fezvr/in/stick_l", ref stickL);
                inp.GetActionHandle("/actions/fezvr/in/stick_r", ref stickR);
                inp.GetActionHandle("/actions/fezvr/in/stickclick_l", ref clickL);
                inp.GetActionHandle("/actions/fezvr/in/stickclick_r", ref clickR);
                inp.GetActionHandle("/actions/fezvr/in/trigger_l", ref trigL);
                inp.GetActionHandle("/actions/fezvr/in/trigger_r", ref trigR);
                inp.GetActionHandle("/actions/fezvr/in/a_l", ref aL);
                inp.GetActionHandle("/actions/fezvr/in/a_r", ref aR);
                inp.GetActionHandle("/actions/fezvr/in/b_l", ref bL);
                inp.GetActionHandle("/actions/fezvr/in/b_r", ref bR);
                inp.GetActionHandle("/actions/fezvr/in/pad_l", ref padL);
                inp.GetActionHandle("/actions/fezvr/in/pad_r", ref padR);
                active = new[] { new VRActiveActionSet_t { ulActionSet = set } };
                ok = true;
                Log.Info("VR controllers: SteamVR Input ready (" + manifest + ")");
            }
            catch (Exception ex) { Log.Info("VR controllers unavailable: " + ex.Message); }
        }

        static bool Digital(ulong h, ref bool any)
        {
            var d = new InputDigitalActionData_t();
            if (OpenVR.Input.GetDigitalActionData(h, ref d, DigitalSize, 0) != EVRInputError.None || !d.bActive) return false;
            any = true;
            return d.bState;
        }

        static Vector2 Analog(ulong h, ref bool any)
        {
            var d = new InputAnalogActionData_t();
            if (OpenVR.Input.GetAnalogActionData(h, ref d, AnalogSize, 0) != EVRInputError.None || !d.bActive) return Vector2.Zero;
            any = true;
            return new Vector2(d.x, d.y);
        }

        // Once per frame (main thread, after WaitGetPoses).
        public static void Update(double now)
        {
            if (!ok) return;
            try
            {
                OpenVR.Input.UpdateActionState(active, ActiveSetSize);
                bool any = false;
                Vector2 sl = Analog(stickL, ref any), sr = Analog(stickR, ref any);
                float tl = Analog(trigL, ref any).X, tr = Analog(trigR, ref any).X;
                Buttons b = 0;
                if (Digital(aR, ref any)) b |= Buttons.A;
                if (Digital(bR, ref any)) b |= Buttons.B;
                if (Digital(aL, ref any)) b |= Buttons.X;
                if (Digital(bL, ref any)) b |= Buttons.Y;
                if (Digital(padL, ref any)) b |= Buttons.Back;
                if (Digital(padR, ref any)) b |= Buttons.Start;
                if (Digital(clickL, ref any)) b |= Buttons.LeftStick;
                if (Digital(clickR, ref any)) b |= Buttons.RightStick;
                // The map has no use for the right stick: up / down zooms it (RB / LB).
                if (InMap())
                {
                    if (sr.Y > 0.6f) b |= Buttons.RightShoulder;
                    else if (sr.Y < -0.6f) b |= Buttons.LeftShoulder;
                    sr = Vector2.Zero;
                }
                if (tl > 0.5f) b |= Buttons.LeftTrigger;
                if (tr > 0.5f) b |= Buttons.RightTrigger;
                if (!Active && any) Log.Info("VR controllers active");
                Active = any;
                State = new GamePadState(new GamePadThumbSticks(sl, sr), new GamePadTriggers(tl, tr), new GamePadButtons(b), new GamePadDPad());

           }
            catch (Exception ex) { Log.Info("VR controllers: " + ex.Message); ok = false; Active = false; }
        }

        // Real pad + VR controllers: buttons OR'ed, the larger stick / trigger wins.
        public static GamePadState Merge(GamePadState real)
        {
            if (!Active) return real;
            GamePadState v = State;
            Buttons b = 0;
            foreach (Buttons x in AllButtons) if (real.IsButtonDown(x) || v.IsButtonDown(x)) b |= x;
            Vector2 l = Bigger(real.ThumbSticks.Left, v.ThumbSticks.Left), r = Bigger(real.ThumbSticks.Right, v.ThumbSticks.Right);
            float lt = Math.Max(real.Triggers.Left, v.Triggers.Left), rt = Math.Max(real.Triggers.Right, v.Triggers.Right);
            var dpad = new GamePadDPad(real.DPad.Up, real.DPad.Down, real.DPad.Left, real.DPad.Right);
            var bl = new System.Collections.Generic.List<Buttons>();
            foreach (Buttons x in AllButtons) if ((b & x) != 0) bl.Add(x);
            if (real.DPad.Up == ButtonState.Pressed) bl.Add(Buttons.DPadUp);
            if (real.DPad.Down == ButtonState.Pressed) bl.Add(Buttons.DPadDown);
            if (real.DPad.Left == ButtonState.Pressed) bl.Add(Buttons.DPadLeft);
            if (real.DPad.Right == ButtonState.Pressed) bl.Add(Buttons.DPadRight);
            return new GamePadState(l, r, lt, rt, bl.ToArray());
        }

        static Vector2 Bigger(Vector2 a, Vector2 b) { return a.LengthSquared() >= b.LengthSquared() ? a : b; }

        static readonly Buttons[] AllButtons = { Buttons.A, Buttons.B, Buttons.X, Buttons.Y, Buttons.Back, Buttons.Start,
            Buttons.LeftStick, Buttons.RightStick, Buttons.LeftShoulder, Buttons.RightShoulder, Buttons.LeftTrigger, Buttons.RightTrigger };
    }

    [HarmonyPatch(typeof(GamePad), "GetState", new[] { typeof(PlayerIndex), typeof(GamePadDeadZone) })]
    static class VrGamepadMerge
    {
        static void Postfix(PlayerIndex playerIndex, ref GamePadState __result)
        {
            if (playerIndex == PlayerIndex.One && VrInput.Active) __result = VrInput.Merge(__result);
        }
    }

    [HarmonyPatch(typeof(GamePad), "GetState", new[] { typeof(PlayerIndex) })]
    static class VrGamepadMerge1
    {
        static void Postfix(PlayerIndex playerIndex, ref GamePadState __result)
        {
            if (playerIndex == PlayerIndex.One && VrInput.Active) __result = VrInput.Merge(__result);
        }
    }
}
