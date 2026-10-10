using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GrannyCraft
{
    /// <summary>GrannyCraftNative.dll: the D3D11 compositor that runs on Unity's render thread.</summary>
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct Params
        {
            public IntPtr hostDepthTex;
            public float hostNear, hostFar, hostTanHalfFov, hostAspect;
            public double camX, camY, camZ;
            public float yaw, pitch, roll;
            public int enabled, showWorld, showOverlay, reproject, debugView;
            public float depthBias, hostDepthScale, overlayOpacity, edgeSoftness, brightness;
            public float lightMatch, lightRef, lightMin, lightMip;
            public int wantSlot, hostDepthFlipY;
            public long wantSeq;
            /// <summary>Granny's real projection: P00, P11, P02, P12.</summary>
            public float projX, projY, shiftX, shiftY;
        }

        /// <summary>The newest complete Minecraft frame: the camera it was rendered with (Minecraft coordinates).</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct Peek
        {
            public int ok, slot;
            public long seq, publish;
            public double x, y, z;
            public float yaw, pitch, roll, fov;
            public int firstPerson, pad;
        }

        private delegate void PeekDelegate(out Peek p);
        private static PeekDelegate _peek;

        public static Peek PeekLatest()
        {
            Peek p = default;
            if (Loaded && _peek != null) _peek(out p);
            return p;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct Stats
        {
            public long published, uploaded, composited;
            public int width, height, mcPid, lastError, targetW, targetH, depthFormat, rtvFormat;
            public double mcX, mcY, mcZ;
            public float mcYaw, mcPitch, mcFov, mcFar;
            public int shmOpen;
        }

        private delegate IntPtr GetFuncDelegate();
        private delegate void SetParamsDelegate(int slot, ref Params p);
        private delegate void GetStatsDelegate(out Stats s);
        private delegate int SizeDelegate();
        private delegate int TakeLogDelegate(byte[] buf, int cap);
        private delegate void CaptureDelegate([MarshalAs(UnmanagedType.LPStr)] string path);
        private static CaptureDelegate _capture;
        private delegate void SetIntDelegate(int v);
        private static SetIntDelegate _setDepthSource;

        private static GetFuncDelegate _getFunc;
        private static SetParamsDelegate _setParams;
        private static GetStatsDelegate _getStats;
        private static TakeLogDelegate _takeLog;
        public static bool Loaded;
        public static IntPtr RenderEventFunc;

        public static void Load(string dir)
        {
            string path = Path.Combine(dir, "GrannyCraftNative.dll");
            IntPtr lib = NativeLibrary.Load(path);
            _getFunc = Marshal.GetDelegateForFunctionPointer<GetFuncDelegate>(NativeLibrary.GetExport(lib, "GC_GetRenderEventFunc"));
            _setParams = Marshal.GetDelegateForFunctionPointer<SetParamsDelegate>(NativeLibrary.GetExport(lib, "GC_SetParams"));
            _getStats = Marshal.GetDelegateForFunctionPointer<GetStatsDelegate>(NativeLibrary.GetExport(lib, "GC_GetStats"));
            _takeLog = Marshal.GetDelegateForFunctionPointer<TakeLogDelegate>(NativeLibrary.GetExport(lib, "GC_TakeLog"));
            var pSize = Marshal.GetDelegateForFunctionPointer<SizeDelegate>(NativeLibrary.GetExport(lib, "GC_ParamsSize"))();
            var sSize = Marshal.GetDelegateForFunctionPointer<SizeDelegate>(NativeLibrary.GetExport(lib, "GC_StatsSize"))();
            if (pSize != Marshal.SizeOf<Params>() || sSize != Marshal.SizeOf<Stats>())
                throw new Exception($"native struct mismatch: params {pSize} vs {Marshal.SizeOf<Params>()}, stats {sSize} vs {Marshal.SizeOf<Stats>()}");
            _capture = Marshal.GetDelegateForFunctionPointer<CaptureDelegate>(NativeLibrary.GetExport(lib, "GC_RequestCapture"));
            _peek = Marshal.GetDelegateForFunctionPointer<PeekDelegate>(NativeLibrary.GetExport(lib, "GC_PeekLatest"));
            var kSize = Marshal.GetDelegateForFunctionPointer<SizeDelegate>(NativeLibrary.GetExport(lib, "GC_PeekSize"))();
            if (kSize != Marshal.SizeOf<Peek>()) throw new Exception($"native peek mismatch: {kSize} vs {Marshal.SizeOf<Peek>()}");
            // an older GrannyCraftNative.dll doesn't have it: then it always uses _CameraDepthTexture
            if (NativeLibrary.TryGetExport(lib, "GC_SetDepthSource", out IntPtr ds)) _setDepthSource = Marshal.GetDelegateForFunctionPointer<SetIntDelegate>(ds);
            RenderEventFunc = _getFunc();
            Loaded = true;
        }

        public static void SetParams(int slot, ref Params p) { if (Loaded) _setParams(slot, ref p); }

        public static void Capture(string path) { if (Loaded) _capture(path); }

        /// <summary>Event flag: copy Granny's depth buffer now (issued right after her opaque geometry).</summary>
        public const int GrabDepth = 0x100;

        public static void SetDepthSource(bool sceneDepth) { if (Loaded && _setDepthSource != null) _setDepthSource(sceneDepth ? 1 : 0); }

        public static Stats GetStats()
        {
            Stats s = default;
            if (Loaded) _getStats(out s);
            return s;
        }

        private static readonly byte[] LogBuf = new byte[4096];

        public static string TakeLog()
        {
            if (!Loaded) return "";
            int n = _takeLog(LogBuf, LogBuf.Length);
            return n > 0 ? System.Text.Encoding.ASCII.GetString(LogBuf, 0, n) : "";
        }
    }
}
