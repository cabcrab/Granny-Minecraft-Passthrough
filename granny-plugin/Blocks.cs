using System;
using System.Collections.Generic;
using System.Text.Json;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace GrannyCraft
{
    /// <summary>
    /// Minecraft blocks placed in the house become solid in Granny's world: a 1 m box collider (the player and anything
    /// physical collide with it) and a carving NavMeshObstacle (Granny and Grandpa path around it).
    /// </summary>
    internal static class Blocks
    {
        /// <summary>Layer for the mirrored blocks; chosen at start among layers nothing uses.</summary>
        public static int Layer = 30;
        private static readonly Dictionary<long, GameObject> Placed = new Dictionary<long, GameObject>();
        private static GameObject _root;
        private static bool _layerChosen;

        public static void ChooseLayer()
        {
            if (_layerChosen) return;
            _layerChosen = true;
            var used = new HashSet<int>();
            foreach (var go in UnityEngine.Object.FindObjectsOfType<Transform>()) used.Add(go.gameObject.layer);
            int pl0 = Core.CC != null ? Core.CC.gameObject.layer : 0;
            Layer = 0;
            for (int l = 31; l >= 8; l--)
            {
                if (!used.Contains(l) && string.IsNullOrEmpty(LayerMask.LayerToName(l)) && !Physics.GetIgnoreLayerCollision(pl0, l))
                {
                    Layer = l;
                    break;
                }
            }
            int pl = pl0;
            if (Physics.GetIgnoreLayerCollision(pl, Layer))
            {
                // the player wouldn't collide with that layer: fall back to Default
                Plugin.L.LogWarning($"layer {Layer} doesn't collide with the player's layer {pl}: using Default for blocks");
                Layer = 0;
            }
            Plugin.L.LogInfo("mirrored blocks use layer " + Layer);
        }

        public static void Reset()
        {
            foreach (var go in Placed.Values) if (go != null) UnityEngine.Object.Destroy(go);
            Placed.Clear();
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
        }

        public static void Update()
        {
        }

        /// <summary>{"t":"blocks","set":[x,y,z,...],"clear":[x,y,z,...]}</summary>
        public static void OnBlocks(JsonElement m)
        {
            if (Core.PS == null) return;
            if (_root == null) _root = new GameObject("GrannyCraft blocks");
            if (m.TryGetProperty("clear", out var clear)) Each(clear, (x, y, z) =>
            {
                long k = Scanner.Key(x, y, z);
                if (Placed.TryGetValue(k, out var go))
                {
                    if (go != null) UnityEngine.Object.Destroy(go);
                    Placed.Remove(k);
                }
            });
            if (m.TryGetProperty("set", out var set)) Each(set, (x, y, z) =>
            {
                long k = Scanner.Key(x, y, z);
                if (Placed.ContainsKey(k)) return;
                var go = new GameObject($"mcblock {x} {y} {z}");
                go.layer = Layer;
                go.transform.SetParent(_root.transform, false);
                go.transform.position = Coords.BlockCenter(x, y, z);
                var box = go.AddComponent<BoxCollider>();
                box.size = Vector3.one * Coords.U;
                Ours.Add(box.GetInstanceID());
                var obs = go.AddComponent<NavMeshObstacle>();
                obs.shape = NavMeshObstacleShape.Box;
                obs.size = new Vector3(1.05f, 1f, 1.05f) * Coords.U;
                obs.carving = true;
                obs.carveOnlyStationary = true;
                Placed[k] = go;
            });
        }

        private static void Each(JsonElement arr, Action<int, int, int> f)
        {
            int n = arr.GetArrayLength();
            for (int i = 0; i + 2 < n; i += 3) f(arr[i].GetInt32(), arr[i + 1].GetInt32(), arr[i + 2].GetInt32());
        }

        public static int Count => Placed.Count;

        /// <summary>Test: is the NavMesh carved where each mirrored block stands (Granny paths around it)?</summary>
        public static void DevNavCheck()
        {
            int carved = 0, open = 0;
            foreach (var go in Placed.Values)
            {
                if (go == null) continue;
                Vector3 c = go.transform.position - Vector3.up * 0.5f * Coords.U;
                bool onMesh = NavMesh.SamplePosition(c, out NavMeshHit hit, 0.25f * Coords.U, NavMesh.AllAreas);
                if (onMesh) open++; else carved++;
                Plugin.L.LogInfo($"dev: block {go.name}: navmesh {(onMesh ? "still walkable at " + hit.position : "carved")}");
            }
            Plugin.L.LogInfo($"dev: navcheck {carved} carved, {open} still walkable");
        }

        private static readonly HashSet<int> Ours = new HashSet<int>();
        public static bool IsOurs(Collider c) => Ours.Contains(c.GetInstanceID());

        private static bool _collidersOn = true;

        public static void SetCollidersEnabled(bool on)
        {
            if (_collidersOn == on || Placed.Count == 0) { _collidersOn = on; return; }
            _collidersOn = on;
            foreach (var go in Placed.Values)
            {
                if (go == null) continue;
                var c = go.GetComponent<BoxCollider>();
                if (c != null) c.enabled = on;
            }
        }
    }
}
