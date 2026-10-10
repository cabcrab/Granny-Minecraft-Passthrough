using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityEngine;

namespace GrannyCraft
{
    /// <summary>
    /// Granny's items in the Minecraft hotbar. Granny's player holds one item at a time (one of PickRay's H_* hand
    /// models is active). When she picks one up it goes into the selected Minecraft slot (the Minecraft item there is
    /// dropped on the floor); when it leaves her hand (dropped, used) the slot empties. While another slot is selected,
    /// Granny's item stays hers but is put away: not drawn, and her fire button doesn't shoot it. Q drops the selected
    /// item: Granny's item with Granny's own drop, a Minecraft item with Minecraft's.
    /// </summary>
    internal static class GrannyHeld
    {
        private static string _held;       // name of the hand model Granny's player holds now (null: nothing)
        private static int _sel = 0, _token = -1;
        private static GameObject _heldGo;
        private static readonly List<Renderer> Hidden = new List<Renderer>();
        private static KeyCode _shootOrig = KeyCode.None;
        private static bool _shootBlocked;

        /// <summary>Granny's item is the selected hotbar slot (or there is no Minecraft to choose: it is just held).</summary>
        public static bool Selected => !Core.Active || _token < 0 || _sel == _token;

        /// <summary>Hand models already active when the game starts (Granny's player starts empty-handed): not items.</summary>
        private static readonly HashSet<int> Baseline = new HashSet<int>();
        private static bool _baselineTaken;
        private static float _upSince = -1f;

        public static void Reset()
        {
            Baseline.Clear(); _baselineTaken = false; _upSince = -1f;
            _held = null; _heldGo = null; _token = -1; Hidden.Clear(); _shootBlocked = false;
        }

        /// <summary>{"t":"hotbar","sel":n,"token":m} from Minecraft.</summary>
        public static void OnHotbar(JsonElement m)
        {
            _sel = m.GetProperty("sel").GetInt32();
            _token = m.GetProperty("token").GetInt32();
        }

        /// <summary>
        /// What Granny's player holds: Granny shows the held item as a model attached to the player/camera. Everything
        /// already there when the game starts (empty-handed, in bed) is the player itself; whatever shows up there later
        /// is the item in hand. The held object is the topmost new object under the player or camera.
        /// </summary>
        private static readonly List<Transform> Roots = new List<Transform>();
        private static float _nextRoots;

        /// <summary>Everything that moves with the player: the player, every camera, the camera pivots, Granny's picking ray, items following the hand.</summary>
        private static List<Transform> GetRoots(PickRay pr)
        {
            // a root Granny destroyed meanwhile (a camera swapped at a cutscene): look again
            if (Roots.Exists(r => r == null)) _nextRoots = 0f;
            if (Time.unscaledTime < _nextRoots && Roots.Count > 0) return Roots;
            _nextRoots = Time.unscaledTime + 2f;
            var all = new List<Transform>();
            void Add(Component c) { if (c != null) all.Add(c.transform); }
            void AddGo(GameObject g) { if (g != null) all.Add(g.transform); }
            Add(Core.PlayerRoot);
            Add(Core.MainCam);
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>()) if (c.targetTexture == null) Add(c);
            AddGo(Core.PS.Camera);
            AddGo(Core.PS.CameraPivot);
            Add(pr);
            var fps = UnityEngine.Object.FindObjectOfType<MobileFPS>();
            if (fps != null) { AddGo(fps.PlayerStuff); Add(fps.CamPivot); Add(fps.playerCamera); }
            foreach (var f in UnityEngine.Object.FindObjectsOfType<FollowHand>()) Add(f);
            // only the outermost ones (no root inside another)
            Roots.Clear();
            foreach (var t in all)
            {
                if (t == null || Roots.Contains(t)) continue;
                bool inside = false;
                foreach (var o in all) if (o != null && o != t && t.IsChildOf(o)) { inside = true; break; }
                if (!inside) Roots.Add(t);
            }
            return Roots;
        }

        private static GameObject FindHeld(PickRay pr)
        {
            var roots = GetRoots(pr);
            if (!_baselineTaken)
            {
                // once the player is up (their own camera draws, a second after waking): empty-handed
                bool up = Core.MainCam != null && Core.Cam == Core.MainCam && Core.MainCam.gameObject.activeInHierarchy;
                if (!up) { _upSince = -1f; return null; }
                if (_upSince < 0f) _upSince = Time.time;
                if (Time.time < _upSince + 1f) return null;
                _baselineTaken = true;
                foreach (var root in roots)
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        if (t.gameObject.activeInHierarchy) Baseline.Add(t.gameObject.GetInstanceID());
                Plugin.L.LogInfo($"held items: {Baseline.Count} objects on the player at the start (not items); watching {string.Join(", ", roots.Select(r => r.name))}");
                return null;
            }
            foreach (var root in roots)
            {
                foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                {
                    if (r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) continue; // particles, lines: effects, not items
                    if (Baseline.Contains(r.gameObject.GetInstanceID()) && !NewAbove(r.transform, root)) continue;
                    // an item in hand is drawn right by the camera
                    if (Core.MainCam != null && (r.bounds.center - Core.MainCam.transform.position).magnitude > 2.5f) continue;
                    // the topmost object of this item that wasn't there at the start
                    Transform top = r.transform;
                    for (var t = r.transform.parent; t != null && t != root; t = t.parent)
                        if (!Baseline.Contains(t.gameObject.GetInstanceID())) top = t;
                    return top.gameObject;
                }
            }
            return null;
        }

        /// <summary>An object of the start that hangs under a new one (an item moved into the hand).</summary>
        private static bool NewAbove(Transform t, Transform root)
        {
            for (var p = t.parent; p != null && p != root; p = p.parent)
                if (!Baseline.Contains(p.gameObject.GetInstanceID())) return true;
            return false;
        }

        public static void Update()
        {
            var pr = Core.PS?.PickRay;
            if (pr == null) return;
            bool on = Core.Active;

            // what Granny's hand holds (the hidden renderers stay hidden models, the object itself stays active)
            var go = FindHeld(pr);
            string name = go != null ? Nice(go.name) : null;
            if (go != null && name != _held) Plugin.L.LogInfo($"held object: {Path(go.transform)} (ItemDrop={(pr.ItemDrop != null ? pr.ItemDrop.name : "-")})");
            if (name != _held)
            {
                Show();
                if (on)
                {
                    if (name != null) Link.Send("{\"t\":\"gitem\",\"name\":" + JsonSerializer.Serialize(name) + "}");
                    else Link.Send("{\"t\":\"gitem_clear\"}");
                }
                Plugin.L.LogInfo($"Granny's hand: {_held ?? "empty"} -> {name ?? "empty"}");
                _held = name;
                _heldGo = go;
                // what carries the item in hand (PlayerStuff): it follows the camera without being attached to it, so it
                // moves with the camera when the camera is placed for Minecraft's frame (else the item shakes)
                CamHook.Followers.Clear();
                if (go != null)
                {
                    var carrier = go.transform.root;
                    if (Core.MainCam == null || !Core.MainCam.transform.IsChildOf(carrier))
                    {
                        CamHook.Followers.Add(carrier);
                        Plugin.L.LogInfo($"held item carrier: {carrier.name} (moves with the camera while drawing)");
                    }
                    else
                    {
                        // the camera itself is inside the carrier: move the item's own top object under it
                        Transform t = go.transform;
                        while (t.parent != null && !Core.MainCam.transform.IsChildOf(t.parent)) t = t.parent;
                        if (!t.IsChildOf(Core.MainCam.transform)) CamHook.Followers.Add(t);
                        Plugin.L.LogInfo($"held item carrier: {t.name} (camera is inside {carrier.name})");
                    }
                }
                if (name != null) _token = _sel; // until Minecraft confirms
            }

            // put away while another slot is selected
            bool away = on && _held != null && !Selected;
            if (away) Hide(); else Show();
            BlockShoot(pr, away || Menu.Open); // clicks in the creative inventory don't fire Granny's gun

            // Q: drop what is selected
            if (on && !Menu.Open && Input.GetKeyDown(KeyCode.Q))
            {
                if (_held != null && Selected) FakeInput.Hold(Interact.GrannyDropKey, 0.15f, grannyOnly: true);
                else Link.Send("{\"t\":\"key\",\"k\":\"drop\",\"down\":true}");
            }
            if (on && Input.GetKeyUp(KeyCode.Q)) Link.Send("{\"t\":\"key\",\"k\":\"drop\",\"down\":false}");
        }

        private static string Path(Transform t)
        {
            string p = t.name;
            for (var x = t.parent; x != null; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        /// <summary>Granny's hand model names ("H_HA", "Hammer_Hand"...) as readable item names.</summary>
        private static string Nice(string n)
        {
            n = System.Text.RegularExpressions.Regex.Replace(n, @"\(\d+\)|\(Clone\)", "");
            foreach (var pre in new[] { "Main_", "H_", "Hand_", "Held_" }) if (n.StartsWith(pre)) n = n.Substring(pre.Length);
            n = System.Text.RegularExpressions.Regex.Replace(n.Replace("_", " "), "([a-z])([A-Z])", "$1 $2").Trim();
            // the hand models are "<Item>Hand", "<Item>HandP1"...: just the item
            n = System.Text.RegularExpressions.Regex.Replace(n, @"\s*Hand(\s*P?\d+)?$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            if (n.Equals("Vas", System.StringComparison.OrdinalIgnoreCase)) n = "Vase";
            return n.Length == 0 ? "Item" : char.ToUpperInvariant(n[0]) + n.Substring(1);
        }

        private static void Hide()
        {
            if (_heldGo == null || Hidden.Count > 0) return;
            foreach (var r in _heldGo.GetComponentsInChildren<Renderer>())
            {
                if (r.enabled) { r.enabled = false; Hidden.Add(r); }
            }
        }

        private static void Show()
        {
            foreach (var r in Hidden) if (r != null) r.enabled = true;
            Hidden.Clear();
        }

        /// <summary>A gun put away doesn't fire: Granny's shoot key goes nowhere until it is selected again.</summary>
        private static void BlockShoot(PickRay pr, bool block)
        {
            if (block == _shootBlocked) return;
            if (_shootOrig == KeyCode.None) _shootOrig = pr.ShootKey == KeyCode.F14 ? KeyCode.Mouse0 : pr.ShootKey;
            KeyCode k = block ? KeyCode.F14 : _shootOrig;
            pr.ShootKey = k;
            foreach (var b in Object.FindObjectsOfType<KeyBindManage>()) b.Shooting = k;
            _shootBlocked = block;
        }
    }
}
