using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace GrannyCraft
{
    /// <summary>
    /// Turns Granny's collision into Minecraft collision: every block cell that touches Granny's (non-trigger) colliders
    /// is sampled as 4x4x4 sub-voxels; the 64-bit occupancy goes to Minecraft as an invisible "ghost" block with exactly
    /// that shape. Time-sliced (a few ms per frame). Cells near the player are re-scanned (doors open and close).
    /// </summary>
    internal static class Scanner
    {
        public static int Mask = ~0;
        public static string Status = "-";
        private static readonly Dictionary<long, ulong> Known = new Dictionary<long, ulong>();
        private static readonly Queue<long> Todo = new Queue<long>();
        private static readonly HashSet<long> Queued = new HashSet<long>();
        private static readonly List<long> Pending = new List<long>();
        private static readonly Stopwatch Sw = new Stopwatch();
        private static bool _begun;
        private static float _nextLocal;
        private static int _scanned, _occupied;
        private const float BudgetMs = 3.5f;
        // IL2CPP arrays: a managed Collider[] would be copied in and the results lost
        private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider> Buf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider>(16384);

        public static long Key(int x, int y, int z) => ((long)(x & 0x3FFFFF) << 42) | ((long)(y & 0xFFFFF) << 22) | (long)(z & 0x3FFFFF);

        /// <summary>A cell grew back (Carve): measure it again, so Minecraft gets its collision back.</summary>
        public static void Rescan(long k)
        {
            if (_begun && Queued.Add(k)) Todo.Enqueue(k);
        }

        public static void Unkey(long k, out int x, out int y, out int z)
        {
            x = (int)(k >> 42) & 0x3FFFFF; if (x >= 0x200000) x -= 0x400000;
            y = (int)(k >> 22) & 0xFFFFF; if (y >= 0x80000) y -= 0x100000;
            z = (int)k & 0x3FFFFF; if (z >= 0x200000) z -= 0x400000;
        }

        public static void Reset()
        {
            Known.Clear(); Todo.Clear(); Queued.Clear(); Pending.Clear();
            _begun = false; _scanned = _occupied = 0; Status = "-";
        }

        public static void Begin()
        {
            Reset();
            // the house's geometry layers (not items, the player, enemies, triggers or AI-only blockers)
            int mask = LayerMask.GetMask("Default", "HouseFloor", "EnemyPass", "Door", "SpiderFloor", "Non-Ground", "Moves", "StairColliders");
            if (mask == 0) mask = 1;
            if (Blocks.Layer != 0) mask &= ~(1 << Blocks.Layer);
            Mask = mask;
            Allowed.Clear();
            Plugin.L.LogInfo($"collision scan mask {Mask:X8}");
            QueueRegion(Core.PlayerRoot.position, Plugin.ScanRadius.Value);
            _begun = true;
        }

        /// <summary>Minecraft (re)connected: it has none of our ghost blocks yet.</summary>
        public static void Resend()
        {
            Pending.Clear();
            foreach (var kv in Known) if (kv.Value != 0) Pending.Add(kv.Key);
        }

        /// <summary>Queue every cell overlapped by the bounds of a collider within the region.</summary>
        private static void QueueRegion(Vector3 center, float radius)
        {
            int n = Physics.OverlapBoxNonAlloc(center, new Vector3(radius, radius * 0.5f, radius), Buf, Quaternion.identity, Mask, QueryTriggerInteraction.Ignore);
            int allowed = 0, before = Todo.Count;
            var region = new Bounds(center, new Vector3(radius * 2, radius, radius * 2));
            for (int i = 0; i < n; i++)
            {
                var col = Buf[i];
                if (col == null || !IsAllowed(col)) continue;
                allowed++;
                Bounds b = col.bounds;
                // clamp huge colliders (terrain, ground planes) to the region
                Vector3 min = Vector3.Max(b.min, region.min), max = Vector3.Min(b.max, region.max);
                if (min.x > max.x || min.y > max.y || min.z > max.z) continue;
                QueueBox(min, max);
            }
            if (radius > 20f * Coords.U || Todo.Count > before + 5000)
                Plugin.L.LogInfo($"scan region r={radius:F0}: {n} colliders, {allowed} geometry, {Todo.Count - before} cells queued");
        }

        private static void QueueBox(Vector3 umin, Vector3 umax)
        {
            var a = Coords.ToMc(umin);
            var b = Coords.ToMc(umax);
            int x0 = (int)Math.Floor(Math.Min(a.x, b.x)), x1 = (int)Math.Floor(Math.Max(a.x, b.x));
            int y0 = (int)Math.Floor(Math.Min(a.y, b.y)), y1 = (int)Math.Floor(Math.Max(a.y, b.y));
            int z0 = (int)Math.Floor(Math.Min(a.z, b.z)), z1 = (int)Math.Floor(Math.Max(a.z, b.z));
            long count = (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
            if (count > 400000) return;
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long k = Key(x, y, z);
                        if (Queued.Add(k)) Todo.Enqueue(k);
                    }
        }

        /// <summary>Granny, Grandpa, the player and anything with a non-kinematic rigidbody are not walls.</summary>
        private static bool IsDynamicActor(Collider c)
        {
            var rb = c.attachedRigidbody;
            if (rb != null && !rb.isKinematic) return true;
            return false;
        }

        private static readonly Dictionary<int, bool> Allowed = new Dictionary<int, bool>();
        private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider> Hits = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Collider>(64);

        /// <summary>Real, visible geometry: a mesh/terrain collider, or a collider on something that is drawn.</summary>
        /// <summary>Part of Granny's house as Minecraft has it (a ghost block): Minecraft's own collision handles it.</summary>
        public static bool IsHouseGeometry(Collider c) => ((1 << c.gameObject.layer) & Mask) != 0 && IsAllowed(c);

        private static bool IsAllowed(Collider c)
        {
            int id = c.GetInstanceID();
            if (Allowed.TryGetValue(id, out bool ok)) return ok;
            ok = !c.isTrigger && !IsDynamicActor(c) && !Blocks.IsOurs(c);
            if (ok)
            {
                // every solid collider on the house's layers counts (floors and walls are often separate invisible
                // boxes), except the invisible helpers that only keep Granny's items from getting stuck
                string n = c.name;
                bool itemHelper = n.IndexOf("ItemAvoid", StringComparison.OrdinalIgnoreCase) >= 0
                                  || n.IndexOf("AntiItem", StringComparison.OrdinalIgnoreCase) >= 0
                                  || n.IndexOf("Softlock", StringComparison.OrdinalIgnoreCase) >= 0;
                ok = !itemHelper;
            }
            Allowed[id] = ok;
            return ok;
        }

        private static bool Any(Vector3 center, Vector3 half)
        {
            int n = Physics.OverlapBoxNonAlloc(center, half, Hits, Quaternion.identity, Mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++) if (Hits[i] != null && IsAllowed(Hits[i])) return true;
            return false;
        }

        public static void Step()
        {
            if (!_begun || Core.PlayerRoot == null) return;
            if (Time.unscaledTime >= _nextLocal && Todo.Count == 0)
            {
                // doors open and close: look again around the player
                _nextLocal = Time.unscaledTime + 1.5f;
                QueueRegion(Core.PlayerRoot.position, 7f * Coords.U);
                foreach (var kv in Known)
                {
                    // cells we have that may have emptied
                    Unkey(kv.Key, out int x, out int y, out int z);
                    var c = Coords.BlockCenter(x, y, z);
                    if ((c - Core.PlayerRoot.position).sqrMagnitude < 49f * Coords.U * Coords.U && Queued.Add(kv.Key)) Todo.Enqueue(kv.Key);
                }
            }

            Sw.Restart();
            while (Todo.Count > 0 && Sw.Elapsed.TotalMilliseconds < BudgetMs)
            {
                long k = Todo.Dequeue();
                Queued.Remove(k);
                Unkey(k, out int x, out int y, out int z);
                ulong mask = Sample(x, y, z);
                _scanned++;
                Known.TryGetValue(k, out ulong old);
                if (mask != old)
                {
                    if (mask == 0) Known.Remove(k); else Known[k] = mask;
                    Pending.Add(k);
                    if (old == 0) _occupied++;
                    else if (mask == 0) _occupied--;
                }
            }
            Status = $"{_occupied} cells ({Todo.Count} to scan)";
            Flush();
        }

        private static Vector3 Half => Vector3.one * (0.499f * Coords.U);
        private static Vector3 HalfOct => Vector3.one * (0.249f * Coords.U);
        private static Vector3 HalfSub => Vector3.one * (0.1245f * Coords.U);

        /// <summary>64-bit occupancy of block (x, y, z): bit i + 4j + 16k is sub-voxel (i, j, k) along Minecraft x, y, z.</summary>
        private static ulong Sample(int x, int y, int z)
        {
            if (Carve.Broken.Contains(Key(x, y, z))) return 0; // broken out of the house
            if (!Any(Coords.BlockCenter(x, y, z), Half)) return 0;
            ulong m = 0;
            for (int o = 0; o < 8; o++)
            {
                int oi = o & 1, oj = (o >> 1) & 1, ok = (o >> 2) & 1;
                var oc = Coords.ToUnity(x + 0.25 + oi * 0.5, y + 0.25 + oj * 0.5, z + 0.25 + ok * 0.5);
                if (!Any(oc, HalfOct)) continue;
                for (int s = 0; s < 8; s++)
                {
                    int i = oi * 2 + (s & 1), j = oj * 2 + ((s >> 1) & 1), kk = ok * 2 + ((s >> 2) & 1);
                    var sc = Coords.ToUnity(x + (i + 0.5) * 0.25, y + (j + 0.5) * 0.25, z + (kk + 0.5) * 0.25);
                    if (Any(sc, HalfSub))
                        m |= 1UL << (i + 4 * j + 16 * kk);
                }
            }
            return m;
        }

        private static void Flush()
        {
            if (Pending.Count == 0 || !Link.Connected) return;
            var sb = new StringBuilder(16384);
            int n = 0;
            sb.Append("{\"t\":\"ghost\",\"c\":[");
            foreach (long k in Pending)
            {
                Unkey(k, out int x, out int y, out int z);
                Known.TryGetValue(k, out ulong m);
                if (n > 0) sb.Append(',');
                sb.Append(x).Append(',').Append(y).Append(',').Append(z).Append(',')
                  .Append((uint)(m & 0xFFFFFFFF)).Append(',').Append((uint)(m >> 32));
                if (++n >= 512)
                {
                    sb.Append("]}");
                    Link.Send(sb.ToString());
                    sb.Clear();
                    sb.Append("{\"t\":\"ghost\",\"c\":[");
                    n = 0;
                }
            }
            if (n > 0)
            {
                sb.Append("]}");
                Link.Send(sb.ToString());
            }
            Pending.Clear();
        }
    }
}
