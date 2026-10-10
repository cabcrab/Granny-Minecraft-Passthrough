using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace GrannyCraft
{
    [BepInPlugin("grannycraft.passthrough", "GrannyCraft", "0.2.0")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource L;
        internal static Plugin Instance;
        internal static string Dir;
        internal static ConfigEntry<KeyCode> ToggleKey, ViewKey, DebugKey, HudKey, BreakHouseKey, InventoryKey, DepthSourceKey;
        internal static ConfigEntry<bool> BreakableHouse;
        internal static ConfigEntry<float> HouseRegenSeconds;
        internal static ConfigEntry<float> LightMatch, LightReference;
        internal static ConfigEntry<float> DepthBias, Brightness, EdgeSoftness, ThirdPersonDistance, RollSign;
        internal static ConfigEntry<bool> Reproject, StartEnabled, ForwardMouse, MinecraftMovement, LockCameraToMinecraft, SceneDepth, FlipGrannyDepth;
        internal static ConfigEntry<int> Port;
        internal static ConfigEntry<float> GrannyHealth, GrandpaHealth, OtherHealth;
        internal static ConfigEntry<int> KnockdownsToKill;
        internal static ConfigEntry<float> ScanRadius, BlocksPerUnit, FallAnimationHeight;
        internal static ConfigEntry<float> NetherSeconds, NetherTint;
        internal static ConfigEntry<int> NetherRadius;

        public override void Load()
        {
            L = Log;
            Instance = this;
            Dir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            ToggleKey = Config.Bind("Keys", "TogglePassthrough", KeyCode.F8, "Turns the Minecraft passthrough on/off");
            ViewKey = Config.Bind("Keys", "CycleView", KeyCode.F5, "First person / third person back / third person front (like Minecraft's F5)");
            DebugKey = Config.Bind("Keys", "DebugView", KeyCode.F7, "Cycles compositor debug views (depth bands)");
            HudKey = Config.Bind("Keys", "StatusOverlay", KeyCode.F9, "Shows a small status line");
            DepthSourceKey = Config.Bind("Keys", "ToggleDepthFlip", KeyCode.F6, "Test: reads Granny's depth upside down or not (Compositor.FlipGrannyDepth)");
            BreakHouseKey = Config.Bind("Keys", "ToggleBreakableHouse", KeyCode.F10, "Experimental: Granny's house breakable like Minecraft blocks on/off");
            InventoryKey = Config.Bind("Keys", "CreativeInventory", KeyCode.R, "Opens/closes Minecraft's creative inventory (spawn eggs, obsidian, every item); the mouse drives it, Escape closes it too");
            NetherSeconds = Config.Bind("Nether", "NetherSeconds", 30f, "Walking into a lit nether portal turns the house around you into the Nether for this many seconds");
            NetherRadius = Config.Bind("Nether", "NetherRadius", 16, "How far around you (blocks) the house turns into the Nether (4..32)");
            NetherTint = Config.Bind("Nether", "NetherTint", 0.08f, "0..1: how red the picture turns while the Nether is here (0: no tint)");
            HouseRegenSeconds = Config.Bind("Experimental", "HouseRegenSeconds", 30f, "Broken parts of Granny's house grow back after this many seconds (0: never)");
            BreakableHouse = Config.Bind("Experimental", "BreakableHouse", true, "Granny's house can be broken like Minecraft blocks (hand and tools, not TNT): the walls get real holes");
            DepthBias = Config.Bind("Compositor", "DepthBias", 0.03f, "Metres Minecraft may be behind Granny's surface and still show");
            Brightness = Config.Bind("Compositor", "Brightness", 1.0f, "Multiplies Minecraft's world colour (Granny's house is dark)");
            EdgeSoftness = Config.Bind("Compositor", "EdgeSoftness", 0.3f, "Softens Minecraft's edges where it meets Granny's surfaces");
            LightMatch = Config.Bind("Compositor", "LightMatch", 0.85f, "0..1: light Minecraft's blocks by Granny's picture around them (dark rooms make them dark)");
            LightReference = Config.Bind("Compositor", "LightReference", 0.40f, "Granny brightness at which Minecraft blocks keep their own colours");
            SceneDepth = Config.Bind("Compositor", "UseSceneDepth", true, "Hide Minecraft behind Granny's house using the depth buffer Granny's picture is drawn with (exact). Off: Unity's depth texture, which misses some of the house (Minecraft then shows through it or gets cut)");
            FlipGrannyDepth = Config.Bind("Compositor", "FlipGrannyDepth", true, "Granny's depth is read upside down: Unity keeps its render textures bottom-up on DirectX. Turn it off (F6) only if Minecraft hides behind things that aren't there, more the further from the middle of the screen");
            LockCameraToMinecraft = Config.Bind("Compositor", "LockCameraToMinecraftFrame", true, "Granny draws each frame from exactly the camera of Minecraft's newest frame: blocks stay perfectly in place (the view follows the mouse a few milliseconds later)");
            Reproject = Config.Bind("Compositor", "CameraMotionReprojection", false, "Move Minecraft's slightly older frame onto Granny's current camera (less lag when turning fast, but can cut or stretch blocks)");
            RollSign = Config.Bind("Compositor", "RollSign", 1f, "Sign of camera roll sent to Minecraft");
            FallAnimationHeight = Config.Bind("General", "FallAnimationHeight", 2.0f, "With Minecraft movement: drops shorter than this (blocks, from the highest point of the jump) don't play Granny's fall/landing animation");
            MinecraftMovement = Config.Bind("General", "MinecraftMovement", true, "Minecraft movement (speeds, sprint on Ctrl, jump on Space, sneak/crouch on Shift; Granny's drop moves to Q)");
            StartEnabled = Config.Bind("General", "StartEnabled", true, "Passthrough on when the game starts");
            ForwardMouse = Config.Bind("General", "ForwardMouse", true, "Mouse buttons and wheel go to Minecraft while the passthrough is on");
            Port = Config.Bind("General", "Port", 25600, "Minecraft mod's WebSocket port");
            ThirdPersonDistance = Config.Bind("Camera", "ThirdPersonDistance", 3.2f, "Camera distance in third person");
            GrannyHealth = Config.Bind("Combat", "GrannyHealth", 40f, "Granny's health in Minecraft damage points (a zombie has 20)");
            GrandpaHealth = Config.Bind("Combat", "GrandpaHealth", 50f, "Grandpa's health");
            OtherHealth = Config.Bind("Combat", "OtherHealth", 24f, "Health of the other creatures (spider, Slendrina, ...)");
            KnockdownsToKill = Config.Bind("Combat", "KnockdownsToKill", 3, "After this many knockdowns Granny/Grandpa stay dead");
            BlocksPerUnit = Config.Bind("Scale", "BlocksPerUnit", 0.7f, "Minecraft blocks per Granny unit: bigger = smaller blocks and Steve (0.43 puts Steve's eyes at the camera height)");
            ScanRadius = Config.Bind("Collision", "ScanRadius", 70f, "Radius (Granny units) around the player turned into Minecraft collision");

            Link.Port = Port.Value;
            Menu.Key = InventoryKey.Value;
            DevConsole.Init();
            try
            {
                Native.Load(Dir);
                L.LogInfo("native compositor loaded");
            }
            catch (Exception e)
            {
                L.LogError("native compositor failed to load: " + e);
            }

            ClassInjector.RegisterTypeInIl2Cpp<Core>();
            ClassInjector.RegisterTypeInIl2Cpp<CamHook>();
            var go = new GameObject("GrannyCraft");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<Core>();
            Link.Start();
            var harmony = new HarmonyLib.Harmony("grannycraft.passthrough");
            try { harmony.PatchAll(typeof(FakeInput)); }
            catch (Exception e) { L.LogWarning("input hooks unavailable (Minecraft movement needs them): " + e.Message); }
            try { harmony.PatchAll(typeof(CursorPatches)); L.LogInfo("cursor hooks installed"); }
            catch (Exception e) { L.LogWarning("cursor hooks unavailable (the creative inventory frees the pointer every frame instead): " + e.Message); }
            try { harmony.PatchAll(typeof(FallPatches)); L.LogInfo("fall hooks installed"); }
            catch (Exception e) { L.LogWarning("fall hooks unavailable: " + e.Message); }
            try { harmony.PatchAll(typeof(AiPatches)); L.LogInfo("AI stagger hooks installed"); }
            catch (Exception e) { L.LogWarning("AI stagger hooks unavailable: " + e.Message); }
            L.LogInfo("GrannyCraft loaded");
        }
    }
}
