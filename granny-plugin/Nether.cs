using System.Text.Json;
using UnityEngine;

namespace GrannyCraft
{
    /// <summary>
    /// Steve walked into a lit nether portal: Minecraft turns the house around him into the Nether for a while (its
    /// blocks, plants and creatures are Minecraft's, composited like everything else). Granny's side shakes the camera
    /// as the Nether arrives and tints the picture red-orange, gently pulsing, until it goes.
    /// </summary>
    internal static class Nether
    {
        private const float Fade = 1.5f;
        private static bool _on;
        private static float _since, _offAt = -100f;

        /// <summary>How long and how far (Minecraft runs the event): sent on every connect.</summary>
        public static void SendConfig()
        {
            int secs = Mathf.Clamp(Mathf.RoundToInt(Plugin.NetherSeconds.Value), 1, 3600);
            int r = Mathf.Clamp(Plugin.NetherRadius.Value, 4, 32);
            Link.Send("{\"t\":\"nethercfg\",\"secs\":" + secs + ",\"r\":" + r + "}");
        }

        public static void Reset()
        {
            _on = false;
            _offAt = -100f;
        }

        /// <summary>{"t":"nether","on":bool,"secs":seconds} from Minecraft.</summary>
        public static void OnNether(JsonElement m)
        {
            bool on = m.GetProperty("on").GetBoolean();
            if (on == _on) return;
            _on = on;
            if (on)
            {
                _since = Time.unscaledTime;
                var shake = Core.PS?.ShakeAnim;
                if (shake != null) shake.Play();
                Plugin.L.LogInfo("the Nether is here" + (m.TryGetProperty("secs", out var s) ? $" for {s.GetInt32()} s" : ""));
            }
            else
            {
                _offAt = Time.unscaledTime;
                Plugin.L.LogInfo("the Nether is gone");
            }
        }

        public static void OnGUI()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint || !Core.Active) return;
            float now = Time.unscaledTime;
            float k = _on ? Mathf.Clamp01((now - _since) / Fade) : 1f - Mathf.Clamp01((now - _offAt) / Fade);
            float a = Mathf.Clamp01(Plugin.NetherTint.Value) * k * (0.85f + 0.15f * Mathf.Sin(now * 2.2f));
            if (a <= 0.002f) return;
            Color old = GUI.color;
            GUI.color = new Color(0.85f, 0.2f, 0.04f, a);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = old;
        }
    }
}
