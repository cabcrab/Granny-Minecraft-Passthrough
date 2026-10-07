using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;

namespace GrannyCraft
{
    /// <summary>One-time dump of what the scene looks like (cameras, collider layers, key bindings, enemies).</summary>
    internal static class Diagnostics
    {
        private static bool _done;

        public static void DumpOnce()
        {
            if (_done) return;
            _done = true;
            try
            {
                var L = Plugin.L;
                L.LogInfo($"screen {Screen.width}x{Screen.height} mode={Screen.fullScreenMode} vsync={QualitySettings.vSyncCount} targetFps={Application.targetFrameRate}");
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>())
                    L.LogInfo($"camera '{c.name}' depth={c.depth} enabled={c.enabled} mask={c.cullingMask:X} clear={c.clearFlags} path={c.actualRenderingPath} target={(c.targetTexture != null ? c.targetTexture.name : "screen")} near={c.nearClipPlane} far={c.farClipPlane} fov={c.fieldOfView} parent={(c.transform.parent != null ? c.transform.parent.name : "-")}");

                var counts = new Dictionary<int, int>();
                var bounds = new Dictionary<int, Bounds>();
                foreach (var col in UnityEngine.Object.FindObjectsOfType<Collider>())
                {
                    if (col.isTrigger) continue;
                    int l = col.gameObject.layer;
                    counts[l] = counts.TryGetValue(l, out int n) ? n + 1 : 1;
                    if (bounds.TryGetValue(l, out var b)) { b.Encapsulate(col.bounds); bounds[l] = b; } else bounds[l] = col.bounds;
                }
                foreach (var kv in counts)
                    L.LogInfo($"colliders layer {kv.Key} '{LayerMask.LayerToName(kv.Key)}': {kv.Value} bounds {bounds[kv.Key].min} .. {bounds[kv.Key].max}");

                var pr = Core.PS.PickRay;
                if (pr != null) L.LogInfo($"keys: interact={pr.MainInteract} drop={pr.DropKey} shoot={pr.ShootKey}");
                foreach (var a in UnityEngine.Object.FindObjectsOfType<NavMeshAgent>())
                    L.LogInfo($"navagent {a.name} layer={a.gameObject.layer} pos={a.transform.position} speed={a.speed} r={a.radius} h={a.height} active={a.isActiveAndEnabled}");
                var ec = UnityEngine.Object.FindObjectOfType<EnemyController>();
                if (ec != null) L.LogInfo($"EnemyController: granny={(ec.Granny != null ? ec.Granny.name : "-")} grandpa={(ec.Grandpa != null ? ec.Grandpa.name : "-")} recoverG={ec.MaxTimerRecoverGranny} recoverGp={ec.MaxTimerRecoverGrandpa}");
                var sb = new StringBuilder("layers in use:");
                for (int i = 0; i < 32; i++)
                {
                    string n = LayerMask.LayerToName(i);
                    if (!string.IsNullOrEmpty(n)) sb.Append($" {i}={n}");
                }
                L.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.L.LogError("diagnostics: " + e);
            }
        }
    }
}
