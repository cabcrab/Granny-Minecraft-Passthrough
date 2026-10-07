using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GrannyCraft
{
    /// <summary>
    /// Lives on Granny's player camera. Each frame: issues the compositor event with this frame's parameter slot (after
    /// image effects, with the camera target bound), moves the camera for third person only while it renders, and
    /// hands the compositor this frame's depth texture and pose.
    /// </summary>
    public class CamHook : MonoBehaviour
    {
        public CamHook(IntPtr p) : base(p) { }

        public static CamHook Instance;
        public static bool ThirdPerson;
        public static Vector3 ViewPos;
        public static Quaternion ViewRot;

        private Camera _cam;
        private CommandBuffer _cb;
        private bool _moved;
        private Vector3 _savedPos;
        private Quaternion _savedRot;
        private readonly List<Renderer> _hidden = new List<Renderer>();
        private int _slot;
        private static int _depthId = -1;
        private Texture _lastDepth;
        private IntPtr _lastDepthPtr;
        internal static Native.Params Last;
        /// <summary>Objects that follow the camera without being its children (Granny's held items: PlayerStuff).</summary>
        public static readonly List<Transform> Followers = new List<Transform>();
        private readonly List<(Transform, Vector3, Quaternion)> _followersMoved = new List<(Transform, Vector3, Quaternion)>();
        private int _wantSlot = -1;
        private long _wantSeq;
        private long _lastPublish = -1;
        private float _lastPublishTime;
        private float _savedFov;
        private bool _fovChanged;

        public void Init(Camera cam)
        {
            _cam = cam;
            _cam.depthTextureMode |= DepthTextureMode.Depth;
            _cb = new CommandBuffer();
            _cb.name = "GrannyCraft composite";
            _cam.AddCommandBuffer(CameraEvent.AfterImageEffects, _cb);
            Instance = this;
            if (_depthId < 0) _depthId = Shader.PropertyToID("_CameraDepthTexture");
            Plugin.L.LogInfo($"camera hooked: {cam.name} renderingPath={cam.actualRenderingPath} hdr={cam.allowHDR} msaa={cam.allowMSAA} near={cam.nearClipPlane} far={cam.farClipPlane} fov={cam.fieldOfView}");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            try { if (_cam != null && _cb != null) _cam.RemoveCommandBuffer(CameraEvent.AfterImageEffects, _cb); } catch { }
        }

        private void OnPreCull()
        {
            if (_cb == null) return;
            _slot = (_slot + 1) & 7;
            _cb.Clear();
            if (Core.Active && Native.Loaded)
            {
                _cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                _cb.IssuePluginEvent(Native.RenderEventFunc, _slot);
            }

            // Granny renders this frame from exactly the camera Minecraft's newest frame was rendered with: both pictures
            // then match pixel for pixel and Minecraft's blocks stay put when the camera moves (instead of trailing behind)
            _wantSlot = -1;
            _wantSeq = 0;
            bool locked = false;
            Vector3 lockPos = default;
            Quaternion lockRot = default;
            if (Core.Active && Native.Loaded && Plugin.LockCameraToMinecraft.Value && _cam == Core.Cam)
            {
                var pk = Native.PeekLatest();
                if (pk.ok != 0)
                {
                    if (pk.publish != _lastPublish) { _lastPublish = pk.publish; _lastPublishTime = Time.unscaledTime; }
                    Vector3 target = ThirdPerson ? ViewPos : transform.position;
                    lockPos = Coords.ToUnity(pk.x, pk.y, pk.z);
                    // a stalled Minecraft or a teleport: Granny's own camera
                    if (Time.unscaledTime - _lastPublishTime < 0.25f && (lockPos - target).magnitude < 3f * Coords.U)
                    {
                        lockRot = Quaternion.Euler(pk.pitch, pk.yaw, 0f);
                        _wantSlot = pk.slot;
                        _wantSeq = pk.seq;
                        locked = true;
                        _savedFov = _cam.fieldOfView;
                        if (pk.fov > 1f) _cam.fieldOfView = pk.fov;
                    }
                }
            }

            if (locked || (ThirdPerson && _cam == Core.Cam))
            {
                _savedPos = transform.position;
                _savedRot = transform.rotation;
                _fovChanged = locked;
                if (locked) transform.SetPositionAndRotation(lockPos, lockRot);
                else transform.SetPositionAndRotation(ViewPos, ViewRot);
                _moved = true;
                // Granny's held items aren't attached to the camera (PlayerStuff follows it): in first person they move
                // with the camera, else they would shake against it
                _followersMoved.Clear();
                if (!ThirdPerson)
                {
                    Quaternion delta = transform.rotation * Quaternion.Inverse(_savedRot);
                    foreach (var f in Followers)
                    {
                        if (f == null || f.IsChildOf(transform)) continue;
                        _followersMoved.Add((f, f.position, f.rotation));
                        f.SetPositionAndRotation(transform.position + delta * (f.position - _savedPos), delta * f.rotation);
                    }
                }
            }

            if (ThirdPerson && _cam == Core.Cam)
            {
                // Granny's first-person held items hang off the camera: not drawn while the camera is behind the player
                _hidden.Clear();
                var rs = GetComponentsInChildren<Renderer>();
                foreach (var r in rs)
                {
                    if (r != null && r.enabled)
                    {
                        r.enabled = false;
                        _hidden.Add(r);
                    }
                }
                // the held items that only follow the camera would float in front of it too
                foreach (var f in Followers)
                {
                    if (f == null || f.IsChildOf(transform)) continue;
                    foreach (var r in f.GetComponentsInChildren<Renderer>())
                        if (r != null && r.enabled) { r.enabled = false; _hidden.Add(r); }
                }
            }
        }

        private void OnPostRender()
        {
            if (Core.Active)
            {
                var p = new Native.Params();
                Texture depth = Shader.GetGlobalTexture(_depthId);
                if (depth != null)
                {
                    if (_lastDepth == null || depth.Pointer != _lastDepth.Pointer)
                    {
                        _lastDepth = depth;
                        _lastDepthPtr = depth.GetNativeTexturePtr();
                        Plugin.L.LogInfo($"depth texture {depth.name} {depth.width}x{depth.height} ptr={_lastDepthPtr}");
                    }
                    p.hostDepthTex = _lastDepthPtr;
                }

                var t = transform;
                var mc = Coords.ToMc(t.position);
                Coords.YawPitch(t.forward, out p.yaw, out p.pitch);
                p.camX = mc.x; p.camY = mc.y; p.camZ = mc.z;
                p.hostNear = _cam.nearClipPlane;
                p.hostFar = _cam.farClipPlane;
                p.hostTanHalfFov = Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                p.hostAspect = _cam.aspect;
                p.enabled = 1;
                p.showWorld = 1;
                p.showOverlay = 1;
                p.reproject = Plugin.Reproject.Value ? 1 : 0;
                p.debugView = Core.DebugView;
                p.depthBias = Plugin.DepthBias.Value;
                p.hostDepthScale = (float)Coords.S; // Granny units -> blocks
                p.overlayOpacity = 1f;
                p.edgeSoftness = Plugin.EdgeSoftness.Value;
                p.brightness = Plugin.Brightness.Value;
                p.lightMatch = Plugin.LightMatch.Value;
                p.lightRef = Plugin.LightReference.Value;
                p.lightMin = 0.3f;
                p.lightMip = 6f;
                p.wantSlot = _wantSlot;
                p.wantSeq = _wantSeq;
                Native.SetParams(_slot, ref p);
                Last = p;
            }

            if (_moved)
            {
                transform.SetPositionAndRotation(_savedPos, _savedRot);
                if (_fovChanged) { _cam.fieldOfView = _savedFov; _fovChanged = false; }
                foreach (var (f, p, r) in _followersMoved) if (f != null) f.SetPositionAndRotation(p, r);
                _followersMoved.Clear();
                _moved = false;
                foreach (var r in _hidden) if (r != null) r.enabled = true;
                _hidden.Clear();
            }
        }
    }
}
