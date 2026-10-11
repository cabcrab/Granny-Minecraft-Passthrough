using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using UnityEngine;

namespace GrannyCraft
{
    /// <summary>The per-frame driver: finds the player, drives Minecraft's camera, forwards input, routes messages.</summary>
    public class Core : MonoBehaviour
    {
        public Core(IntPtr p) : base(p) { }

        public static bool Enabled = true;
        /// <summary>Passthrough on, a player camera exists and Minecraft is connected.</summary>
        public static bool Active;
        public static int DebugView;
        /// <summary>0 first person, 1 third person back, 2 third person front.</summary>
        public static int View;
        public static PlayerStatus PS;
        public static Camera Cam;
        public static Transform PlayerRoot;
        public static CharacterController CC;
        private static bool _showStatus;
        private static float _nextFind;
        private static float _nextStats;
        private static int _sentW, _sentH;
        private static uint _mcPid;
        private static IntPtr _mcWnd;
        private static float _nextWnd;
        private static string _status = "";
        private static float _nextLockLog;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static long _frame;
        private static bool _setupSent;
        /// <summary>Frames until a simulated click is released.</summary>
        public static int[] PendingRelease = new int[3];

        private void Start()
        {
            Enabled = Plugin.StartEnabled.Value;
            // keep running when another window has the focus (Minecraft's, or anything else): Minecraft never pauses either
            Application.runInBackground = true;
        }

        private void Update()
        {
            try
            {
                FakeInput.Tick();
                DevConsole.Poll();
                DevConsole.AutoFight();
                for (int b = 0; b < 3; b++)
                    if (PendingRelease[b] > 0 && --PendingRelease[b] == 0) { DevConsole.FakeDown[b] = false; DevConsole.FakeReleased[b] = true; }
                HandleMessages();
                HandleKeys();
                if (Time.unscaledTime >= _nextFind) FindPlayer();
                if (PS != null)
                {
                    Scanner.Step();
                    if (!DevConsole.Has("noenemies")) Enemies.Update();
                    Interact.Update();
                    Movement.Update();
                    GrannyHeld.Update();
                    Carve.Update();
                    Blocks.Update();
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogError("Update: " + e);
            }
        }

        private void LateUpdate()
        {
            try
            {
                Active = Enabled && Link.Connected && Cam != null && PS != null;
                if (Cam == null) return;
                ComputeView();
                if (Active) SendCamera();
                Menu.LateUpdate();
                if (Time.unscaledTime >= _nextStats)
                {
                    _nextStats = Time.unscaledTime + 1f;
                    var log = Native.TakeLog();
                    if (log.Length > 0) Plugin.L.LogInfo("native: " + log.Trim().Replace("\n", " | "));
                    var s = Native.GetStats();
                    int frames = CamHook.LockedFrames + CamHook.FreeFrames;
                    int lockPct = frames > 0 ? CamHook.LockedFrames * 100 / frames : 0;
                    _status = $"GrannyCraft {(Enabled ? "ON" : "OFF")} link={(Link.Connected ? "ok" : "--")} shm={s.shmOpen} mc={s.width}x{s.height} pub={s.published} up={s.uploaded} comp={s.composited} tgt={s.targetW}x{s.targetH} dfmt={s.depthFormat} rtv={s.rtvFormat} err={s.lastError} view={View} ghost={Scanner.Status} depth={(Plugin.SceneDepth.Value ? "scene" : "texture")} flip={(Plugin.FlipGrannyDepth.Value ? "ON" : "off")} locked={lockPct}% debug={DebugView}";
                    if (frames > 0 && Time.unscaledTime >= _nextLockLog)
                    {
                        _nextLockLog = Time.unscaledTime + 10f;
                        Plugin.L.LogInfo($"frames from Minecraft's camera: {lockPct}% ({frames} frames), depth={(Plugin.SceneDepth.Value ? "scene" : "texture")}");
                    }
                    CamHook.LockedFrames = CamHook.FreeFrames = 0;
                    KeepWindows();
                }
                DevConsole.EndFrame();
            }
            catch (Exception e)
            {
                Plugin.L.LogError("LateUpdate: " + e);
            }
        }

        private void OnGUI()
        {
            Nether.OnGUI();
            if (!_showStatus) return;
            GUI.Label(new Rect(10, 10, Screen.width - 20, 60), _status);
        }

        // ---------------------------------------------------------------------------------------------------------
        private static void FindPlayer()
        {
            _nextFind = Time.unscaledTime + 1f;
            if (PS != null && PS.gameObject != null && PS.gameObject.activeInHierarchy)
            {
                PickCamera();
                return;
            }
            var ps = UnityEngine.Object.FindObjectOfType<PlayerStatus>();
            if (ps == null)
            {
                if (PS != null)
                {
                    Plugin.L.LogInfo("player gone");
                    PS = null; Cam = null; PlayerRoot = null; CC = null;
                    Hooked.Clear();
                    Scanner.Reset(); Enemies.Reset(); Blocks.Reset(); Movement.Reset(); GrannyHeld.Reset(); Carve.Reset(); Menu.Reset(); Nether.Reset();
                }
                return;
            }

            PS = ps;
            Cam = ps.PlayerCam;
            MainCam = ps.PlayerCam;
            PlayerRoot = ps.Player != null ? ps.Player.transform : ps.transform;
            CC = PlayerRoot.GetComponent<CharacterController>();
            if (CC == null) CC = UnityEngine.Object.FindObjectOfType<CharacterController>();
            PickCamera();
            Plugin.L.LogInfo($"player found: root={PlayerRoot.name} pos={PlayerRoot.position} cam={(Cam != null ? Cam.name : "null")} cc={(CC != null ? CC.height.ToString(Inv) : "none")} layer={PlayerRoot.gameObject.layer}");
            if (!DevConsole.Has("nodiag")) Diagnostics.DumpOnce();
            AlignOffsets();
            Blocks.ChooseLayer();
            CamHook.Followers.Clear();
            if (!DevConsole.Has("noscan")) Scanner.Begin();
            GrannyHeld.Reset();
            Link.Send("{\"t\":\"newgame\"}"); // Minecraft's side of the house starts clean too (built blocks gone, fresh kit)
            Carve.Reset(); // a new game loads the house whole again
            Carve.SendState();
            if (!DevConsole.Has("noenemies")) Enemies.Find();
            _setupSent = false;
        }

        public static Camera MainCam;
        private static readonly System.Collections.Generic.HashSet<int> Hooked = new System.Collections.Generic.HashSet<int>();

        /// <summary>The camera drawing the game right now (the bed camera at wake-up, then the player's): it gets the hook.</summary>
        private static void PickCamera()
        {
            Camera best = null;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>())
            {
                if (!c.enabled || c.targetTexture != null) continue;
                if (best == null || c.depth > best.depth) best = c;
            }
            if (best == null) return;
            if (best != Cam) Plugin.L.LogInfo("rendering camera: " + best.name);
            Cam = best;
            // GetComponent<T> doesn't see injected components: remember which cameras are hooked
            if (!DevConsole.Has("nocamhook") && Hooked.Add(Cam.GetInstanceID())) Cam.gameObject.AddComponent<CamHook>().Init(Cam);
        }

        /// <summary>Line Granny's ground floor up with a block boundary (blocks then sit on her floor).</summary>
        private static void AlignOffsets()
        {
            double floor = PlayerRoot.position.y;
            Vector3 feet = CC != null ? new Vector3(CC.bounds.center.x, CC.bounds.min.y, CC.bounds.center.z) : PlayerRoot.position;
            if (Physics.Raycast(feet + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 5f, LayerMask.GetMask("HouseFloor", "Default"), QueryTriggerInteraction.Ignore))
                floor = hit.point.y;
            double baseS = Math.Clamp(Plugin.BlocksPerUnit.Value, 0.1f, 3f);

            // the house's walkable floors: rays straight down over a grid across the house, every upward surface hit
            var floors = new System.Collections.Generic.List<(double y, double w)> { (floor, 60.0) };
            int floorMask = LayerMask.GetMask("HouseFloor");
            Bounds house = new Bounds(PlayerRoot.position, Vector3.zero);
            bool any = false;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Collider>())
            {
                if (c.isTrigger || ((1 << c.gameObject.layer) & floorMask) == 0) continue;
                if (!any) { house = c.bounds; any = true; } else house.Encapsulate(c.bounds);
            }
            for (float x = house.min.x; x <= house.max.x; x += 1.5f)
                for (float z = house.min.z; z <= house.max.z; z += 1.5f)
                    foreach (var h in Physics.RaycastAll(new Vector3(x, house.max.y + 1f, z), Vector3.down, house.size.y + 2f, floorMask, QueryTriggerInteraction.Ignore))
                        if (h.normal.y > 0.95f) floors.Add((h.point.y, 1.0));
            floors = floors.GroupBy(f => Math.Round(f.y * 20.0) / 20.0).Select(g => (g.Key, g.Sum(f => f.w))).ToList();

            // scale (within 8% of the setting) and offset that put as much floor as possible on block boundaries
            double bestS = baseS, bestOff = 0, bestCost = double.MaxValue;
            for (double sc = baseS * 0.92; sc <= baseS * 1.08; sc += baseS * 0.002)
            {
                for (double off = 0; off < 1.0; off += 0.01)
                {
                    double cost = 0;
                    foreach (var f in floors)
                    {
                        double v = f.y * sc + off;
                        double d = v - Math.Round(v);
                        cost += f.w * d * d;
                    }
                    // stay close to the chosen size
                    cost *= 1.0 + 4.0 * Math.Abs(sc / baseS - 1.0);
                    if (cost < bestCost) { bestCost = cost; bestS = sc; bestOff = off; }
                }
            }
            Coords.S = bestS;
            // the start floor lands on Minecraft y = 64 (whole block)
            Coords.OY = bestOff + Math.Round(64.0 - (floor * bestS + bestOff));
            var sb = new StringBuilder();
            foreach (var f in floors.OrderBy(f => f.y).Select(f => f.y).Distinct().Take(12))
                sb.Append($" {f:F2}->{f * Coords.S + Coords.OY:F2}");
            Plugin.L.LogInfo($"floor at y={floor:F3}; {floors.Count} floor surfaces; {Coords.S:F4} blocks per unit (setting {baseS:F3}), y offset {Coords.OY:F3}; floors in blocks:{sb}");
        }

        // ---------------------------------------------------------------------------------------------------------
        private static void HandleKeys()
        {
            if (Input.GetKeyDown(Plugin.ToggleKey.Value))
            {
                Enabled = !Enabled;
                Plugin.L.LogInfo("passthrough " + (Enabled ? "on" : "off"));
                Link.Send("{\"t\":\"passthrough\",\"on\":" + (Enabled ? "true" : "false") + "}");
            }
            if (Input.GetKeyDown(Plugin.HudKey.Value)) _showStatus = !_showStatus;
            if (Input.GetKeyDown(Plugin.BreakHouseKey.Value))
            {
                Plugin.BreakableHouse.Value = !Plugin.BreakableHouse.Value;
                Carve.SendState();
                Plugin.L.LogInfo("breakable house (experimental): " + (Plugin.BreakableHouse.Value ? "on" : "off"));
            }
            if (Input.GetKeyDown(Plugin.DebugKey.Value)) DebugView = (DebugView + 1) % 4;
            if (Input.GetKeyDown(Plugin.DepthSourceKey.Value))
            {
                Plugin.FlipGrannyDepth.Value = !Plugin.FlipGrannyDepth.Value;
                Plugin.L.LogInfo("Granny's depth read " + (Plugin.FlipGrannyDepth.Value ? "upside down" : "as stored"));
            }
            if (!Enabled || !Link.Connected || PS == null)
            {
                if (Menu.Open)
                {
                    Link.Send("{\"t\":\"menu\",\"on\":false}");
                    Menu.Reset();
                }
                if (DevConsole.FakePressed[0] || DevConsole.FakePressed[1]) Plugin.L.LogInfo($"input dropped: enabled={Enabled} link={Link.Connected} ps={PS != null}");
                return;
            }
            if (Input.GetKeyDown(Plugin.ViewKey.Value)) View = (View + 1) % 3;
            // the creative inventory (R) takes the mouse: nothing goes to Minecraft's hand meanwhile
            Menu.Update();
            if (Menu.Open) return;

            if (Plugin.ForwardMouse.Value)
            {
                Key(0, "attack");
                Key(1, "use");
                Key(2, "pick");
                float wheel = Input.mouseScrollDelta.y + DevConsole.FakeWheel;
                if (wheel > 0.01f) Link.Send("{\"t\":\"scroll\",\"d\":1}");
                else if (wheel < -0.01f) Link.Send("{\"t\":\"scroll\",\"d\":-1}");
            }
            for (int i = 0; i < 9; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i)) Link.Send("{\"t\":\"slot\",\"n\":" + i + "}");
            }
        }

        private static bool _useToMinecraft;

        private static void Key(int button, string name)
        {
            if (button == 1)
            {
                // right click: Granny's interaction when she shows a prompt (door, item), Minecraft's "use" otherwise
                if (Input.GetMouseButtonDown(1) || DevConsole.FakePressed[1])
                {
                    _useToMinecraft = !Interact.GrannyHasTarget();
                    Plugin.L.LogInfo("right click -> " + (_useToMinecraft ? "Minecraft" : "Granny (" + Interact.Debug() + ")"));
                    if (_useToMinecraft) Link.Send("{\"t\":\"key\",\"k\":\"use\",\"down\":true}");
                    else if (PS.PickRay != null) FakeInput.Hold(PS.PickRay.MainInteract, 0.1f, grannyOnly: true); // the door opens as with E
                }
                if ((Input.GetMouseButtonUp(1) || DevConsole.FakeReleased[1]) && _useToMinecraft)
                {
                    _useToMinecraft = false;
                    Link.Send("{\"t\":\"key\",\"k\":\"use\",\"down\":false}");
                }
                return;
            }
            if (Input.GetMouseButtonDown(button) || DevConsole.FakePressed[button]) Plugin.L.LogInfo("forward " + name);
            if (Input.GetMouseButtonDown(button) || DevConsole.FakePressed[button]) Link.Send("{\"t\":\"key\",\"k\":\"" + name + "\",\"down\":true}");
            if (Input.GetMouseButtonUp(button) || DevConsole.FakeReleased[button]) Link.Send("{\"t\":\"key\",\"k\":\"" + name + "\",\"down\":false}");
        }

        // ---------------------------------------------------------------------------------------------------------
        /// <summary>Where the camera renders from this frame (third person: behind or in front of the player).</summary>
        private static void ComputeView()
        {
            CamHook.ThirdPerson = Active && View != 0;
            if (!CamHook.ThirdPerson) return;
            Transform ct = Cam.transform;
            Vector3 head = ct.position;
            Vector3 dir = View == 1 ? -ct.forward : ct.forward;
            float dist = Plugin.ThirdPersonDistance.Value * Coords.U;
            // Minecraft pulls the camera in against walls; same here against Granny's geometry
            if (Physics.SphereCast(head, 0.2f * Coords.U, dir, out RaycastHit hit, dist, ~(1 << Blocks.Layer) & Scanner.Mask, QueryTriggerInteraction.Ignore))
                dist = Mathf.Max(0.3f, hit.distance - 0.1f);
            CamHook.ViewPos = head + dir * dist;
            CamHook.ViewRot = View == 1 ? ct.rotation : Quaternion.LookRotation(-ct.forward, ct.up);
        }

        private static void SendCamera()
        {
            Transform ct = Cam.transform;
            Vector3 pos = CamHook.ThirdPerson ? CamHook.ViewPos : ct.position;
            Quaternion rot = CamHook.ThirdPerson ? CamHook.ViewRot : ct.rotation;
            var mc = Coords.ToMc(pos);
            Coords.YawPitch(rot * Vector3.forward, out float yaw, out float pitch);
            float roll = 0f;
            // body: the player's feet and where the player faces
            Vector3 feet = CC != null ? new Vector3(CC.bounds.center.x, CC.bounds.min.y, CC.bounds.center.z) : PlayerRoot.position;
            var f = Coords.ToMc(feet);
            Coords.YawPitch(ct.forward, out float bodyYaw, out float headPitch);
            int w = Screen.width, h = Screen.height;
            if (w != _sentW || h != _sentH)
            {
                _sentW = w; _sentH = h;
                Link.Send($"{{\"t\":\"view\",\"w\":{w},\"h\":{h}}}");
            }
            var ch = UnityEngine.Object.FindObjectOfType<CrouchHolder>();
            // Granny's crouch (C) or Minecraft's sneak (Shift): Steve crouches either way
            bool crouch = ch != null && (ch.isCrouching || ch.IsCrouched) || Movement.Sneaking;
            var sb = new StringBuilder(256);
            sb.Append("{\"t\":\"cam\",\"f\":").Append(++_frame)
              .Append(",\"p\":[").Append(D(mc.x)).Append(',').Append(D(mc.y)).Append(',').Append(D(mc.z))
              .Append("],\"r\":[").Append(F(yaw)).Append(',').Append(F(pitch)).Append(',').Append(F(roll))
              .Append("],\"fov\":").Append(F(Cam.fieldOfView))
              .Append(",\"fp\":").Append(View == 0 ? "true" : "false")
              .Append(",\"pl\":[").Append(D(f.x)).Append(',').Append(D(f.y)).Append(',').Append(D(f.z))
              .Append("],\"h\":").Append(F(bodyYaw))
              .Append(",\"hp\":").Append(F(headPitch))
              .Append(",\"crouch\":").Append(crouch ? "true" : "false")
              .Append(",\"sprint\":").Append(Movement.Sprinting ? "true" : "false")
              .Append('}');
            Link.SendCam(sb.ToString());
        }

        internal static string D(double v) => v.ToString("0.####", Inv);
        internal static string F(float v) => v.ToString("0.###", Inv);

        // ---------------------------------------------------------------------------------------------------------
        private static void HandleMessages()
        {
            int n = 0;
            while (n++ < 200 && Link.Incoming.TryDequeue(out var msg))
            {
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    var m = doc.RootElement;
                    string t = m.GetProperty("t").GetString();
                    switch (t)
                    {
                        case "_connected":
                            Plugin.L.LogInfo("Minecraft connected");
                            _sentW = _sentH = 0;
                            Scanner.Resend();
                            Carve.SendState();
                            Nether.SendConfig();
                            break;
                        case "_disconnected":
                            Plugin.L.LogInfo("Minecraft disconnected");
                            Menu.Reset();
                            Nether.Reset();
                            Enemies.ClearMobs();
                            break;
                        case "hello":
                            _mcPid = m.GetProperty("pid").GetUInt32();
                            _mcWnd = IntPtr.Zero;
                            Plugin.L.LogInfo("Minecraft pid " + _mcPid);
                            break;
                        case "blocks":
                            Blocks.OnBlocks(m);
                            break;
                        case "hotbar":
                            GrannyHeld.OnHotbar(m);
                            break;
                        case "melee":
                            Enemies.OnMelee(m);
                            break;
                        case "carve":
                            Carve.OnCarve(m);
                            break;
                        case "proj":
                            Enemies.OnProjectiles(m);
                            break;
                        case "mobs":
                            Enemies.OnMobs(m);
                            break;
                        case "hurt":
                            Enemies.OnHurt(m);
                            break;
                        case "explosion":
                            Effects.Explosion(m);
                            break;
                        case "menu":
                            Menu.OnMenu(m);
                            break;
                        case "nether":
                            Nether.OnNether(m);
                            break;
                        case "log":
                            Plugin.L.LogInfo("mc: " + m.GetProperty("m").GetString());
                            break;
                    }
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning("bad message " + (msg.Length > 120 ? msg.Substring(0, 120) : msg) + ": " + e.Message);
                }
            }
        }

        /// <summary>Minecraft's window stays behind Granny's; Granny keeps the focus.</summary>
        private static void KeepWindows()
        {
            if (_mcPid == 0 || !Link.Connected) return;
            if (_mcWnd == IntPtr.Zero || Time.unscaledTime >= _nextWnd)
            {
                _nextWnd = Time.unscaledTime + 5f;
                _mcWnd = Win.FindMainWindow(_mcPid);
            }
            if (_mcWnd != IntPtr.Zero)
                Win.SetWindowPos(_mcWnd, Win.HWND_BOTTOM, 0, 0, 0, 0, Win.SWP_NOMOVE | Win.SWP_NOSIZE | Win.SWP_NOACTIVATE);
        }
    }
}
