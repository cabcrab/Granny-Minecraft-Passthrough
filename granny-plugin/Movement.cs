using UnityEngine;

namespace GrannyCraft
{
    /// <summary>
    /// Minecraft movement for Granny's player, on Granny's own CharacterController (so crouching under tables and beds,
    /// hiding, doors and Granny's floors all still work): Minecraft speeds (walk 4.317, sprint 5.612, sneak 1.295 blocks
    /// per second), ground friction and air momentum, jumping (Space, ~1.25 blocks) with Minecraft gravity, sprint on
    /// Ctrl, sneak on Shift like Minecraft (sneaking speed, Steve crouches, no stepping off an edge; Granny's own crouch
    /// to hide under tables and beds stays on C), cobweb-slow in spider webs. Granny's own walking (MobileFPS) keeps the
    /// mouse look but its speeds are set to 0 while this runs, and restored when the passthrough is turned off.
    /// </summary>
    internal static class Movement
    {
        private const float Walk = 4.317f, Sprint = 5.612f, Sneak = 1.295f; // blocks per second
        private const float JumpVelocity = 8.4f;   // 0.42 blocks per tick
        private const float Gravity = 32f;         // 0.08 blocks per tick^2
        private const float SprintJumpBoost = 4f;  // 0.2 blocks per tick forward
        private static MobileFPS _fps;
        private static CrouchHolder _crouch;
        private static bool _on;
        private static float _savedMove, _savedSpeedMove, _savedSpeedCrouch, _savedFall, _savedStep;
        private static Vector3 _vel; // blocks per second (horizontal in Unity axes)
        private static float _vy;    // blocks per second
        public static bool Sprinting, Moving, Sneaking;
        public static bool Active => _on;

        // ---- falls: a normal jump or a short drop is not a fall in Granny (she has no jump) ----
        private static bool _airborne;
        private static float _peakY;      // highest point (Unity y) since leaving the ground
        private static float _airTime;    // seconds since leaving the ground
        /// <summary>How far below the highest point of this jump/drop the player is, in blocks.</summary>
        public static float DropBlocks => _airborne && Core.PlayerRoot != null ? (_peakY - Core.PlayerRoot.position.y) * (float)Coords.S : 0f;
        public static float AirTime => _airborne ? _airTime : 0f;
        private static bool _realFall;   // this drop already went past FallAnimationHeight
        private static float _maxDrop;   // blocks below the highest point, so far
        private static bool _grounded;
        private static Vector3 _lastPos;
        private static float _debugUntil, _nextDebug;
        private static Vector3 _ourMoveSum;

        public static void Debug(float seconds) { _debugUntil = Time.unscaledTime + seconds; _lastPos = Core.PlayerRoot.position; _ourMoveSum = Vector3.zero; }

        public static void Reset()
        {
            _fps = null; _crouch = null; _on = false; _vel = Vector3.zero; _vy = 0f; _airborne = false; _realFall = false;
        }

        private static bool Wanted => Core.Enabled && Link.Connected && Plugin.MinecraftMovement.Value && Core.PS != null && Core.CC != null;

        public static void Update()
        {
            if (Core.PS == null) return;
            if (_fps == null) _fps = Object.FindObjectOfType<MobileFPS>();
            if (_crouch == null || !_crouch.isActiveAndEnabled) _crouch = Object.FindObjectOfType<CrouchHolder>();
            if (_fps == null) return;
            bool want = Wanted;
            if (want != _on) Switch(want);
            if (!_on) return;
            // Granny may reset its speeds (crouch, webs, cutscenes): keep them at 0 while we move the player
            _fps.moveSpeed = 0f; _fps.SpeedMove = 0f; _fps.SpeedMoveCrouch = 0f; _fps.FallSpeed = 0f;
            // Granny's landing animation moves the player itself: don't fight it
            if (FallPatches.LandingActive) { _vel = Vector3.zero; _vy = 0f; return; }
            Step(Time.deltaTime);
        }

        private static void Switch(bool on)
        {
            var cc = Core.CC;
            if (on)
            {
                _savedMove = _fps.moveSpeed; _savedSpeedMove = _fps.SpeedMove; _savedSpeedCrouch = _fps.SpeedMoveCrouch; _savedFall = _fps.FallSpeed;
                Plugin.L.LogInfo($"Minecraft movement on (Granny speeds were move={_savedMove} walk={_savedSpeedMove} crouch={_savedSpeedCrouch} fall={_savedFall})");
            }
            else
            {
                _fps.moveSpeed = _savedMove; _fps.SpeedMove = _savedSpeedMove; _fps.SpeedMoveCrouch = _savedSpeedCrouch; _fps.FallSpeed = _savedFall;
                Plugin.L.LogInfo("Minecraft movement off (Granny's own movement restored)");
            }
            _on = on;
            _vel = Vector3.zero; _vy = 0f; Sneaking = false;
        }

        /// <summary>
        /// Sneaking on the ground, like Minecraft: a step that would leave nothing under the player (a drop of more than
        /// about half a block: stairs down, a table's edge, a block's edge) doesn't happen; along the edge still works.
        /// </summary>
        private static void KeepOnEdge(CharacterController cc, ref Vector3 move)
        {
            Bounds b = cc.bounds;
            Vector3 feet = new Vector3(b.center.x, b.min.y, b.center.z);
            float half = Mathf.Min(b.extents.x, b.extents.z) * 0.9f;
            if (!Supported(feet, half)) return; // already over the edge (pushed, landed half on it): don't get stuck
            if (Supported(feet + new Vector3(move.x, 0f, move.z), half)) return;
            if (Supported(feet + new Vector3(move.x, 0f, 0f), half)) { move.z = 0f; _vel.z = 0f; }
            else if (Supported(feet + new Vector3(0f, 0f, move.z), half)) { move.x = 0f; _vel.x = 0f; }
            else { move.x = move.z = 0f; _vel = Vector3.zero; }
        }

        /// <summary>Ground within half a block under any part of the player's feet (Granny's floors or a Minecraft block).</summary>
        private static bool Supported(Vector3 feet, float half)
        {
            float u = Coords.U, depth = 0.6f * u;
            int mask = Scanner.Mask | (1 << Blocks.Layer);
            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                {
                    Vector3 o = feet + new Vector3(i * half, 0.1f * u, j * half);
                    if (Physics.Raycast(o, Vector3.down, depth + 0.1f * u, mask, QueryTriggerInteraction.Ignore)) return true;
                }
            return false;
        }

        private static void Step(float dt)
        {
            var cc = Core.CC;
            if (!cc.enabled || !cc.gameObject.activeInHierarchy || dt <= 0f) return;
            // cutscenes, hiding spots, being caught: Granny locks the player
            bool locked = !_fps.AbleToMove || !_fps.isAllowedToMove || Core.PS.Killed || Core.PS.IsJumpscared
                          || Core.PS.HidingInClosets || Core.PS.InBed1 || Core.PS.InBed2 || Core.PS.InBed3 || Core.PS.InCar
                          || Core.PS.InCoffinAttic || Core.PS.InCoffinBackyard || Menu.Open;
            float f = 0f, s = 0f;
            if (!locked)
            {
                if (FakeInput.Key(KeyCode.W)) f += 1f;
                if (FakeInput.Key(KeyCode.S)) f -= 1f;
                if (FakeInput.Key(KeyCode.D)) s += 1f;
                if (FakeInput.Key(KeyCode.A)) s -= 1f;
            }
            bool crouched = _crouch != null && (_crouch.isCrouching || _crouch.IsCrouched);
            // our own ground contact: Granny's controller moves the collider again after us (with zero), which resets isGrounded
            bool grounded = _grounded || cc.isGrounded;
            Sneaking = !locked && (FakeInput.Key(KeyCode.LeftShift) || FakeInput.Key(KeyCode.RightShift));
            Sprinting = !locked && f > 0f && !crouched && !Sneaking && (FakeInput.Key(KeyCode.LeftControl) || FakeInput.Key(KeyCode.RightControl) || (Sprinting && FakeInput.Key(KeyCode.W)));
            float speed = crouched || Sneaking ? Sneak : Sprinting ? Sprint : Walk;
            if (_fps.InWeb) speed *= 0.25f; // a cobweb

            Transform view = Core.MainCam != null ? Core.MainCam.transform : Core.Cam.transform;
            Vector3 fwd = view.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Core.PlayerRoot.forward;
            fwd.Normalize();
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            Vector3 wish = fwd * f + right * s;
            if (wish.sqrMagnitude > 1f) wish.Normalize();
            Vector3 target = wish * speed;
            Moving = wish.sqrMagnitude > 0.01f;

            // ground: quick start and stop (friction); air: keeps momentum, little control
            float k = grounded ? 1f - Mathf.Exp(-dt / 0.09f) : 1f - Mathf.Exp(-dt / 0.9f);
            _vel = Vector3.Lerp(_vel, target, k);

            if (grounded)
            {
                if (_vy < 0f) _vy = -2f; // stay pressed to the floor (slopes, stairs)
                if (!locked && !crouched && FakeInput.Key(KeyCode.Space))
                {
                    _vy = JumpVelocity;
                    if (Sprinting) _vel += fwd * SprintJumpBoost;
                }
            }
            else
            {
                _vy -= Gravity * dt;
                _vy *= Mathf.Pow(0.98f, dt * 20f);
            }

            Vector3 move = new Vector3(_vel.x, _vy, _vel.z) * Coords.U * dt;
            if (Sneaking && grounded) KeepOnEdge(cc, ref move);
            var flags = cc.Move(move);
            _grounded = (flags & CollisionFlags.Below) != 0;
            float y = Core.PlayerRoot.position.y;
            if (_grounded)
            {
                if (_airborne && _realFall) FallPatches.Land(_airTime, _maxDrop); // a real fall: Granny's landing
                _airborne = false;
                _realFall = false;
                _airTime = 0f;
                _maxDrop = 0f;
            }
            else
            {
                if (!_airborne) { _airborne = true; _peakY = y; _airTime = 0f; _maxDrop = 0f; }
                _peakY = Mathf.Max(_peakY, y);
                _airTime += dt;
                _maxDrop = Mathf.Max(_maxDrop, (_peakY - y) * (float)Coords.S);
                // jumps and short drops are not falls in Granny (she has no jump); from FallAnimationHeight down they are
                if (_maxDrop >= Plugin.FallAnimationHeight.Value) _realFall = true;
            }
            if ((flags & CollisionFlags.Above) != 0 && _vy > 0f) _vy = 0f; // head against the ceiling
            if (Time.unscaledTime < _debugUntil)
            {
                _ourMoveSum += move;
                if (Time.unscaledTime >= _nextDebug)
                {
                    _nextDebug = Time.unscaledTime + 0.25f;
                    Vector3 p = Core.PlayerRoot.position;
                    Plugin.L.LogInfo($"move: vel {_vel.magnitude:F2} b/s vy {_vy:F2} grounded {_grounded} ours {_ourMoveSum.magnitude:F2} actual {(p - _lastPos).magnitude:F2} units, fps.moveSpeed {_fps.moveSpeed}");
                    _lastPos = p; _ourMoveSum = Vector3.zero;
                }
            }
        }
    }
}
