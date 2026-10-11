using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace GrannyCraft
{
    /// <summary>
    /// Granny and Grandpa against Minecraft's hostile mobs (spawned with eggs in the house).
    ///
    /// Minecraft side: while they are up and about, each has a stand-in where it stands (sent ~10 times a second), so
    /// the mobs see them, chase them and hit them; every hit comes back here ({"t":"hurt"}) and is acted out like the
    /// player's (health, stagger, stun, knockdown). Granny side: they go for a mob they see, or one that is after them:
    /// they run to it on their NavMesh and hit it with Minecraft damage ({"t":"mobhit"}, it fights back). Meanwhile
    /// their own AI pauses (as during a stagger) and picks up again when no mob is left.
    /// </summary>
    internal static partial class Enemies
    {
        private class McMob
        {
            public int Id, Target;
            public Vector3 Feet;
            public float Height, Width;
            public string Type;
        }

        /// <summary>The hostile mobs around, from Minecraft's latest list.</summary>
        private static readonly Dictionary<int, McMob> Mobs = new Dictionary<int, McMob>();
        /// <summary>Mobs she couldn't reach: left alone for a while.</summary>
        private static readonly Dictionary<int, float> IgnoredUntil = new Dictionary<int, float>();
        private static float _mobsAt, _nextStandIns;
        private static bool _standInsSent;
        private const float GiveUpSeconds = 8f, SwingSeconds = 1.1f;

        public static void ClearMobs()
        {
            Mobs.Clear();
            IgnoredUntil.Clear();
            _standInsSent = false;
        }

        private static bool IsBig(Enemy e) => e.Kind == "granny" || e.Kind == "grandpa";

        /// <summary>{"t":"mobs","m":[[id,x,y,z,height,width,"zombie",target],...]} (Minecraft coordinates, blocks).</summary>
        public static void OnMobs(JsonElement m)
        {
            Mobs.Clear();
            foreach (var a in m.GetProperty("m").EnumerateArray())
            {
                var mob = new McMob
                {
                    Id = a[0].GetInt32(),
                    Feet = Coords.ToUnity(a[1].GetDouble(), a[2].GetDouble(), a[3].GetDouble()),
                    Height = (float)a[4].GetDouble() * Coords.U,
                    Width = (float)a[5].GetDouble() * Coords.U,
                    Type = a[6].GetString(),
                    Target = a[7].GetInt32(),
                };
                Mobs[mob.Id] = mob;
            }
            _mobsAt = Time.unscaledTime;
        }

        /// <summary>{"t":"hurt","id":id,"d":damage,"by":"zombie","from":[x,y,z]}: a mob hit Granny or Grandpa.</summary>
        public static void OnHurt(JsonElement m)
        {
            if (!All.TryGetValue(m.GetProperty("id").GetInt32(), out var e) || !Visible(e)) return;
            var f = m.GetProperty("from");
            Vector3 from = Coords.ToUnity(f[0].GetDouble(), f[1].GetDouble(), f[2].GetDouble());
            Damage(e, (float)m.GetProperty("d").GetDouble(), e.Go.transform.position - from, m.GetProperty("by").GetString());
        }

        /// <summary>Knocked down (the game's ragdoll), dying, or in the middle of catching the player.</summary>
        private static bool Down(Enemy e)
        {
            if (_ec == null) _ec = UnityEngine.Object.FindObjectOfType<EnemyController>();
            if (e.Ai is AI_Granny g) return g.IsDying || g.CaughtPlayer || _ec != null && _ec.GrannyKilled;
            if (e.Ai is AI_Grandpa gp) return gp.IsDying || gp.CaughtPlayer || _ec != null && _ec.GrandpaKilled;
            return true;
        }

        /// <summary>While fighting a mob, their own AI waits (AiPatches).</summary>
        public static bool IsBusy(GameObject go)
        {
            if (go == null || All.Count == 0 || !All.TryGetValue(go.GetInstanceID(), out var e)) return false;
            return e.Staggered && Time.time < e.StaggerUntil || e.Brawling;
        }

        /// <summary>Every frame (from Update): stand-ins to Minecraft, then each of them on its mob.</summary>
        private static void UpdateMobs()
        {
            if (Time.unscaledTime - _mobsAt > 1f) Mobs.Clear(); // Minecraft went quiet: nothing left to fight
            SendStandIns();
            foreach (var e in All.Values)
            {
                if (!IsBig(e)) continue;
                if (!Plugin.FightMobs.Value || !Link.Connected || !Visible(e) || Down(e))
                {
                    StopBrawl(e);
                    continue;
                }
                if (e.Staggered) continue; // a hit interrupts: back to it right after
                var mob = ChooseMob(e);
                if (mob == null)
                {
                    StopBrawl(e);
                    continue;
                }
                if (mob.Id != e.BrawlTarget)
                {
                    if (!e.Brawling) Plugin.L.LogInfo($"{e.Kind} goes for a {mob.Type} ({Vector3.Distance(e.Go.transform.position, mob.Feet) * Coords.S:F1} blocks)");
                    e.BrawlTarget = mob.Id;
                    e.BrawlSince = Time.time;
                }
                e.Brawling = true;
                try { Fight(e, mob); }
                catch (Exception ex) { Plugin.L.LogWarning($"{e.Kind} vs {mob.Type}: {ex.Message}"); }
            }
        }

        /// <summary>{"t":"standins","s":[[id,"granny",x,y,z,width,height,yaw],...]}: who mobs can see and hit, and where.</summary>
        private static void SendStandIns()
        {
            if (Time.unscaledTime < _nextStandIns || !Link.Connected) return;
            _nextStandIns = Time.unscaledTime + 0.1f;
            var sb = new StringBuilder("{\"t\":\"standins\",\"s\":[");
            int n = 0;
            if (Plugin.FightMobs.Value)
            {
                foreach (var e in All.Values)
                {
                    if (!IsBig(e) || !Visible(e) || Down(e)) continue;
                    var t = e.Go.transform;
                    var p = Coords.ToMc(t.position);
                    Coords.YawPitch(t.forward, out float yaw, out _);
                    sb.Append(n++ == 0 ? "[" : ",[").Append(e.Id).Append(",\"").Append(e.Kind).Append("\",")
                      .Append(Core.D(p.x)).Append(',').Append(Core.D(p.y)).Append(',').Append(Core.D(p.z)).Append(',')
                      .Append(Core.F(e.Width * (float)Coords.S)).Append(',').Append(Core.F(e.Height * (float)Coords.S)).Append(',')
                      .Append(Core.F(yaw)).Append(']');
                }
            }
            if (n == 0 && !_standInsSent) return; // nobody, and Minecraft knows it already
            _standInsSent = n > 0;
            Link.Send(sb.Append("]}").ToString());
        }

        /// <summary>
        /// The mob to go for: the nearest one after her (she knows it is there), else the nearest she can see within
        /// MobSightBlocks; the one she is on already she keeps following around corners. The player comes first while
        /// she is chasing them and they are nearer.
        /// </summary>
        private static McMob ChooseMob(Enemy e)
        {
            Vector3 pos = e.Go.transform.position;
            Vector3 eyes = pos + Vector3.up * e.Height * 0.9f;
            float sight = Mathf.Max(1f, Plugin.MobSightBlocks.Value) * Coords.U;
            McMob best = null;
            float bestScore = float.MaxValue, bestDist = 0f;
            foreach (var m in Mobs.Values)
            {
                if (IgnoredUntil.TryGetValue(m.Id, out float until) && Time.time < until) continue;
                float d = Vector3.Distance(pos, m.Feet);
                bool afterHer = m.Target == e.Id;
                bool current = m.Id == e.BrawlTarget;
                if (!afterHer)
                {
                    if (current ? d > sight * 1.5f : d > sight || Blocked(eyes, m.Feet + Vector3.up * m.Height * 0.6f)) continue;
                }
                float score = afterHer ? d * 0.5f : d;
                if (score < bestScore) { bestScore = score; best = m; bestDist = d; }
            }
            if (best == null || Core.PlayerRoot == null) return best;
            bool chasingPlayer = e.Ai is AI_Granny g ? g.IsChasing : e.Ai is AI_Grandpa gp && gp.IsChasing;
            if (chasingPlayer && !e.Brawling && Vector3.Distance(pos, Core.PlayerRoot.position) < bestDist) return null;
            return best;
        }

        /// <summary>Run to the mob; within reach, stop, face it and hit it every SwingSeconds.</summary>
        private static void Fight(Enemy e, McMob m)
        {
            var t = e.Go.transform;
            Vector3 to = m.Feet - t.position;
            float dy = to.y;
            to.y = 0f;
            float flat = to.magnitude;
            float reach = (e.Width + m.Width) * 0.5f + 1.0f * Coords.U;
            bool inReach = flat <= reach && Mathf.Abs(dy) < Mathf.Max(e.Height, m.Height);
            var a = e.Agent;
            GetClips(e, out var walk, out var idle, out var swing, out float run);

            if (inReach)
            {
                if (a != null && a.isOnNavMesh)
                {
                    a.isStopped = true;
                    a.velocity = Vector3.zero;
                }
                if (flat > 1e-3f) t.rotation = Quaternion.Slerp(t.rotation, Quaternion.LookRotation(to / flat, Vector3.up), Mathf.Clamp01(Time.deltaTime * 10f));
                if (Time.time >= e.NextSwing)
                {
                    e.NextSwing = Time.time + SwingSeconds;
                    e.BrawlSince = Time.time;
                    var played = Play(e, swing ?? idle, restart: true);
                    e.SwingUntil = Time.time + (played != null ? Mathf.Clamp(played.length, 0.3f, SwingSeconds) : 0.5f);
                    float dmg = e.Kind == "granny" ? Plugin.GrannyMobDamage.Value : Plugin.GrandpaMobDamage.Value;
                    Link.Send("{\"t\":\"mobhit\",\"id\":" + m.Id + ",\"d\":" + Core.F(dmg) + ",\"by\":" + e.Id + "}");
                    Plugin.L.LogInfo($"{e.Kind} hits a {m.Type} for {dmg:F1}");
                }
                else if (Time.time >= e.SwingUntil) Play(e, idle);
                return;
            }

            if (Time.time - e.BrawlSince > GiveUpSeconds)
            {
                // no hit for a while: out of her reach (on a block pile, behind a wall she can't path around...)
                IgnoredUntil[m.Id] = Time.time + GiveUpSeconds;
                Plugin.L.LogInfo($"{e.Kind} can't get to the {m.Type}: leaves it for now");
                e.BrawlTarget = 0;
                return;
            }
            if (a != null && a.isOnNavMesh)
            {
                a.isStopped = false;
                if (run > 0f) a.speed = run;
                if (Time.time >= e.NextPath)
                {
                    e.NextPath = Time.time + 0.25f;
                    a.SetDestination(m.Feet);
                }
            }
            if (Time.time >= e.SwingUntil) Play(e, walk);
        }

        private static void GetClips(Enemy e, out AnimationClip walk, out AnimationClip idle, out AnimationClip swing, out float run)
        {
            walk = idle = swing = null;
            run = 0f;
            if (e.Ai is AI_Granny g) { walk = g.WalkAnim; idle = g.Idle; swing = g.JumpscarePlayer; run = g.Run_Speed; }
            else if (e.Ai is AI_Grandpa gp) { walk = gp.WalkAnim; idle = gp.Idle; swing = gp.JumpscarePlayer; run = gp.Run_Speed; }
        }

        /// <summary>Crossfade to one of the character's own clips (only if its Animation has it); returns the clip played.</summary>
        private static AnimationClip Play(Enemy e, AnimationClip clip, bool restart = false)
        {
            if (clip == null) return null;
            string name = clip.name;
            if (!restart && e.CurrentAnim == name) return clip;
            var anim = e.Ai is AI_Granny g ? g.Anim : e.Ai is AI_Grandpa gp ? gp.Anim : null;
            if (anim == null || anim.GetClip(name) == null) return null;
            if (restart) anim.Rewind(name);
            anim.CrossFade(name, 0.12f);
            e.CurrentAnim = name;
            return clip;
        }

        private static void StopBrawl(Enemy e)
        {
            if (!e.Brawling) return;
            e.Brawling = false;
            e.BrawlTarget = 0;
            e.CurrentAnim = null;
            try
            {
                if (e.Agent != null && e.Agent.isOnNavMesh && !e.Staggered) e.Agent.isStopped = false;
                // her own AI decides again where to go (not to the mob's last spot)
                if (e.Go != null && e.Go.activeInHierarchy)
                {
                    if (e.Ai is AI_Granny g) g.ResetAIDecision();
                    else if (e.Ai is AI_Grandpa gp) gp.ResetAIDecision();
                }
            }
            catch (Exception ex) { Plugin.L.LogWarning("brawl end: " + ex.Message); }
            Plugin.L.LogInfo($"{e.Kind} stops fighting mobs");
        }

        /// <summary>Testing: a Minecraft mob in front of the player: mob zombie 4</summary>
        public static void DevMob(string type, float dist)
        {
            if (Core.Cam == null) return;
            Vector3 f = Core.Cam.transform.forward;
            f.y = 0f;
            f.Normalize();
            Vector3 feet = Core.CC != null ? new Vector3(Core.CC.bounds.center.x, Core.CC.bounds.min.y, Core.CC.bounds.center.z) : Core.PlayerRoot.position;
            var p = Coords.ToMc(feet + f * dist * Coords.U);
            string cmd = string.Format(System.Globalization.CultureInfo.InvariantCulture, "summon minecraft:{0} {1:F2} {2:F2} {3:F2}", type, p.x, p.y + 0.1, p.z);
            Link.Send("{\"t\":\"cmd\",\"c\":\"" + cmd + "\"}");
            Plugin.L.LogInfo("dev: " + cmd);
        }

        public static void DevMobs()
        {
            foreach (var m in Mobs.Values) Plugin.L.LogInfo($"dev: mob {m.Type} #{m.Id} at {m.Feet} target={m.Target}");
            foreach (var e in All.Values)
                if (IsBig(e)) Plugin.L.LogInfo($"dev: {e.Kind} brawling={e.Brawling} target={e.BrawlTarget} down={Visible(e) && Down(e)}");
        }
    }
}
