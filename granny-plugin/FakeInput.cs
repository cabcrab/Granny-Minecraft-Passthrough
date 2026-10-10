using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GrannyCraft
{
    /// <summary>
    /// What Granny's own scripts see through Input.GetKey*:
    /// - keys "pressed" by the mod (Q pressing Granny's drop, dev test commands);
    /// - keys taken away from Granny while the passthrough is on (her drop key is Space, Minecraft's jump: a real Space
    ///   press is only the jump). The mod's own reads (inside <see cref="Own"/>) see the real keyboard.
    /// </summary>
    [HarmonyPatch]
    internal static class FakeInput
    {
        private static readonly Dictionary<KeyCode, float> HeldUntil = new Dictionary<KeyCode, float>();
        private static readonly HashSet<KeyCode> DownThisFrame = new HashSet<KeyCode>();
        private static readonly HashSet<KeyCode> NextDown = new HashSet<KeyCode>();
        private static readonly HashSet<KeyCode> UpThisFrame = new HashSet<KeyCode>();
        private static readonly List<KeyCode> Released = new List<KeyCode>();
        /// <summary>Presses meant for Granny only (the mod's own reads don't see them: Q's drop must not jump).</summary>
        private static readonly HashSet<KeyCode> GrannyOnly = new HashSet<KeyCode>();
        /// <summary>Real presses of these keys don't reach Granny.</summary>
        public static readonly HashSet<KeyCode> TakenFromGranny = new HashSet<KeyCode>();
        private static int _frame = -1;
        private static int _own;

        /// <summary>The mod reading the real keyboard (jump, sprint...).</summary>
        public static bool Key(KeyCode k)
        {
            _own++;
            try { return Input.GetKey(k); } finally { _own--; }
        }

        public static bool KeyDown(KeyCode k)
        {
            _own++;
            try { return Input.GetKeyDown(k); } finally { _own--; }
        }

        public static void Hold(KeyCode k, float seconds, bool grannyOnly = false)
        {
            HeldUntil[k] = Time.unscaledTime + seconds + 0.05f;
            NextDown.Add(k); // seen as "pressed" for the whole next frame, whatever the script order
            if (grannyOnly) GrannyOnly.Add(k); else GrannyOnly.Remove(k);
        }

        /// <summary>Once per frame (Core.Update runs early): turn last frame's edges off, release expired keys.</summary>
        public static void Tick()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            DownThisFrame.Clear();
            foreach (var k in NextDown) DownThisFrame.Add(k);
            NextDown.Clear();
            UpThisFrame.Clear();
            Released.Clear();
            foreach (var kv in HeldUntil) if (Time.unscaledTime > kv.Value) Released.Add(kv.Key);
            foreach (var k in Released) { HeldUntil.Remove(k); UpThisFrame.Add(k); GrannyOnly.Remove(k); }
        }

        private static bool Fake(KeyCode key, HashSet<KeyCode> set) => set.Contains(key) && (_own == 0 || !GrannyOnly.Contains(key));

        private static void Filter(KeyCode key, ref bool result, bool fake)
        {
            // a real press of a key taken from Granny: not for her (the mod's own reads still get it)
            if (result && _own == 0 && Core.Enabled && (TakenFromGranny.Contains(key) || Menu.HidesFromGranny(key))) result = false;
            if (!result && fake) result = true;
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), typeof(KeyCode))]
        [HarmonyPostfix]
        private static void Down(KeyCode key, ref bool __result) => Filter(key, ref __result, Fake(key, DownThisFrame));

        [HarmonyPatch(typeof(Input), nameof(Input.GetKey), typeof(KeyCode))]
        [HarmonyPostfix]
        private static void Held(KeyCode key, ref bool __result) => Filter(key, ref __result, HeldUntil.ContainsKey(key) && (_own == 0 || !GrannyOnly.Contains(key)));

        [HarmonyPatch(typeof(Input), nameof(Input.GetKeyUp), typeof(KeyCode))]
        [HarmonyPostfix]
        private static void Up(KeyCode key, ref bool __result) => Filter(key, ref __result, Fake(key, UpThisFrame));

        private static float Axis(string name)
        {
            if (name == "Vertical") return (HeldUntil.ContainsKey(KeyCode.W) ? 1f : 0f) - (HeldUntil.ContainsKey(KeyCode.S) ? 1f : 0f);
            if (name == "Horizontal") return (HeldUntil.ContainsKey(KeyCode.D) ? 1f : 0f) - (HeldUntil.ContainsKey(KeyCode.A) ? 1f : 0f);
            return 0f;
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetAxis), typeof(string))]
        [HarmonyPostfix]
        private static void GetAxis(string axisName, ref float __result)
        {
            // the creative inventory has the mouse: Granny's camera doesn't turn
            if (Menu.Open && axisName.StartsWith("Mouse")) { __result = 0f; return; }
            // Minecraft movement moves the player itself: Granny's own walking reads no input then
            if (Movement.Active && (axisName == "Vertical" || axisName == "Horizontal")) { __result = 0f; return; }
            float a = Axis(axisName); if (a != 0f) __result = a;
        }

        [HarmonyPatch(typeof(Input), nameof(Input.GetAxisRaw), typeof(string))]
        [HarmonyPostfix]
        private static void GetAxisRaw(string axisName, ref float __result)
        {
            if (Menu.Open && axisName.StartsWith("Mouse")) { __result = 0f; return; }
            if (Movement.Active && (axisName == "Vertical" || axisName == "Horizontal")) { __result = 0f; return; }
            float a = Axis(axisName); if (a != 0f) __result = a;
        }
    }
}
