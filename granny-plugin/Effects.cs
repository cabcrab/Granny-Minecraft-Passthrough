using System.Text.Json;
using UnityEngine;

namespace GrannyCraft
{
    /// <summary>Minecraft's events acted out in Granny's world.</summary>
    internal static class Effects
    {
        /// <summary>{"t":"explosion","pos":[x,y,z],"r":radius}: loose objects fly, the camera shakes, Granny hears it.</summary>
        public static void Explosion(JsonElement m)
        {
            var pa = m.GetProperty("pos");
            Vector3 at = Coords.ToUnity(pa[0].GetDouble(), pa[1].GetDouble(), pa[2].GetDouble());
            float r = (float)m.GetProperty("r").GetDouble() * Coords.U;
            Enemies.OnExplosion(at, (float)m.GetProperty("r").GetDouble());
            int pushed = 0;
            foreach (var c in Physics.OverlapSphere(at, r * 2f, ~0, QueryTriggerInteraction.Ignore))
            {
                var rb = c.attachedRigidbody;
                if (rb == null || rb.isKinematic) continue;
                rb.AddExplosionForce(600f * r, at, r * 2f, 1.5f);
                pushed++;
            }
            if (Core.PS != null && Core.PlayerRoot != null && (Core.PlayerRoot.position - at).magnitude < r * 4f)
            {
                var shake = Core.PS.ShakeAnim;
                if (shake != null) shake.Play();
            }
            Plugin.L.LogInfo($"explosion at {at} r={r}: pushed {pushed} objects");
        }
    }
}
