using UnityEngine;

namespace GrannyCraft
{
    /// <summary>
    /// Granny's keys while the passthrough is on: all her interactions on her own key (E), items dropped with Q, and a
    /// right click on a door prompt also opens it (else a right click is Minecraft's "use"). Only in memory: the saved
    /// key bindings are never changed.
    /// </summary>
    internal static class Interact
    {
        private static KeyCode _original = KeyCode.None;
        private static bool _applied;
        private static float _next;

        public static void Update()
        {
            if (Core.PS == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;
            Apply(Core.Enabled && Link.Connected);
        }

        /// <summary>
        /// Granny's items: picked up with her own key (E), dropped with Q. Doors and traps: right click, like Minecraft's
        /// "use". Enforced every half second (Granny may set her keys again); off: everything back on Granny's key.
        /// </summary>
        private static void Apply(bool on)
        {
            var pr = Core.PS.PickRay;
            if (pr == null) return;
            if (_original == KeyCode.None)
            {
                _original = pr.MainInteract == KeyCode.Mouse1 ? KeyCode.E : pr.MainInteract;
                Plugin.L.LogInfo("Granny's interact key: " + _original);
            }
            // every interaction on Granny's own key (E): a right click on a door prompt presses it for her (Core.Key)
            KeyCode doors = _original;
            if (pr.MainInteract != _original) pr.MainInteract = _original;
            foreach (var d in Object.FindObjectsOfType<DoorRay>()) if (d.InteractKey != doors) d.InteractKey = doors;
            foreach (var r in Object.FindObjectsOfType<RayRemoveTraps>()) if (r.KeyInteract != doors) r.KeyInteract = doors;
            // an earlier version moved Granny's menu bindings to the right button: back to her key
            foreach (var k in Resources.FindObjectsOfTypeAll<KeyBindManage>())
                if (k.PrimaryInteraction == KeyCode.Mouse1 && _original != KeyCode.Mouse1) k.PrimaryInteraction = _original;
            if (_applied == on) return;
            _applied = on;
            Plugin.L.LogInfo(on ? $"Granny's interactions on {_original} (drop: Q; right click on a door works too)" : $"everything back on {_original}");
            RemapMovementKeys(on);
        }

        private static KeyCode _dropOrig = KeyCode.None;
        /// <summary>Granny's own drop key (Space unless rebound in her menu); Q presses it for her.</summary>
        public static KeyCode GrannyDropKey => _dropOrig == KeyCode.None ? KeyCode.Space : _dropOrig;

        /// <summary>
        /// Space is Minecraft's jump and Q Minecraft's drop: while the passthrough is on, a real press of Granny's drop
        /// key no longer reaches Granny (only the jump), and Q presses it for her when Granny's item is the selected one.
        /// Done on the keyboard input itself, so it works wherever Granny reads her binding from. Crouch stays C.
        /// </summary>
        private static void RemapMovementKeys(bool on)
        {
            if (!Plugin.MinecraftMovement.Value) return;
            var binds = Resources.FindObjectsOfTypeAll<KeyBindManage>();
            if (_dropOrig == KeyCode.None)
            {
                foreach (var k in binds)
                {
                    Plugin.L.LogInfo($"Granny bindings: primary={k.PrimaryInteraction} secondary={k.SecondaryInteraction} drop={k.Dropping} crouch={k.Crouching} shoot={k.Shooting}");
                    if (_dropOrig == KeyCode.None && k.Dropping != KeyCode.F13 && k.Dropping != KeyCode.Q && k.Dropping != KeyCode.None) _dropOrig = k.Dropping;
                }
                if (_dropOrig == KeyCode.None) _dropOrig = KeyCode.Space;
            }
            // an earlier version moved the drop to F13: back to Granny's own key
            foreach (var pr in Resources.FindObjectsOfTypeAll<PickRay>()) if (pr.DropKey == KeyCode.F13) pr.DropKey = _dropOrig;
            foreach (var k in binds) if (k.Dropping == KeyCode.F13) k.Dropping = _dropOrig;
            FakeInput.TakenFromGranny.Clear();
            if (on) FakeInput.TakenFromGranny.Add(_dropOrig);
            // an earlier version moved crouch to Shift: Granny's crouch is C
            foreach (var ch in Resources.FindObjectsOfTypeAll<CrouchHolder>())
                if (ch.gameObject.scene.name != null && ch.CrouchKey == KeyCode.LeftShift) ch.CrouchKey = KeyCode.C;
            foreach (var k in binds) if (k.Crouching == KeyCode.LeftShift) k.Crouching = KeyCode.C;
            Plugin.L.LogInfo(on ? $"Granny's drop ({_dropOrig}) only through Q; {_dropOrig} is the jump" : $"Granny's drop back on {_dropOrig}");
        }

        /// <summary>Granny shows an interaction prompt (door, item, winch): the right click is hers.</summary>
        public static bool GrannyHasTarget()
        {
            var pr = Core.PS?.PickRay;
            if (pr == null) return false;
            // items are picked up with E: a right click on them is Minecraft's
            return Shown(pr.EButton_Door) || Shown(pr.EButtonWinch);
        }

        private static bool Shown(Component c) => c != null && c.gameObject.activeInHierarchy && (c.TryCast<Behaviour>()?.enabled ?? true);
        private static bool Shown(GameObject g) => g != null && g.activeInHierarchy;

        public static string Debug()
        {
            var pr = Core.PS?.PickRay;
            if (pr == null) return "-";
            return $"door={Shown(pr.EButton_Door)} pick={Shown(pr.EButton_Pick)} winch={Shown(pr.EButtonWinch)} highlight={Shown(pr.T_HighlightItem)}";
        }
    }
}
