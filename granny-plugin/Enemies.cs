using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using UnityEngine;
using UnityEngine.AI;

namespace GrannyCraft
{
    /// <summary>
    /// Minecraft's weapons against Granny's own characters. Nothing of them goes into Minecraft: Minecraft reports what
    /// Steve does (a swing with its real damage, arrows in flight, explosions) and the hits are found here, against
    /// Granny's, Grandpa's and the others' real bodies in Granny's world (a cylinder around each, walls in between block).
    ///
    /// Behaviour (Granny and Grandpa): every hit costs health (Minecraft damage points), plays the game's hit sound and
    /// pushes them back with a short stagger. Below half health the first time: stunned (the game's own pepper-spray
    /// stun). At zero: knocked down (the game's own knock-out ragdoll; they come back after the game's recover timer
    /// with full health). The third knockdown kills them for good. Smaller creatures die at zero.
    /// </summary>
    internal static partial class Enemies
    {
        private class Enemy
        {
            public int Id;
            public string Kind;
            public Component Ai;
            public GameObject Go;
            public NavMeshAgent Agent;
            public float Hp, MaxHp;
            public int Knockdowns;
            public bool Stunned, Dead;
            public float StaggerUntil;
            public float Height = 1.8f, Width = 0.6f;
            public Vector3 Knock;
            public float VisibleCheckUntil;
            public bool LastVisible;
            public float ShowHpUntil;
            public bool Staggered;
            public float SavedCatch = -1f;
            // fighting Minecraft's mobs (Mobs.cs)
            public bool Brawling;
            public int BrawlTarget;
            public float BrawlSince, NextSwing, SwingUntil, NextPath;
            public string CurrentAnim;
        }

        private static readonly Dictionary<int, Enemy> All = new Dictionary<int, Enemy>();
        private static float _nextFind, _nextSend;
        private static EnemyController _ec;

        public static void Reset()
        {
            All.Clear();
            _ec = null;
            ClearMobs();
        }

        public static IEnumerable<int> Layers()
        {
            var set = new HashSet<int>();
            foreach (var e in All.Values) if (e.Go != null) set.Add(e.Go.layer);
            return set;
        }

        public static void Find()
        {
            _nextFind = Time.unscaledTime + 3f;
            _ec = UnityEngine.Object.FindObjectOfType<EnemyController>();
            Add<AI_Granny>("granny", Plugin.GrannyHealth.Value);
            Add<AI_Grandpa>("grandpa", Plugin.GrandpaHealth.Value);
            Add<AI_Slendrina>("slendrina", Plugin.OtherHealth.Value);
            Add<AI_MomSpider>("momspider", Plugin.OtherHealth.Value);
            Add<AtticSpider>("spider", Plugin.OtherHealth.Value);
            Add<SpectralGranny>("spectral", Plugin.OtherHealth.Value);
            Add<AI_RobotEnemy>("robot", Plugin.OtherHealth.Value);
            Add<RatEnemy>("rat", 4f);
            Add<CrowManage>("crow", 6f);
        }

        private static void Add<T>(string kind, float hp) where T : Component
        {
            foreach (var c in Resources.FindObjectsOfTypeAll<T>())
            {
                if (c == null || c.gameObject.scene.name == null) continue; // prefab assets, not scene objects
                int id = c.gameObject.GetInstanceID();
                if (All.ContainsKey(id)) continue;
                var e = new Enemy { Id = id, Kind = kind, Ai = c, Go = c.gameObject, MaxHp = hp, Hp = hp, Agent = c.GetComponent<NavMeshAgent>() };
                Measure(e);
                All[id] = e;
                Plugin.L.LogInfo($"enemy {kind} '{c.gameObject.name}' id={id} layer={c.gameObject.layer} size {e.Width:F2}x{e.Height:F2} active={c.gameObject.activeInHierarchy}");
            }
        }

        private static void Measure(Enemy e)
        {
            float agentH = 0f, agentW = 0f, rendH = 0f, rendW = 0f;
            if (e.Agent != null)
            {
                agentH = e.Agent.height * e.Go.transform.lossyScale.y;
                agentW = e.Agent.radius * 2f * e.Go.transform.lossyScale.x;
            }
            // skinned meshes only (the body), not lights, props or trigger volumes hanging off the parent
            var rs = e.Go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (rs.Length > 0)
            {
                Bounds b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                rendH = b.size.y;
                rendW = Mathf.Min(b.size.x, b.size.z);
            }
            float u = Coords.U; // Granny units per block
            e.Height = rendH > 0.3f * u && rendH < 4f * u ? rendH : agentH > 0.2f * u ? agentH : 1.8f * u;
            e.Width = Mathf.Clamp(agentW > 0.1f * u ? agentW : rendW, 0.3f * u, 1.5f * u);
            if (e.Kind == "rat") { e.Height = Mathf.Min(e.Height, 0.6f * u); e.Width = Mathf.Min(e.Width, 0.6f * u); }
            Plugin.L.LogInfo($"{e.Kind}: agent {agentH:F2}x{agentW:F2} skinned {rendH:F2}x{rendW:F2} -> {e.Height * Coords.S:F2} x {e.Width * Coords.S:F2} blocks");
        }

        /// <summary>Warp an enemy in front of the player's camera (testing): summon granny 2.2</summary>
        public static void DevSummon(string kind, float dist)
        {
            foreach (var e in All.Values)
            {
                if (e.Kind != kind || e.Go == null) continue;
                var ct = Core.Cam.transform;
                Vector3 f = ct.forward; f.y = 0f; f.Normalize();
                Vector3 feet = Core.CC != null ? new Vector3(Core.CC.bounds.center.x, Core.CC.bounds.min.y, Core.CC.bounds.center.z) : Core.PlayerRoot.position;
                Vector3 at = feet + f * dist * Coords.U;
                if (e.Agent != null && e.Agent.isOnNavMesh) e.Agent.Warp(at); else e.Go.transform.position = at;
                e.Go.transform.rotation = Quaternion.LookRotation(-f, Vector3.up);
                Plugin.L.LogInfo($"dev: {kind} summoned to {at}");
                return;
            }
        }

        /// <summary>The enemy the camera points at within reach (blocks), if any.</summary>
        public static string NearestInFront(float reachBlocks)
        {
            Vector3 o = Core.Cam.transform.position, dir = Core.Cam.transform.forward;
            var e = Pick(o, dir, reachBlocks * Coords.U, out _);
            return e?.Kind;
        }

        /// <summary>Primed TNT in Minecraft right next to an enemy (testing explosions): tntat granny 1.5</summary>
        public static void DevTnt(string kind, float offsetBlocks)
        {
            foreach (var e in All.Values)
            {
                if (e.Kind != kind || !Visible(e)) continue;
                Vector3 toPlayer = (Core.PlayerRoot.position - e.Go.transform.position); toPlayer.y = 0; toPlayer.Normalize();
                var p = Coords.ToMc(e.Go.transform.position + toPlayer * offsetBlocks * Coords.U);
                string cmd = string.Format(System.Globalization.CultureInfo.InvariantCulture, "summon minecraft:tnt {0:F2} {1:F2} {2:F2} {{fuse:30}}", p.x, p.y + 0.1, p.z);
                Link.Send("{\"t\":\"cmd\",\"c\":\"" + cmd.Replace("\"", "\\\"") + "\"}");
                Plugin.L.LogInfo("dev: " + cmd);
                return;
            }
        }

        public static void DevRecover(float seconds)
        {
            if (_ec == null) _ec = UnityEngine.Object.FindObjectOfType<EnemyController>();
            if (_ec == null) return;
            foreach (var e in All.Values) if (e.Dead && (e.Kind == "granny" || e.Kind == "grandpa")) return; // never revive the dead
            _ec.MaxTimerRecoverGranny = seconds;
            _ec.MaxTimerRecoverGrandpa = seconds;
            Plugin.L.LogInfo($"dev: recover timers {seconds}s (granny killed={_ec.GrannyKilled} t={_ec.TimerGrannyRecover:F1})");
        }

        public static void DevList()
        {
            foreach (var e in All.Values)
                Plugin.L.LogInfo($"dev: {e.Kind} '{e.Go?.name}' visible={Visible(e)} pos={e.Go?.transform.position} hp={e.Hp}/{e.MaxHp} kd={e.Knockdowns}");
        }

        /// <summary>Teleport the player in front of an enemy, looking at it (testing).</summary>
        public static void DevGoto(string kind, float dist)
        {
            foreach (var e in All.Values)
            {
                if (e.Kind != kind || !Visible(e)) continue;
                var t = e.Go.transform;
                Vector3 front = t.position + t.forward * dist * Coords.U;
                var cc = Core.CC;
                if (cc != null) cc.enabled = false;
                Core.PlayerRoot.position = front + Vector3.up * 0.2f * Coords.U;
                if (cc != null) cc.enabled = true;
                Vector3 look = t.position - front;
                Core.PlayerRoot.rotation = Quaternion.Euler(0f, Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg, 0f);
                Plugin.L.LogInfo($"dev: moved to {kind} at {t.position}");
                return;
            }
            Plugin.L.LogInfo("dev: no visible " + kind);
        }

        /// <summary>Present in the game right now: active, its AI running and its body drawn (not a dormant copy).</summary>
        private static bool Visible(Enemy e)
        {
            if (e.Go == null || !e.Go.activeInHierarchy || e.Dead) return false;
            var b = e.Ai.TryCast<Behaviour>();
            if (b != null && !b.enabled) return false;
            if (Time.unscaledTime < e.VisibleCheckUntil) return e.LastVisible;
            e.VisibleCheckUntil = Time.unscaledTime + 0.5f;
            e.LastVisible = false;
            foreach (var r in e.Go.GetComponentsInChildren<Renderer>())
            {
                if (r.enabled && r.TryCast<SkinnedMeshRenderer>() != null) { e.LastVisible = true; break; }
            }
            return e.LastVisible;
        }

        public static void Update()
        {
            if (Time.unscaledTime >= _nextFind) Find();
            foreach (var e in All.Values)
            {
                if (!e.Staggered) continue;
                if (Time.time < e.StaggerUntil && e.Agent != null && e.Agent.isOnNavMesh && e.Go.activeInHierarchy)
                {
                    // knocked back along the hit, standing still meanwhile
                    e.Agent.isStopped = true;
                    e.Agent.velocity = Vector3.zero;
                    e.Agent.Move(e.Knock * Time.deltaTime);
                    e.Knock = Vector3.Lerp(e.Knock, Vector3.zero, Time.deltaTime * 5f);
                    continue;
                }
                EndStagger(e);
            }
            UpdateMobs();
        }

        private static void EndStagger(Enemy e)
        {
            e.Staggered = false;
            try
            {
                if (e.Agent != null && e.Agent.isOnNavMesh) e.Agent.isStopped = false;
                if (e.SavedCatch >= 0f)
                {
                    if (e.Ai is AI_Granny g) g.DistanceJumpscare = e.SavedCatch;
                    else if (e.Ai is AI_Grandpa gp) gp.DistanceJumpscare = e.SavedCatch;
                    e.SavedCatch = -1f;
                }
            }
            catch (Exception ex) { Plugin.L.LogWarning("stagger end: " + ex.Message); }
        }

        // ---------------------------------------------------------------------------------------------------------
        /// <summary>Nearest hit of a ray (Unity) with an enemy's body cylinder, or -1.</summary>
        private static float RayCylinder(Enemy e, Vector3 o, Vector3 d, float maxDist)
        {
            Vector3 c = e.Go.transform.position;
            float r = e.Width * 0.5f + 0.1f * Coords.U;
            float ox = o.x - c.x, oz = o.z - c.z;
            float A = d.x * d.x + d.z * d.z, B = 2f * (ox * d.x + oz * d.z), C = ox * ox + oz * oz - r * r;
            float t0, t1;
            if (A < 1e-8f)
            {
                if (C > 0f) return -1f;
                t0 = 0f; t1 = maxDist;
            }
            else
            {
                float disc = B * B - 4f * A * C;
                if (disc < 0f) return -1f;
                float sq = Mathf.Sqrt(disc);
                t0 = (-B - sq) / (2f * A); t1 = (-B + sq) / (2f * A);
            }
            float y0 = c.y, y1 = c.y + e.Height;
            if (Mathf.Abs(d.y) > 1e-8f)
            {
                float s0 = (y0 - o.y) / d.y, s1 = (y1 - o.y) / d.y;
                if (s0 > s1) { float tmp = s0; s0 = s1; s1 = tmp; }
                t0 = Mathf.Max(t0, s0); t1 = Mathf.Min(t1, s1);
            }
            else if (o.y < y0 || o.y > y1) return -1f;
            if (t1 < Mathf.Max(t0, 0f)) return -1f;
            float t = Mathf.Max(t0, 0f);
            return t <= maxDist ? t : -1f;
        }

        /// <summary>Granny's walls between two points (geometry layers only, never the characters themselves).</summary>
        private static bool Blocked(Vector3 a, Vector3 b)
        {
            return Physics.Linecast(a, b, out RaycastHit h, Scanner.Mask, QueryTriggerInteraction.Ignore) && !BelongsToEnemy(h.collider);
        }

        public static bool IsEnemyPart(Transform t)
        {
            foreach (var e in All.Values) if (e.Go != null && t.IsChildOf(e.Go.transform)) return true;
            return false;
        }

        private static bool BelongsToEnemy(Collider c)
        {
            var t = c.transform;
            foreach (var e in All.Values) if (e.Go != null && t.IsChildOf(e.Go.transform)) return true;
            return false;
        }

        private static Enemy Pick(Vector3 o, Vector3 d, float maxDist, out float dist)
        {
            Enemy best = null;
            dist = maxDist;
            foreach (var e in All.Values)
            {
                if (!Visible(e)) continue;
                float t = RayCylinder(e, o, d, dist);
                if (t >= 0f && t < dist && !Blocked(o, o + d * t)) { best = e; dist = t; }
            }
            return best;
        }

        /// <summary>{"t":"melee","d":damage,"reach":blocks}: the swing goes along the camera (as Minecraft aims).</summary>
        public static void OnMelee(JsonElement m)
        {
            if (Core.Cam == null) return;
            float d = (float)m.GetProperty("d").GetDouble();
            float reach = (float)m.GetProperty("reach").GetDouble();
            // from the player's eyes along their look, like Minecraft (also in third person)
            Vector3 o = Core.Cam.transform.position;
            Vector3 dir = Core.Cam.transform.forward;
            float extra = 0f;
            bool crit = m.TryGetProperty("crit", out var c) && c.GetBoolean();
            var e = Pick(o, dir, reach * Coords.U + extra, out _);
            if (e != null) Damage(e, d, dir, crit ? "sword (critical)" : "sword");
            else
            {
                var sb = new StringBuilder($"swing ({d:F1}) missed:");
                foreach (var x in All.Values)
                {
                    if (!Visible(x)) continue;
                    float t = RayCylinder(x, o, dir, 1000f);
                    float flat = Vector3.Distance(new Vector3(o.x, 0, o.z), new Vector3(x.Go.transform.position.x, 0, x.Go.transform.position.z)) * (float)Coords.S;
                    if (flat > 8f) continue;
                    sb.Append($" {x.Kind} {flat:F1} blocks away, ray {(t < 0 ? "misses body" : (t * Coords.S).ToString("F1") + " blocks" + (Blocked(o, o + dir * t) ? " (wall in between)" : ""))};");
                }
                Plugin.L.LogInfo(sb.ToString());
            }
        }

        private static readonly Dictionary<int, Vector3> ArrowLast = new Dictionary<int, Vector3>();
        private static float _arrowSweep;

        /// <summary>{"t":"proj","p":[[id,x,y,z,vx,vy,vz,damage],...]}: each arrow's path since last tick, traced here.</summary>
        public static void OnProjectiles(JsonElement m)
        {
            var seen = new HashSet<int>();
            foreach (var a in m.GetProperty("p").EnumerateArray())
            {
                int id = a[0].GetInt32();
                seen.Add(id);
                Vector3 now = Coords.ToUnity(a[1].GetDouble(), a[2].GetDouble(), a[3].GetDouble());
                Vector3 vel = new Vector3(-(float)a[4].GetDouble(), (float)a[5].GetDouble(), (float)a[6].GetDouble()) * Coords.U;
                float dmg = (float)a[7].GetDouble();
                // from where it was last tick (or one tick back) to one tick ahead: arrows move ~3 blocks per tick
                Vector3 from = ArrowLast.TryGetValue(id, out var last) ? last : now - vel;
                Vector3 to = now + vel;
                ArrowLast[id] = now;
                Vector3 seg = to - from;
                float len = seg.magnitude;
                if (len < 1e-4f) continue;
                var e = Pick(from, seg / len, len, out float enemyDist);
                if (e == null) enemyDist = len;
                // Granny's objects in the way (a vase, a box, a wall): hit like the shotgun hits them
                if (!Impacted.Contains(id) && ObjectHit(from, seg / len, enemyDist, out RaycastHit hit))
                {
                    Impacted.Add(id);
                    ArrowImpact(id, hit, seg / len, dmg);
                    if (!Scanner.IsHouseGeometry(hit.collider)) { ArrowLast.Remove(id); continue; }
                }
                if (e != null)
                {
                    Damage(e, dmg, seg / len, "arrow");
                    Link.Send("{\"t\":\"projhit\",\"id\":" + id + "}");
                    ArrowLast.Remove(id);
                }
            }
            if (Time.unscaledTime > _arrowSweep)
            {
                _arrowSweep = Time.unscaledTime + 5f;
                var stale = new List<int>();
                foreach (var k in ArrowLast.Keys) if (!seen.Contains(k)) stale.Add(k);
                foreach (var k in stale) { ArrowLast.Remove(k); Impacted.Remove(k); }
            }
        }

        private static readonly HashSet<int> Impacted = new HashSet<int>();
        private static readonly int NotObjects = (1 << 2) | (1 << 5) | (1 << 8) | LayerMask.GetMask("GrannyParent", "SpiderHead", "GrandpaArm", "Dead");

        /// <summary>Nearest of Granny's objects along an arrow's path (not the player, not the characters, not our blocks).</summary>
        private static bool ObjectHit(Vector3 o, Vector3 d, float maxDist, out RaycastHit best)
        {
            best = default;
            bool found = false;
            float bestDist = maxDist;
            foreach (var h in Physics.RaycastAll(o, d, maxDist, ~NotObjects, QueryTriggerInteraction.Ignore))
            {
                if (h.collider == null || h.distance >= bestDist || Blocks.IsOurs(h.collider) || BelongsToEnemy(h.collider)) continue;
                if (Core.PlayerRoot != null && h.collider.transform.IsChildOf(Core.PlayerRoot)) continue;
                best = h; bestDist = h.distance; found = true;
            }
            return found;
        }

        /// <summary>The shotgun's impact (effect and sound) where the arrow hits; loose objects are knocked away and the arrow stops there.</summary>
        private static void ArrowImpact(int id, RaycastHit hit, Vector3 dir, float dmg)
        {
            var pr = Core.PS?.PickRay;
            try
            {
                if (pr != null && pr.ImpactLord != null)
                {
                    var fx = UnityEngine.Object.Instantiate(pr.ImpactLord, hit.point + hit.normal * 0.02f, Quaternion.LookRotation(hit.normal));
                    fx.SetActive(true);
                    UnityEngine.Object.Destroy(fx, 4f);
                }
                if (pr != null && pr.ImpactSound != null) AudioSource.PlayClipAtPoint(pr.ImpactSound, hit.point, 0.7f);
            }
            catch (Exception ex) { Plugin.L.LogWarning("arrow impact effect: " + ex.Message); }
            var rb = hit.rigidbody;
            if (rb != null && !rb.isKinematic)
                rb.AddForceAtPosition(dir * Mathf.Clamp(dmg, 2f, 12f) * 0.6f * Coords.U, hit.point, ForceMode.VelocityChange);
            if (!Scanner.IsHouseGeometry(hit.collider))
            {
                // Minecraft doesn't have this object: the arrow stops at it there
                var p = Coords.ToMc(hit.point);
                Link.Send("{\"t\":\"projhit\",\"id\":" + id + ",\"pos\":[" + Core.D(p.x) + "," + Core.D(p.y) + "," + Core.D(p.z) + "]}");
            }
            Plugin.L.LogInfo($"arrow hit {hit.collider.name}{(rb != null && !rb.isKinematic ? " (knocked)" : "")}");
        }

        /// <summary>
        /// An explosion (TNT), with Minecraft's own formula: within twice the power, impact = (1 - distance / 2r) x
        /// exposure (the share of rays from the blast to the body not stopped by Granny's walls), and damage =
        /// (impact^2 + impact) / 2 x 7 x 2r + 1.
        /// </summary>
        public static void OnExplosion(Vector3 at, float powerBlocks)
        {
            float reach = 2f * powerBlocks;
            foreach (var e in All.Values)
            {
                if (!Visible(e)) continue;
                Vector3 feet = e.Go.transform.position;
                Vector3 center = feet + Vector3.up * e.Height * 0.5f;
                float distBlocks = Vector3.Distance(at, center) * (float)Coords.S;
                if (distBlocks > reach) continue;
                int open = 0, total = 0;
                for (int i = 0; i <= 2; i++)
                    for (int j = -1; j <= 1; j++)
                    {
                        Vector3 p = feet + Vector3.up * (e.Height * (0.1f + 0.4f * i)) + new Vector3(j * e.Width * 0.4f, 0f, 0f);
                        total++;
                        if (!Blocked(at, p)) open++;
                    }
                float impact = (1f - distBlocks / reach) * open / total;
                if (impact <= 0f) continue;
                float dmg = (impact * impact + impact) / 2f * 7f * reach + 1f;
                Damage(e, dmg, (center - at).normalized, "explosion");
            }
        }

        private static void Damage(Enemy e, float d, Vector3 dir, string src)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude > 1e-6f) dir.Normalize();
            e.Hp -= d;
            e.ShowHpUntil = Time.time + 3f;
            Plugin.L.LogInfo($"{e.Kind} hit by {src} for {d:F1}: {Mathf.Max(0, e.Hp):F1}/{e.MaxHp}");
            try { React(e, d, dir); }
            catch (Exception ex) { Plugin.L.LogWarning($"{e.Kind} reaction failed: {ex.Message}"); }
        }

        private static void React(Enemy e, float d, Vector3 dir)
        {
            bool big = e.Kind == "granny" || e.Kind == "grandpa";
            if (e.Hp > 0f)
            {
                Stagger(e, dir.sqrMagnitude > 0.01f ? dir : -e.Go.transform.forward, Mathf.Clamp(d * 0.8f, 4f, 9f) * Coords.U);
                if (big)
                {
                    if (e.Kind == "granny") { var a = (AI_Granny)e.Ai; a.HitSound?.Play(); }
                    else { var a = (AI_Grandpa)e.Ai; a.HitSound?.Play(); }
                    if (!e.Stunned && e.Hp <= e.MaxHp * 0.5f)
                    {
                        e.Stunned = true;
                        Plugin.L.LogInfo($"{e.Kind} stunned");
                        if (e.Kind == "granny") ((AI_Granny)e.Ai).PepperHit(); else ((AI_Grandpa)e.Ai).PepperHit();
                    }
                }
                return;
            }

            if (big)
            {
                e.Knockdowns++;
                bool kill = e.Knockdowns >= Plugin.KnockdownsToKill.Value;
                Plugin.L.LogInfo($"{e.Kind} knocked down ({e.Knockdowns}/{Plugin.KnockdownsToKill.Value}){(kill ? ": dead for good" : "")}");
                e.Hp = e.MaxHp;
                e.Stunned = false;
                if (_ec == null) _ec = UnityEngine.Object.FindObjectOfType<EnemyController>();
                if (e.Kind == "granny") _ec.GrannyDeath(false); else _ec.GrandpaDeath(false);
                if (kill)
                {
                    e.Dead = true;
                    // the game brings them back when its recover timer runs out: it never runs out
                    if (e.Kind == "granny") { _ec.MaxTimerRecoverGranny = 1e9f; _ec.TimerGrannyRecover = 0f; }
                    else { _ec.MaxTimerRecoverGrandpa = 1e9f; _ec.TimerGrandpaRecover = 0f; }
                }
                return;
            }

            // small creatures: gone
            e.Dead = true;
            Plugin.L.LogInfo($"{e.Kind} killed");
            switch (e.Ai)
            {
                case AI_MomSpider mom:
                    if (_ec != null) _ec.MomFroze();
                    break;
                default:
                    e.Go.SetActive(false);
                    break;
            }
        }

        public static void DevHurt(string kind, float d)
        {
            foreach (var e in All.Values)
            {
                if (e.Kind != kind || !Visible(e)) continue;
                Damage(e, d, Vector3.zero, "dev");
                return;
            }
            Plugin.L.LogInfo("dev: no visible " + kind);
        }

        /// <summary>
        /// A hit interrupts: knocked back (~1.2 blocks, a strong Minecraft hit), stopped for 0.6 s, and she can't grab the
        /// player during it (the game's catch distance is set to 0 meanwhile, then restored).
        /// </summary>
        private static void Stagger(Enemy e, Vector3 dir, float speed)
        {
            e.StaggerUntil = Time.time + 0.6f;
            e.Knock = dir * speed;
            if (!e.Staggered)
            {
                e.Staggered = true;
                if (e.Ai is AI_Granny g) { e.SavedCatch = g.DistanceJumpscare; g.DistanceJumpscare = 0f; }
                else if (e.Ai is AI_Grandpa gp) { e.SavedCatch = gp.DistanceJumpscare; gp.DistanceJumpscare = 0f; }
            }
        }

        /// <summary>Health of Granny and Grandpa (status line).</summary>
        public static string Summary()
        {
            var sb = new StringBuilder();
            foreach (var e in All.Values) if (e.Kind == "granny" || e.Kind == "grandpa") sb.Append($"{e.Kind}:{e.Hp:F0}/{e.MaxHp:F0} kd{e.Knockdowns}{(e.Dead ? " DEAD" : "")} ");
            return sb.ToString();
        }
    }
}
