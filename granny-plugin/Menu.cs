using System.Globalization;
using System.Text.Json;
using HarmonyLib;
using UnityEngine;

namespace GrannyCraft
{
    /// <summary>
    /// Minecraft's creative inventory over Granny (R by default): every item, the spawn eggs included. Minecraft draws
    /// the screen into its overlay layer (composited over Granny like the HUD); its window is behind Granny's and never
    /// sees the mouse, so while the screen is open Granny's pointer is freed and sent over (position, buttons, wheel).
    /// Granny's camera, walking and every key are held back meanwhile; while the search box has the keyboard, what is
    /// typed goes to it. R or Escape closes it (R types an "r" in the search box). Open/closed is whatever Minecraft
    /// reports ({"t":"menu","on":bool,"typing":bool}), so closing it inside Minecraft frees Granny too.
    /// </summary>
    internal static class Menu
    {
        /// <summary>The creative inventory is up (as Minecraft reports it).</summary>
        public static bool Open;
        /// <summary>Minecraft's search box has the keyboard.</summary>
        public static bool Typing;
        /// <summary>The key that opens it (taken from Granny while the passthrough is on).</summary>
        public static KeyCode Key = KeyCode.R;
        private static CursorLockMode _savedLock;
        private static bool _savedVisible;
        private static MobileFPS _fps;
        private static float _savedTurn = -1f;
        private static float _lastX = -1f, _lastY = -1f;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Real presses Granny doesn't get: the menu key always, every key and mouse button while it is open.</summary>
        public static bool HidesFromGranny(KeyCode k) => Open || k == Key && Link.Connected;

        /// <summary>Once per frame from Core (passthrough on, Minecraft connected, a player).</summary>
        public static void Update()
        {
            // Granny's own pause menu has the pointer then
            if (FakeInput.KeyDown(Key) && Time.timeScale > 0f && !(Open && Typing)) Link.Send("{\"t\":\"menu\",\"on\":" + (Open ? "false" : "true") + "}");
            if (!Open) return;
            if (FakeInput.KeyDown(KeyCode.Escape)) Link.Send("{\"t\":\"menu\",\"on\":false}");
            if (Typing) ForwardTyping();

            // where the pointer is, as a fraction of Granny's picture (Minecraft's window has the same size), from the top
            Vector3 mp = Input.mousePosition;
            float x = Mathf.Clamp01(mp.x / Mathf.Max(1, Screen.width)), y = Mathf.Clamp01(1f - mp.y / Mathf.Max(1, Screen.height));
            if (Mathf.Abs(x - _lastX) > 1e-5f || Mathf.Abs(y - _lastY) > 1e-5f)
            {
                _lastX = x; _lastY = y;
                Link.Send("{\"t\":\"mouse\",\"x\":" + x.ToString("0.#####", Inv) + ",\"y\":" + y.ToString("0.#####", Inv) + "}");
            }
            for (int b = 0; b < 3; b++)
            {
                if (Input.GetMouseButtonDown(b)) Link.Send("{\"t\":\"mbtn\",\"b\":" + b + ",\"down\":true}");
                if (Input.GetMouseButtonUp(b)) Link.Send("{\"t\":\"mbtn\",\"b\":" + b + ",\"down\":false}");
            }
            float wheel = Input.mouseScrollDelta.y;
            if (wheel > 0.01f) Link.Send("{\"t\":\"mwheel\",\"d\":1}");
            else if (wheel < -0.01f) Link.Send("{\"t\":\"mwheel\",\"d\":-1}");
        }

        private static readonly (KeyCode key, string name)[] EditKeys =
        {
            (KeyCode.Delete, "delete"), (KeyCode.LeftArrow, "left"), (KeyCode.RightArrow, "right"),
            (KeyCode.Home, "home"), (KeyCode.End, "end"),
        };

        /// <summary>What was typed this frame (Unity's text input: characters, backspace, enter) and the editing keys.</summary>
        private static void ForwardTyping()
        {
            var text = new System.Text.StringBuilder();
            foreach (char c in Input.inputString)
            {
                if (c == '\b' || c == '\n' || c == '\r')
                {
                    if (text.Length > 0) { SendText(text.ToString()); text.Clear(); }
                    Link.Send("{\"t\":\"mkey\",\"k\":\"" + (c == '\b' ? "backspace" : "enter") + "\"}");
                }
                else if (c >= ' ') text.Append(c);
            }
            if (text.Length > 0) SendText(text.ToString());
            foreach (var (key, name) in EditKeys)
                if (FakeInput.KeyDown(key)) Link.Send("{\"t\":\"mkey\",\"k\":\"" + name + "\"}");
        }

        private static void SendText(string s) => Link.Send("{\"t\":\"mtext\",\"s\":" + JsonSerializer.Serialize(s) + "}");

        /// <summary>After Granny's scripts: the pointer stays free and visible while the screen is open.</summary>
        public static void LateUpdate()
        {
            if (!Open) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (_fps != null) _fps.cameraRotationSpeed = 0f;
        }

        /// <summary>{"t":"menu","on":bool} from Minecraft.</summary>
        public static void OnMenu(JsonElement m)
        {
            Set(m.GetProperty("on").GetBoolean());
            Typing = Open && m.TryGetProperty("typing", out var t) && t.GetBoolean();
        }

        /// <summary>Minecraft gone, passthrough off, a new game: Granny gets everything back.</summary>
        public static void Reset() => Set(false);

        private static void Set(bool on)
        {
            if (!on) Typing = false;
            if (on == Open) return;
            if (on)
            {
                _savedLock = Cursor.lockState;
                _savedVisible = Cursor.visible;
                _fps = Object.FindObjectOfType<MobileFPS>();
                if (_fps != null) _savedTurn = _fps.cameraRotationSpeed;
                _lastX = _lastY = -1f; // send the pointer straight away
                Open = true;
            }
            else
            {
                Open = false;
                if (_fps != null && _savedTurn >= 0f) _fps.cameraRotationSpeed = _savedTurn;
                _fps = null;
                _savedTurn = -1f;
                Cursor.lockState = _savedLock;
                Cursor.visible = _savedVisible;
            }
            Plugin.L.LogInfo("creative inventory " + (on ? "open" : "closed"));
        }
    }

    /// <summary>While the creative inventory is open, Granny's scripts can't lock or hide the pointer again.</summary>
    [HarmonyPatch]
    internal static class CursorPatches
    {
        [HarmonyPatch(typeof(Cursor), nameof(Cursor.lockState), MethodType.Setter)]
        [HarmonyPrefix]
        private static void Lock(ref CursorLockMode value)
        {
            if (Menu.Open) value = CursorLockMode.None;
        }

        [HarmonyPatch(typeof(Cursor), nameof(Cursor.visible), MethodType.Setter)]
        [HarmonyPrefix]
        private static void Visible(ref bool value)
        {
            if (Menu.Open) value = true;
        }
    }
}
