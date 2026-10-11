using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace GrannyCraft
{
    /// <summary>
    /// Test harness: commands written to BepInEx\plugins\GrannyCraft\dev\granny_cmd.txt (one per line) run inside the game, results go
    /// to the BepInEx log. Lets the mod be tested end to end without anyone at the keyboard.
    /// </summary>
    internal static class DevConsole
    {
        public static string DevDir, ShotsDir;
        private static float _next;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        /// <summary>Simulated mouse buttons (forwarded exactly like the real ones).</summary>
        public static bool[] FakeDown = new bool[3], FakePressed = new bool[3], FakeReleased = new bool[3];
        public static float FakeWheel;

        public static void Init()
        {
            // everything stays next to the plugin (BepInEx\plugins\GrannyCraft\dev, \shots)
            DevDir = Path.Combine(Plugin.Dir, "dev");
            ShotsDir = Path.Combine(Plugin.Dir, "shots");
            Directory.CreateDirectory(DevDir);
            Directory.CreateDirectory(ShotsDir);
            string flags = Path.Combine(DevDir, "flags.txt");
            if (File.Exists(flags)) Flags = File.ReadAllText(flags);
            Plugin.L.LogInfo("dev flags: " + Flags);
        }

        public static string Flags = "";
        public static bool Has(string f) => Flags.Contains(f);

        private static float _autoUntil, _nextSwing, _nextAutoShot;
        private static string _autoTag;
        private static int _autoShot;

        /// <summary>Test player: attack whenever an enemy is within reach and the sword has recharged.</summary>
        public static void AutoFight()
        {
            if (Time.unscaledTime > _autoUntil || Core.Cam == null) return;
            if (Time.unscaledTime >= _nextAutoShot)
            {
                _nextAutoShot = Time.unscaledTime + 0.5f;
                Native.Capture(Path.Combine(ShotsDir, $"{_autoTag}_{_autoShot++:00}.bmp"));
            }
            if (Time.unscaledTime < _nextSwing) return;
            if (Enemies.NearestInFront(3.0f) == null) return;
            _nextSwing = Time.unscaledTime + 0.65f; // diamond sword: 1.6 attacks per second
            FakeDown[0] = true; FakePressed[0] = true; Core.PendingRelease[0] = 3;
        }

        public static void EndFrame()
        {
            for (int i = 0; i < 3; i++) { FakePressed[i] = false; FakeReleased[i] = false; }
            FakeWheel = 0f;
        }

        public static void Poll()
        {
            if (DevDir == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.2f;
            string f = Path.Combine(DevDir, "granny_cmd.txt");
            if (!File.Exists(f)) return;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(f);
                File.Delete(f);
            }
            catch { return; }
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { Run(line.Trim()); }
                catch (Exception e) { Plugin.L.LogWarning($"dev '{line}': {e}"); }
            }
        }

        private static float P(string s) => float.Parse(s, Inv);

        private static void Run(string line)
        {
            var a = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Plugin.L.LogInfo("dev> " + line);
            switch (a[0])
            {
                case "shot":
                    {
                        string name = a.Length > 1 ? a[1] : DateTime.Now.ToString("HHmmss");
                        string path = Path.Combine(ShotsDir, name + ".png");
                        // the compositor reads back the finished frame (Unity's own capture drops requests)
                        if (Core.Active) Native.Capture(Path.ChangeExtension(path, ".bmp")); else ScreenCapture.CaptureScreenshot(path);
                        Plugin.L.LogInfo("dev: screenshot " + path);
                        break;
                    }
                case "play":
                    {
                        var mm = UnityEngine.Object.FindObjectOfType<MainMenu>();
                        if (mm == null) { Plugin.L.LogInfo("dev: no MainMenu"); break; }
                        if (a.Length > 1 && a[1] == "load") mm.PlayLoad(); else mm.OnClickPlay();
                        break;
                    }
                case "tp":
                    {
                        var cc = Core.CC;
                        if (cc != null) cc.enabled = false;
                        Core.PlayerRoot.position = new Vector3(P(a[1]), P(a[2]), P(a[3]));
                        if (cc != null) cc.enabled = true;
                        break;
                    }
                case "look":
                    {
                        // yaw: player body, pitch: camera
                        var e = Core.PlayerRoot.eulerAngles;
                        Core.PlayerRoot.eulerAngles = new Vector3(e.x, P(a[1]), e.z);
                        var fps = UnityEngine.Object.FindObjectOfType<MobileFPS>();
                        if (fps != null && a.Length > 2) fps.rotationX = P(a[2]);
                        if (a.Length > 2) Core.Cam.transform.localEulerAngles = new Vector3(P(a[2]), 0, 0);
                        break;
                    }
                case "press": // press attack|use|pick
                case "down":
                case "up":
                    {
                        int b = a[1] == "attack" ? 0 : a[1] == "use" ? 1 : 2;
                        if (a[0] != "up") { FakeDown[b] = true; FakePressed[b] = true; }
                        if (a[0] == "up" || a[0] == "press") { if (a[0] == "press") Core.PendingRelease[b] = 3; else { FakeDown[b] = false; FakeReleased[b] = true; } }
                        break;
                    }
                case "wheel": FakeWheel = P(a[1]); break;
                case "key": FakeInput.Hold((KeyCode)Enum.Parse(typeof(KeyCode), a[1], true), 0.05f); break;
                case "hold": FakeInput.Hold((KeyCode)Enum.Parse(typeof(KeyCode), a[1], true), P(a[2])); break;
                case "slot": Link.Send("{\"t\":\"slot\",\"n\":" + int.Parse(a[1]) + "}"); break;
                case "view": Core.View = int.Parse(a[1]); break;
                case "toggle": Core.Enabled = !Core.Enabled; break;
                case "debug": Core.DebugView = int.Parse(a[1]); break;
                case "set":
                    {
                        // set <config key> <value>
                        var entry = Plugin.Instance.Config.Keys.FirstOrDefault(k => k.Key == a[1]);
                        if (entry != null) Plugin.Instance.Config[entry].SetSerializedValue(a[2]);
                        break;
                    }
                case "mc": Link.Send(line.Substring(3)); break; // raw JSON to Minecraft
                case "info": Info(); break;
                case "hurt": // hurt <kind> <amount>
                    Enemies.DevHurt(a[1], P(a[2]));
                    break;
                case "call": Call(a); break;
                case "autofight": // autofight <seconds> [tag]: swings like a player whenever an enemy is within reach
                    _autoUntil = Time.unscaledTime + P(a[1]);
                    _autoTag = a.Length > 2 ? a[2] : "auto";
                    _autoShot = 0;
                    _nextAutoShot = 0f;
                    break;
                case "goto": Enemies.DevGoto(a[1], a.Length > 2 ? P(a[2]) : 2.5f); break;
                case "enemies": Enemies.DevList(); break;
                case "mob": Enemies.DevMob(a[1], a.Length > 2 ? P(a[2]) : 4f); break; // mob zombie 4: a Minecraft mob in front of the player
                case "mobs": Enemies.DevMobs(); break;
                case "aimat": AimAt(a[1]); break;
                case "near": Near(a.Length > 1 ? P(a[1]) : 6f); break;
                case "movedebug": Movement.Debug(P(a[1])); break;
                case "navcheck": Blocks.DevNavCheck(); break;
                case "heldscan": HeldScan(); break;
                case "meshinfo": Carve.DevInfo(); break;
                case "meshapi": Carve.DevApi(); break;
                case "unbatch": Carve.DevUnbatchNearest(); break;
                case "peace": // testing: Granny and Grandpa stand still (peace off: back to normal)
                    {
                        bool off = a.Length > 1 && a[1] == "off";
                        foreach (var g in UnityEngine.Object.FindObjectsOfType<AI_Granny>()) g.enabled = off;
                        foreach (var g in UnityEngine.Object.FindObjectsOfType<AI_Grandpa>()) g.enabled = off;
                        foreach (var g in UnityEngine.Object.FindObjectsOfType<UnityEngine.AI.NavMeshAgent>()) if (g.isOnNavMesh) g.isStopped = !off;
                        Plugin.L.LogInfo("dev: peace " + (off ? "off" : "on"));
                        break;
                    }
                case "recover": Enemies.DevRecover(P(a[1])); break;
                case "tntat": Enemies.DevTnt(a[1], a.Length > 2 ? P(a[2]) : 1.5f); break;
                case "summon": Enemies.DevSummon(a[1], a.Length > 2 ? P(a[2]) : 2.2f); break;
                case "quit": Application.Quit(); break;
                default: Plugin.L.LogInfo("dev: unknown command"); break;
            }
        }

        /// <summary>Turn the player to look at the nearest object whose name contains the text.</summary>
        private static void AimAt(string text)
        {
            Transform best = null;
            float bd = float.MaxValue;
            Vector3 eye = Core.Cam.transform.position;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (!t.gameObject.activeInHierarchy || t.name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var r = t.GetComponentInChildren<Renderer>();
                Vector3 p = r != null ? r.bounds.center : t.position;
                float d = (p - eye).sqrMagnitude;
                if (d < bd) { bd = d; best = t; }
            }
            if (best == null) { Plugin.L.LogInfo("dev: nothing named " + text); return; }
            var rr = best.GetComponentInChildren<Renderer>();
            Vector3 target = rr != null ? rr.bounds.center : best.position;
            Vector3 dir = target - eye;
            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg;
            Run($"look {yaw.ToString(Inv)} {pitch.ToString(Inv)}");
            Plugin.L.LogInfo($"dev: aiming at {best.name} ({Mathf.Sqrt(bd) * Coords.S:F1} blocks)");
        }

        /// <summary>Diagnostics for held items and key bindings: what is drawn right in front of the camera, and where.</summary>
        private static void HeldScan()
        {
            var cam = Core.MainCam != null ? Core.MainCam : Core.Cam;
            Vector3 eye = cam.transform.position;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) continue;
                Vector3 local = cam.transform.InverseTransformPoint(r.bounds.center);
                if (local.z < 0f || local.magnitude > 2.5f * Coords.U) continue;
                string path = r.name;
                for (var t = r.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                Plugin.L.LogInfo($"dev: drawn near camera: {path} local={local} layer={LayerMask.LayerToName(r.gameObject.layer)}");
            }
            var pr = Core.PS?.PickRay;
            if (pr != null)
                Plugin.L.LogInfo($"dev: PickRay drop={pr.DropKey} main={pr.MainInteract} shoot={pr.ShootKey} ItemDrop={(pr.ItemDrop != null ? pr.ItemDrop.name + " active=" + pr.ItemDrop.activeInHierarchy : "-")} Drop1={(pr.Drop1 != null ? pr.Drop1.name : "-")}");
            foreach (var k in Resources.FindObjectsOfTypeAll<KeyBindManage>())
                Plugin.L.LogInfo($"dev: KeyBindManage '{k.name}' scene={k.gameObject.scene.name} active={k.gameObject.activeInHierarchy} drop={k.Dropping} primary={k.PrimaryInteraction} crouch={k.Crouching}");
            foreach (var key in new[] { "Dropping", "Drop", "DropKey", "KeyDrop", "Primary", "Crouching", "Shooting" })
                if (PlayerPrefs.HasKey(key)) Plugin.L.LogInfo($"dev: PlayerPrefs {key}={PlayerPrefs.GetInt(key)}");
        }

        private static void Near(float blocks)
        {
            Vector3 eye = Core.Cam.transform.position;
            var names = new System.Collections.Generic.SortedSet<string>();
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                if ((c.bounds.center - eye).magnitude * Coords.S < blocks) names.Add($"{c.name}[{LayerMask.LayerToName(c.gameObject.layer)}{(c.isTrigger ? ",trigger" : "")}]");
            }
            Plugin.L.LogInfo("dev: near: " + string.Join(", ", names));
        }

        private static void Info()
        {
            var L = Plugin.L;
            if (Core.PlayerRoot != null) L.LogInfo($"dev: player {Core.PlayerRoot.position} rot {Core.PlayerRoot.eulerAngles} cam {Core.Cam.transform.position} fwd {Core.Cam.transform.forward}");
            L.LogInfo($"dev: frame {Time.frameCount} fps {1f / Mathf.Max(1e-4f, Time.smoothDeltaTime):F0} timeScale {Time.timeScale} focused {Application.isFocused} runInBackground {Application.runInBackground}");
            L.LogInfo($"dev: scene {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} active={Core.Active} link={Link.Connected} {Enemies.Summary()} blocks={Blocks.Count} ghost={Scanner.Status}");
            var s = Native.GetStats();
            L.LogInfo($"dev: native shm={s.shmOpen} pub={s.published} up={s.uploaded} comp={s.composited} mc={s.width}x{s.height} tgt={s.targetW}x{s.targetH} dfmt={s.depthFormat} rtv={s.rtvFormat} err={s.lastError} mcpose=({s.mcX:F2},{s.mcY:F2},{s.mcZ:F2} {s.mcYaw:F1}/{s.mcPitch:F1}) fov={s.mcFov}");
        }

        /// <summary>call Type Method [args]: invokes a method on the first scene object of that game type.</summary>
        private static void Call(string[] a)
        {
            var asm = typeof(PlayerStatus).Assembly;
            var t = asm.GetType(a[1]);
            if (t == null) { Plugin.L.LogInfo("dev: no type " + a[1]); return; }
            var found = UnityEngine.Object.FindObjectOfType(Il2CppType.From(t));
            if (found == null) { Plugin.L.LogInfo("dev: no object of " + a[1]); return; }
            var obj = Activator.CreateInstance(t, found.Pointer);
            var m = t.GetMethods().FirstOrDefault(x => x.Name == a[2] && x.GetParameters().Length == a.Length - 3);
            if (m == null) { Plugin.L.LogInfo("dev: no method " + a[2]); return; }
            var ps = m.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) args[i] = Convert.ChangeType(a[3 + i], ps[i].ParameterType, Inv);
            var r = m.Invoke(obj, args);
            Plugin.L.LogInfo($"dev: {a[1]}.{a[2]} -> {r}");
        }
    }
}
