using System;
using HarmonyLib;
using UnityEngine;

namespace GrannyCraft
{
    /// <summary>
    /// Granny has no jump, and her FallingHolder doesn't see Minecraft movement properly. While Minecraft movement is on,
    /// her fall detection is kept quiet and the landing is decided here: jumps and drops shorter than FallAnimationHeight
    /// blocks (from the highest point) do nothing; a longer drop, on touching the ground, runs Granny's own landing
    /// (HandleLanding: animation, sound, and fall damage for long falls) with the real time spent in the air. Her code
    /// then runs normally for a few seconds so the animation and camera finish as in the normal game.
    /// </summary>
    [HarmonyPatch]
    internal static class FallPatches
    {
        private static float _allowUntil;
        private static FallingHolder _fh;

        private static bool Quiet => Movement.Active && Time.time >= _allowUntil;

        /// <summary>Granny's landing (animation, camera) is playing: Minecraft movement waits.</summary>
        public static bool LandingActive
        {
            get
            {
                if (Time.time >= _allowUntil || _fh == null) return false;
                if (Time.time < _landedAt + 0.4f) return true;
                bool anim = _fh.playerAnimation != null && _fh.playerAnimation.isPlaying;
                return anim || _fh.isLanding;
            }
        }
        private static float _landedAt = -10f;

        public static void Land(float airTime, float dropBlocks)
        {
            try
            {
                if (_fh == null || !_fh.isActiveAndEnabled) _fh = UnityEngine.Object.FindObjectOfType<FallingHolder>();
                if (_fh == null) { Plugin.L.LogWarning("fall: no FallingHolder"); return; }
                _allowUntil = Time.time + 3f;
                _landedAt = Time.time;
                // at least Granny's own "this was a fall" duration, more if the fall was longer (long falls hurt)
                float duration = Mathf.Max(airTime, _fh.FallDurationHold + 0.05f);
                _fh.isFalling = true;
                _fh.isLanding = false;
                _fh.fallDuration = duration;
                Plugin.L.LogInfo($"fall: {dropBlocks:F1} blocks, {airTime:F2} s in the air (Granny: hold {_fh.FallDurationHold:F2} check {_fh.FallChecker:F2} mega {_fh.FallMega:F2}) -> landing");
                _fh.HandleLanding();
            }
            catch (Exception e)
            {
                // the landing then happens in her own Update (allowed for the next 3 s), from the state set above
                Plugin.L.LogWarning("fall: HandleLanding failed, leaving it to Granny's update: " + e.Message);
            }
        }

        [HarmonyPatch(typeof(FallingHolder), nameof(FallingHolder.Update))]
        [HarmonyPrefix]
        private static bool FallingUpdate(FallingHolder __instance)
        {
            if (!Quiet) return true;
            __instance.isFalling = false;
            __instance.fallDuration = 0f;
            return false;
        }

        [HarmonyPatch(typeof(MobileFPS), nameof(MobileFPS.Update))]
        [HarmonyPostfix]
        private static void FpsUpdate(MobileFPS __instance)
        {
            if (!Quiet) return;
            __instance.isFallingOrLanding = false;
            __instance.fallingDuration = 0f;
        }
    }
}
