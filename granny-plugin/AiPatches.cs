using HarmonyLib;
using UnityEngine;

namespace GrannyCraft
{
    /// <summary>
    /// While a hit staggers Granny or Grandpa, their AI pauses (no chasing, no grabbing the player); it resumes on its
    /// own afterwards. Patches only skip the AI's own update for those 0.6 s.
    /// </summary>
    [HarmonyPatch]
    internal static class AiPatches
    {
        [HarmonyPatch(typeof(AI_Granny), nameof(AI_Granny.FixedUpdate))]
        [HarmonyPrefix]
        private static bool GrannyUpdate(AI_Granny __instance) => !Enemies.IsStaggered(__instance.gameObject);

        [HarmonyPatch(typeof(AI_Granny), nameof(AI_Granny.OnTriggerStay))]
        [HarmonyPrefix]
        private static bool GrannyTrigger(AI_Granny __instance) => !Enemies.IsStaggered(__instance.gameObject);

        [HarmonyPatch(typeof(AI_Grandpa), nameof(AI_Grandpa.FixedUpdate))]
        [HarmonyPrefix]
        private static bool GrandpaUpdate(AI_Grandpa __instance) => !Enemies.IsStaggered(__instance.gameObject);

        [HarmonyPatch(typeof(AI_Grandpa), nameof(AI_Grandpa.OnTriggerStay))]
        [HarmonyPrefix]
        private static bool GrandpaTrigger(AI_Grandpa __instance) => !Enemies.IsStaggered(__instance.gameObject);
    }
}
