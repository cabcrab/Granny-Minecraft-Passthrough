// GrannyCraftNative: composites Minecraft's exported frame (shared memory "Local\GrannyCraftFrame", written by the
// grannycraft Fabric mod) into Granny Legacy's picture, on Unity's render thread (D3D11).
//
// Called from a camera CommandBuffer (CameraEvent.AfterImageEffects) through IssuePluginEvent, with the camera
// target bound. Minecraft's world layer is drawn where it is nearer than Granny's depth (her own depth buffer, copied
// after her opaque geometry by a second event; _CameraDepthTexture when that isn't available);
// Minecraft's overlay layer (hand, held item, HUD) is drawn on top of everything.
//
// The ray-marched re-projection (Minecraft's slightly older frame moved onto Granny's current camera) follows the
// approach of universal-modder's MCPassthrough.fx (MIT, Rehan and contributors).
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "d3dcompiler.lib")

#define EXPORT extern "C" __declspec(dllexport)

// ---- shared memory layout (see FrameExporter.java) ----
static const wchar_t* SHM_NAME = L"Local\\GrannyCraftFrame";
static const uint32_t MAGIC = 0x5450434D; // "MCPT"
static const int SLOT_DESC = 256;
static const int SLOT_DESC_BYTES = 128;

// ---- parameters from the C# plugin (main thread), read on the render thread ----
#pragma pack(push, 4)
struct Params {
	void* hostDepthTex;   // ID3D11Texture2D* of _CameraDepthTexture
	float hostNear, hostFar;
	float hostTanHalfFov; // tan(vertical fov / 2) of Granny's camera
	float hostAspect;     // width / height
	// Granny's current camera in Minecraft coordinates
	double camX, camY, camZ;
	float yaw, pitch, roll;
	int enabled;          // 0: draw nothing
	int showWorld;        // draw the depth-tested world layer
	int showOverlay;      // draw the hand/HUD layer
	int reproject;        // move Minecraft's frame onto the current camera
	int debugView;        // 0 composite, 1 host depth bands, 2 mc depth bands, 3 diff
	float depthBias;      // metres Minecraft may be behind Granny's surface and still show
	float hostDepthScale; // calibration multiplier for Granny's linear depth
	float overlayOpacity;
	float edgeSoftness;
	float brightness;     // multiplies Minecraft's world colour (match Granny's dark house)
	float lightMatch;     // 0..1: relight Minecraft by Granny's (blurred) picture around it
	float lightRef;       // Granny brightness at which Minecraft keeps its own colours
	float lightMin;       // darkest gain
	float lightMip;       // blur (mip level of Granny's picture)
	int wantSlot;         // the Minecraft frame Granny's camera was placed for this frame (-1: the newest)
	int hostDepthFlipY;   // Granny's depth is stored bottom-up (Unity's render textures on D3D): read it mirrored
	int64_t wantSeq;      // that slot's sequence number when it was chosen
	float hostProj[4];    // Granny's real projection: P00, P11, P02, P12 (0, 0, 0, 0: a plain fov/aspect one)
};
// the newest Minecraft frame, for Granny to render from exactly the same camera
struct Peek {
	int32_t ok;
	int32_t slot;
	int64_t seq;
	int64_t publish;
	double x, y, z;
	float yaw, pitch, roll, fov;
	int32_t firstPerson;
	int32_t pad;
};
struct Stats {
	int64_t published;     // Minecraft's publish counter
	int64_t uploaded;      // frames uploaded here
	int64_t composited;    // composite passes run
	int32_t width, height; // last Minecraft frame
	int32_t mcPid;
	int32_t lastError;     // HRESULT or step code
	int32_t targetW, targetH;
	int32_t depthFormat;   // DXGI format of Granny's depth texture
	int32_t rtvFormat;
	double mcX, mcY, mcZ;  // pose the last uploaded Minecraft frame was rendered with
	float mcYaw, mcPitch, mcFov, mcFar;
	int32_t shmOpen;
};
#pragma pack(pop)

static std::mutex g_paramLock;
static Params g_params[8] = {};
static Stats g_stats = {};
static char g_log[4096];

static void Log(const char* fmt, ...) {
	char buf[512];
	va_list ap;
	va_start(ap, fmt);
	vsnprintf(buf, sizeof buf, fmt, ap);
	va_end(ap);
	size_t n = strlen(g_log);
	if (n + strlen(buf) + 2 >= sizeof g_log) { g_log[0] = 0; n = 0; }
	strcat_s(g_log, buf);
	strcat_s(g_log, "\n");
	OutputDebugStringA(buf);
}

// ---- shared memory ----
static HANDLE g_map = nullptr;
static uint8_t* g_view = nullptr;
static int64_t g_lastPublish = -1;

static std::mutex g_shmLock;

static bool OpenShm() {
	std::lock_guard<std::mutex> lock(g_shmLock);
	if (g_view) {
		return true;
	}
	g_map = OpenFileMappingW(FILE_MAP_READ, FALSE, SHM_NAME);
	if (!g_map) {
		return false;
	}
	g_view = (uint8_t*)MapViewOfFile(g_map, FILE_MAP_READ, 0, 0, 0);
	if (!g_view || *(uint32_t*)g_view != MAGIC) {
		if (g_view) UnmapViewOfFile(g_view);
		CloseHandle(g_map);
		g_view = nullptr;
		g_map = nullptr;
		return false;
	}
	Log("shared memory open");
	return true;
}

static void CloseShm() {
	if (g_view) UnmapViewOfFile(g_view);
	if (g_map) CloseHandle(g_map);
	g_view = nullptr;
	g_map = nullptr;
}

// ---- D3D objects ----
static ID3D11Device* g_dev = nullptr;
static ID3D11Texture2D* g_texColor = nullptr;
static ID3D11Texture2D* g_texDepth = nullptr;
static ID3D11Texture2D* g_texOverlay = nullptr;
static ID3D11ShaderResourceView* g_srvColor = nullptr;
static ID3D11ShaderResourceView* g_srvDepth = nullptr;
static ID3D11ShaderResourceView* g_srvOverlay = nullptr;
static int g_texW = 0, g_texH = 0;
static ID3D11Texture2D* g_hostDepthTex = nullptr; // not owned
static ID3D11ShaderResourceView* g_srvHostDepth = nullptr;
static ID3D11VertexShader* g_vs = nullptr;
static ID3D11PixelShader* g_ps = nullptr;
static ID3D11Buffer* g_cb = nullptr;
static ID3D11SamplerState* g_sampLinear = nullptr;
static ID3D11SamplerState* g_sampPoint = nullptr;
static ID3D11BlendState* g_blend = nullptr;
static ID3D11DepthStencilState* g_dss = nullptr;
static ID3D11RasterizerState* g_rs = nullptr;
static bool g_initFailed = false;
static ID3D11Texture2D* g_hostColor = nullptr;
static ID3D11ShaderResourceView* g_srvHostColor = nullptr;
static int g_hcW = 0, g_hcH = 0;
static DXGI_FORMAT g_hcFmt = DXGI_FORMAT_UNKNOWN;

static DXGI_FORMAT ViewFormat(DXGI_FORMAT f) {
	switch (f) {
	case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
	case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
	case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
	case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
	default: return f;
	}
}

/** A blurred copy of Granny's picture (mips), for relighting Minecraft by the light around it. */
static void CopyHostColor(ID3D11DeviceContext* ctx, ID3D11Texture2D* rt, const D3D11_TEXTURE2D_DESC& td) {
	if (td.SampleDesc.Count != 1) return;
	if (!g_hostColor || g_hcW != (int)td.Width || g_hcH != (int)td.Height || g_hcFmt != td.Format) {
		if (g_srvHostColor) { g_srvHostColor->Release(); g_srvHostColor = nullptr; }
		if (g_hostColor) { g_hostColor->Release(); g_hostColor = nullptr; }
		D3D11_TEXTURE2D_DESC d = {};
		d.Width = td.Width; d.Height = td.Height; d.MipLevels = 0; d.ArraySize = 1; d.Format = td.Format;
		d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
		d.MiscFlags = D3D11_RESOURCE_MISC_GENERATE_MIPS;
		if (FAILED(g_dev->CreateTexture2D(&d, nullptr, &g_hostColor))) { Log("host colour copy unavailable (fmt %d)", (int)td.Format); g_hcFmt = td.Format; g_hcW = td.Width; g_hcH = td.Height; return; }
		D3D11_SHADER_RESOURCE_VIEW_DESC sd = {};
		sd.Format = ViewFormat(td.Format); sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D; sd.Texture2D.MipLevels = (UINT)-1;
		g_dev->CreateShaderResourceView(g_hostColor, &sd, &g_srvHostColor);
		g_hcW = td.Width; g_hcH = td.Height; g_hcFmt = td.Format;
		Log("host colour copy %ux%u fmt %d", td.Width, td.Height, (int)td.Format);
	}
	if (!g_hostColor || !g_srvHostColor) return;
	ctx->CopySubresourceRegion(g_hostColor, 0, 0, 0, 0, rt, 0, nullptr);
	ctx->GenerateMips(g_srvHostColor);
}

// what the last uploaded frame was rendered with (Minecraft coordinates)
static struct { double x, y, z; float yaw, pitch, roll, fov, nearP, farP; int flags; } g_mcPose;

struct CB {
	float mcPlanes[4];   // near, far, flags, firstPerson
	float hostPlanes[4]; // near, far, depthScale, depthBias
	float warpRow0[4];
	float warpRow1[4];
	float warpRow2[4];
	float warpTan[4];    // tan host half fov, tan mc half fov, mc aspect, host aspect
	float warpT[4];
	float target[4];     // width, height, 1/w, 1/h
	float opts[4];       // showWorld, showOverlay, reproject, debugView
	float opts2[4];      // overlayOpacity, srgbTarget, edgeSoftness, brightness
	float light[4];      // lightMatch, lightRef, lightMin, lightMip
	float hostProj[4];   // P00, P11, P02, P12 of Granny's projection
	float extra[4];      // hostDepthFlipY
};

static const char* SHADER = R"HLSL(
cbuffer P : register(b0) {
	float4 McPlanes; float4 HostPlanes; float4 WarpRow0; float4 WarpRow1; float4 WarpRow2; float4 WarpTan; float4 WarpT;
	float4 Target; float4 Opts; float4 Opts2; float4 Light; float4 HostProj; float4 Extra;
};
Texture2D HostColor : register(t4);
Texture2D McColor : register(t0);
Texture2D<float> McDepth : register(t1);
Texture2D McOverlay : register(t2);
Texture2D<float> HostDepth : register(t3);
SamplerState sLin : register(s0);
SamplerState sPt : register(s1);

void VS(uint id : SV_VertexID, out float4 pos : SV_Position, out float2 uv : TEXCOORD0) {
	uv = float2((id << 1) & 2, id & 2);
	pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

// Minecraft depth (reversed Z: 1 near, 0 = nothing drawn) -> metres
float mc_linear(float d) {
	float n = McPlanes.x, f = McPlanes.y;
	if (d <= 0.0) return 1e9;
	return n * f / (n + d * (f - n));
}
// Unity D3D11 depth (reversed Z: 1 near, 0 far) -> metres
float host_linear(float d) {
	float n = HostPlanes.x, f = HostPlanes.y;
	if (d <= 0.0) return 1e9;
	return f / ((f / n - 1.0) * d + 1.0) * HostPlanes.z;
}
float3 srgb_to_linear(float3 c) { return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4); }
float3 bands(float z) { return lerp(float3(0.1, 0.1, 0.1), float3(1.0, 0.85, 0.3), step(0.5, frac(z))) * saturate(1.5 - z / 60.0); }

float4 PS(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target {
	float zh = host_linear(HostDepth.SampleLevel(sPt, Extra.x > 0.5 ? float2(uv.x, 1.0 - uv.y) : uv, 0));
	float allow = HostPlanes.w;
	float2 muv = float2(uv.x, 1.0 - uv.y); // Minecraft's rows are bottom-up
	float zm = 1e9;
	bool inside = true;
	if (Opts.z > 0.5) {
		// march Granny's current view ray through Minecraft's (older) camera; the first point behind
		// Minecraft's surface is where Minecraft is seen
		float3 ray = float3((uv.x * 2.0 - 1.0) * WarpTan.x * WarpTan.w, (1.0 - uv.y * 2.0) * WarpTan.x, -1.0);
		float2 mcScale = float2(WarpTan.y * WarpTan.z, WarpTan.y);
		float zNear = 0.05, zFar = max(min(zh + allow, 300.0), 0.2);
		inside = false;
		[loop] for (int i = 0; i < 28; ++i) {
			float z = zNear * pow(zFar / zNear, i / 27.0);
			float3 p = ray * z;
			float3 pm = float3(dot(WarpRow0.xyz, p), dot(WarpRow1.xyz, p), dot(WarpRow2.xyz, p)) + WarpT.xyz;
			if (pm.z > -1e-3) continue;
			float2 n = pm.xy / -pm.z / mcScale;
			if (any(abs(n) > 1.0)) continue;
			float zmv = mc_linear(McDepth.SampleLevel(sPt, n * 0.5 + 0.5, 0));
			if (zmv > -pm.z + 0.03) continue;
			float zg = z;
			float2 ndc = n;
			[loop] for (int k = 0; k < 3; ++k) {
				float3 pk = ray * zg;
				float3 mk = float3(dot(WarpRow0.xyz, pk), dot(WarpRow1.xyz, pk), dot(WarpRow2.xyz, pk)) + WarpT.xyz;
				if (mk.z > -1e-3) break;
				float2 nk = mk.xy / -mk.z / mcScale;
				if (any(abs(nk) > 1.0)) break;
				float zk = mc_linear(McDepth.SampleLevel(sPt, nk * 0.5 + 0.5, 0));
				if (zk > 1e8) break;
				ndc = nk;
				float3 q = float3(nk * mcScale, -1.0) * zk - WarpT.xyz;
				zg = -(q.x * WarpRow0.z + q.y * WarpRow1.z + q.z * WarpRow2.z);
			}
			muv = ndc * 0.5 + 0.5;
			zm = zg;
			inside = true;
			break;
		}
	}
	if (!inside || Opts.z < 0.5) {
		// the same camera position: Granny's pixel -> its view direction through Granny's real projection -> where
		// Minecraft (a plain perspective with its fov and aspect) drew that direction. Identical projections: same uv.
		float2 ndc = float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0);
		float2 v = (ndc + HostProj.zw) / HostProj.xy;
		float2 n = v / float2(WarpTan.y * WarpTan.z, WarpTan.y);
		muv = n * 0.5 + 0.5; // Minecraft's rows are bottom-up
		inside = all(abs(n) <= 1.0);
	}
	if (Opts.z < 0.5 || zm > 1e8) {
		zm = mc_linear(McDepth.SampleLevel(sPt, muv, 0));
	}

	if (Opts.w > 0.5) {
		float3 c = Opts.w < 1.5 ? bands(zh) : Opts.w < 2.5 ? bands(zm) : (zm < zh ? float3(0, 1, 0) : float3(1, 0, 0)) * saturate(abs(zm - zh));
		return float4(c, 1.0);
	}

	float4 world = 0;
	if (Opts.x > 0.5 && inside && zm < zh + allow) {
		world = McColor.SampleLevel(sPt, muv, 0);
		// Minecraft drew something here (it has depth): opaque. Minecraft's colour target doesn't keep alpha at 1 for
		// solid blocks, which made them see-through over Granny's picture
		world.a = 1.0;
		// soften the hard edge where Minecraft's surface meets Granny's
		if (Opts2.z > 0.0) {
			float edge = saturate((zh + allow - zm) / max(0.02, 0.15 * Opts2.z));
			world *= edge;
		}
		world.rgb *= Opts2.w;
		if (Light.x > 0.0) {
			// Minecraft is lit like daylight: light it by Granny's own picture around this pixel instead
			// the room's light around it, not the picture right behind it (that showed through the block): Granny's
			// image averaged over a wide area (a third of the screen), heavily blurred
			float3 L = 0;
			[unroll] for (int gy = -2; gy <= 2; ++gy)
				[unroll] for (int gx = -2; gx <= 2; ++gx)
					L += HostColor.SampleLevel(sLin, uv + float2(gx * 0.08 / WarpTan.w, gy * 0.08), Light.w).rgb;
			L /= 25.0;
			float lum = max(dot(L, float3(0.2126, 0.7152, 0.0722)), 1e-3);
			float gain = clamp(lum / Light.y, Light.z, 1.1);
			float3 tint = clamp(lerp(1.0, L / lum, 0.25), 0.75, 1.3);
			world.rgb = lerp(world.rgb, world.rgb * gain * tint, Light.x);
		}
	}
	float4 ov = 0;
	if (Opts.y > 0.5) {
		ov = McOverlay.SampleLevel(sLin, float2(uv.x, 1.0 - uv.y), 0) * Opts2.x;
	}
	float4 o = ov + (1.0 - ov.a) * world;
	if (Opts2.y > 0.5) {
		// sRGB render target: Minecraft's bytes are sRGB, the hardware encodes on write
		float a = max(o.a, 1e-4);
		o.rgb = srgb_to_linear(o.rgb / a) * a;
	}
	return o;
}
)HLSL";

template <class T> static void SafeRelease(T*& p) { if (p) { p->Release(); p = nullptr; } }

static bool InitPipeline(ID3D11Device* dev) {
	if (g_vs) return true;
	if (g_initFailed) return false;
	ID3DBlob* vsb = nullptr; ID3DBlob* psb = nullptr; ID3DBlob* err = nullptr;
	HRESULT hr = D3DCompile(SHADER, strlen(SHADER), "grannycraft", nullptr, nullptr, "VS", "vs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &vsb, &err);
	if (FAILED(hr)) { Log("VS compile failed: %s", err ? (char*)err->GetBufferPointer() : "?"); g_initFailed = true; SafeRelease(err); return false; }
	hr = D3DCompile(SHADER, strlen(SHADER), "grannycraft", nullptr, nullptr, "PS", "ps_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &psb, &err);
	if (FAILED(hr)) { Log("PS compile failed: %s", err ? (char*)err->GetBufferPointer() : "?"); g_initFailed = true; SafeRelease(err); SafeRelease(vsb); return false; }
	dev->CreateVertexShader(vsb->GetBufferPointer(), vsb->GetBufferSize(), nullptr, &g_vs);
	dev->CreatePixelShader(psb->GetBufferPointer(), psb->GetBufferSize(), nullptr, &g_ps);
	SafeRelease(vsb); SafeRelease(psb);

	D3D11_BUFFER_DESC bd = {};
	bd.ByteWidth = sizeof(CB);
	bd.Usage = D3D11_USAGE_DYNAMIC;
	bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
	bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
	dev->CreateBuffer(&bd, nullptr, &g_cb);

	D3D11_SAMPLER_DESC sd = {};
	sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
	sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
	sd.MaxLOD = D3D11_FLOAT32_MAX;
	dev->CreateSamplerState(&sd, &g_sampLinear);
	sd.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
	dev->CreateSamplerState(&sd, &g_sampPoint);

	D3D11_BLEND_DESC bl = {};
	bl.RenderTarget[0].BlendEnable = TRUE;
	bl.RenderTarget[0].SrcBlend = D3D11_BLEND_ONE;
	bl.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
	bl.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
	bl.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ZERO;
	bl.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_ONE;
	bl.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
	bl.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
	dev->CreateBlendState(&bl, &g_blend);

	D3D11_DEPTH_STENCIL_DESC ds = {};
	ds.DepthEnable = FALSE;
	ds.StencilEnable = FALSE;
	dev->CreateDepthStencilState(&ds, &g_dss);

	D3D11_RASTERIZER_DESC rd = {};
	rd.FillMode = D3D11_FILL_SOLID;
	rd.CullMode = D3D11_CULL_NONE;
	rd.DepthClipEnable = TRUE;
	dev->CreateRasterizerState(&rd, &g_rs);
	Log("pipeline ready");
	return g_vs && g_ps && g_cb;
}

static void FreeMcTextures() {
	SafeRelease(g_srvColor); SafeRelease(g_srvDepth); SafeRelease(g_srvOverlay);
	SafeRelease(g_texColor); SafeRelease(g_texDepth); SafeRelease(g_texOverlay);
	g_texW = g_texH = 0;
}

static bool EnsureMcTextures(ID3D11Device* dev, int w, int h) {
	if (g_texW == w && g_texH == h && g_texColor) return true;
	FreeMcTextures();
	D3D11_TEXTURE2D_DESC td = {};
	td.Width = w; td.Height = h; td.MipLevels = 1; td.ArraySize = 1;
	td.SampleDesc.Count = 1; td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
	td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
	if (FAILED(dev->CreateTexture2D(&td, nullptr, &g_texColor))) return false;
	if (FAILED(dev->CreateTexture2D(&td, nullptr, &g_texOverlay))) return false;
	td.Format = DXGI_FORMAT_R32_FLOAT;
	if (FAILED(dev->CreateTexture2D(&td, nullptr, &g_texDepth))) return false;
	dev->CreateShaderResourceView(g_texColor, nullptr, &g_srvColor);
	dev->CreateShaderResourceView(g_texOverlay, nullptr, &g_srvOverlay);
	dev->CreateShaderResourceView(g_texDepth, nullptr, &g_srvDepth);
	g_texW = w; g_texH = h;
	Log("minecraft textures %dx%d", w, h);
	return true;
}

static DXGI_FORMAT DepthSrvFormat(DXGI_FORMAT f) {
	switch (f) {
	case DXGI_FORMAT_R32_TYPELESS: case DXGI_FORMAT_D32_FLOAT: case DXGI_FORMAT_R32_FLOAT: return DXGI_FORMAT_R32_FLOAT;
	case DXGI_FORMAT_R24G8_TYPELESS: case DXGI_FORMAT_D24_UNORM_S8_UINT: return DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
	case DXGI_FORMAT_R32G8X24_TYPELESS: case DXGI_FORMAT_D32_FLOAT_S8X24_UINT: return DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;
	case DXGI_FORMAT_R16_TYPELESS: case DXGI_FORMAT_D16_UNORM: return DXGI_FORMAT_R16_UNORM;
	default: return f;
	}
}

static bool EnsureHostDepth(ID3D11Device* dev, ID3D11Texture2D* tex) {
	if (tex == g_hostDepthTex && g_srvHostDepth) return true;
	SafeRelease(g_srvHostDepth);
	g_hostDepthTex = tex;
	if (!tex) return false;
	D3D11_TEXTURE2D_DESC td;
	tex->GetDesc(&td);
	g_stats.depthFormat = td.Format;
	D3D11_SHADER_RESOURCE_VIEW_DESC sd = {};
	sd.Format = DepthSrvFormat(td.Format);
	if (td.SampleDesc.Count > 1) {
		sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2DMS;
	} else {
		sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
		sd.Texture2D.MipLevels = 1;
	}
	HRESULT hr = dev->CreateShaderResourceView(tex, &sd, &g_srvHostDepth);
	if (FAILED(hr)) {
		Log("host depth SRV failed fmt=%d hr=%08x", (int)td.Format, (unsigned)hr);
		g_stats.lastError = hr;
		return false;
	}
	Log("host depth %ux%u fmt=%d samples=%u", td.Width, td.Height, (int)td.Format, td.SampleDesc.Count);
	return true;
}

// ---- Granny's own depth buffer, copied right after her opaque geometry is drawn ----
// _CameraDepthTexture comes from a separate pass that only draws materials with a ShadowCaster pass: it doesn't match
// the picture everywhere (Minecraft then showed through parts of the house, or was cut by things nobody sees). The
// depth buffer the picture itself was drawn with matches it exactly.
static ID3D11Texture2D* g_sceneDepth = nullptr;
static ID3D11ShaderResourceView* g_srvSceneDepth = nullptr;
static D3D11_TEXTURE2D_DESC g_sceneDesc = {};
static bool g_sceneFresh = false;         // grabbed this frame, not composited yet
static std::atomic<int> g_depthSource{1}; // 1: the scene's depth buffer, 0: _CameraDepthTexture
static int g_sceneWarned = 0;

static DXGI_FORMAT TypelessDepth(DXGI_FORMAT f) {
	switch (f) {
	case DXGI_FORMAT_D32_FLOAT: case DXGI_FORMAT_R32_TYPELESS: case DXGI_FORMAT_R32_FLOAT: return DXGI_FORMAT_R32_TYPELESS;
	case DXGI_FORMAT_D24_UNORM_S8_UINT: case DXGI_FORMAT_R24G8_TYPELESS: return DXGI_FORMAT_R24G8_TYPELESS;
	case DXGI_FORMAT_D32_FLOAT_S8X24_UINT: case DXGI_FORMAT_R32G8X24_TYPELESS: return DXGI_FORMAT_R32G8X24_TYPELESS;
	case DXGI_FORMAT_D16_UNORM: case DXGI_FORMAT_R16_TYPELESS: return DXGI_FORMAT_R16_TYPELESS;
	default: return DXGI_FORMAT_UNKNOWN;
	}
}

static void GrabSceneDepth(ID3D11DeviceContext* ctx) {
	g_sceneFresh = false;
	if (!g_dev || g_depthSource.load() == 0) return;
	ID3D11RenderTargetView* rtv = nullptr;
	ID3D11DepthStencilView* dsv = nullptr;
	ctx->OMGetRenderTargets(1, &rtv, &dsv);
	SafeRelease(rtv);
	if (!dsv) {
		if (!(g_sceneWarned & 1)) { g_sceneWarned |= 1; Log("scene depth: no depth buffer bound, using _CameraDepthTexture"); }
		return;
	}
	ID3D11Resource* res = nullptr;
	dsv->GetResource(&res);
	dsv->Release();
	D3D11_TEXTURE2D_DESC td;
	((ID3D11Texture2D*)res)->GetDesc(&td);
	DXGI_FORMAT typeless = TypelessDepth(td.Format);
	if (td.SampleDesc.Count != 1 || td.ArraySize != 1 || typeless == DXGI_FORMAT_UNKNOWN) {
		if (!(g_sceneWarned & 2)) { g_sceneWarned |= 2; Log("scene depth: fmt=%d samples=%u not copyable, using _CameraDepthTexture", (int)td.Format, td.SampleDesc.Count); }
		res->Release();
		return;
	}
	if (!g_sceneDepth || g_sceneDesc.Width != td.Width || g_sceneDesc.Height != td.Height || g_sceneDesc.Format != td.Format) {
		SafeRelease(g_srvSceneDepth);
		SafeRelease(g_sceneDepth);
		D3D11_TEXTURE2D_DESC d = {};
		d.Width = td.Width; d.Height = td.Height; d.MipLevels = 1; d.ArraySize = 1; d.Format = typeless;
		d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
		D3D11_SHADER_RESOURCE_VIEW_DESC sd = {};
		sd.Format = DepthSrvFormat(typeless); sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D; sd.Texture2D.MipLevels = 1;
		if (FAILED(g_dev->CreateTexture2D(&d, nullptr, &g_sceneDepth)) || FAILED(g_dev->CreateShaderResourceView(g_sceneDepth, &sd, &g_srvSceneDepth))) {
			SafeRelease(g_srvSceneDepth);
			SafeRelease(g_sceneDepth);
			if (!(g_sceneWarned & 4)) { g_sceneWarned |= 4; Log("scene depth: copy texture failed (fmt=%d), using _CameraDepthTexture", (int)td.Format); }
			res->Release();
			return;
		}
		g_sceneDesc = td;
		Log("scene depth %ux%u fmt=%d", td.Width, td.Height, (int)td.Format);
	}
	// a depth-stencil subresource is copied whole (no box)
	ctx->CopySubresourceRegion(g_sceneDepth, 0, 0, 0, 0, res, 0, nullptr);
	g_sceneFresh = true;
	res->Release();
}

// Pulls a Minecraft frame into the upload textures: the one Granny's camera was placed for if it is still intact,
// else the newest. Returns true if there is a frame to show.
static int g_lastSlot = -1;
static int64_t g_lastSeq = -1;

static bool UploadLatest(ID3D11DeviceContext* ctx, int wantSlot, int64_t wantSeq) {
	if (!OpenShm()) { g_stats.shmOpen = 0; return false; }
	g_stats.shmOpen = 1;
	uint8_t* m = g_view;
	int64_t publish = *(volatile int64_t*)(m + 32);
	g_stats.published = publish;
	g_stats.mcPid = *(int32_t*)(m + 44);
	int slots = *(int32_t*)(m + 12);
	int slot = wantSlot;
	int64_t seq = -1;
	if (slot >= 0 && slot < slots) {
		seq = *(volatile int64_t*)(m + SLOT_DESC + SLOT_DESC_BYTES * slot);
		if (seq != wantSeq) slot = -1; // rewritten since: take the newest
	} else {
		slot = -1;
	}
	if (slot < 0) {
		slot = *(volatile int32_t*)(m + 40);
		if (slot < 0 || slot >= slots) return g_texColor != nullptr;
		seq = *(volatile int64_t*)(m + SLOT_DESC + SLOT_DESC_BYTES * slot);
	}
	if (slot == g_lastSlot && seq == g_lastSeq) return g_texColor != nullptr; // already up there
	int64_t stride = *(int64_t*)(m + 16);
	int header = *(int32_t*)(m + 8);
	uint8_t* d = m + SLOT_DESC + SLOT_DESC_BYTES * slot;
	if (seq & 1) return g_texColor != nullptr;
	MemoryBarrier();
	int w = *(int32_t*)(d + 24), h = *(int32_t*)(d + 28);
	if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return g_texColor != nullptr;
	if (!EnsureMcTextures(g_dev, w, h)) return false;
	uint8_t* base = m + header + stride * slot;
	size_t n = (size_t)w * h * 4;
	ctx->UpdateSubresource(g_texColor, 0, nullptr, base, w * 4, 0);
	ctx->UpdateSubresource(g_texDepth, 0, nullptr, base + n, w * 4, 0);
	ctx->UpdateSubresource(g_texOverlay, 0, nullptr, base + 2 * n, w * 4, 0);
	g_mcPose.nearP = *(float*)(d + 32);
	g_mcPose.farP = *(float*)(d + 36);
	g_mcPose.fov = *(float*)(d + 40);
	g_mcPose.flags = *(int32_t*)(d + 44);
	g_mcPose.x = *(double*)(d + 48);
	g_mcPose.y = *(double*)(d + 56);
	g_mcPose.z = *(double*)(d + 64);
	g_mcPose.yaw = *(float*)(d + 72);
	g_mcPose.pitch = *(float*)(d + 76);
	g_mcPose.roll = *(float*)(d + 80);
	MemoryBarrier();
	if (*(volatile int64_t*)d != seq) {
		// overwritten while we read it: show it anyway, the next one comes right after
	}
	g_lastPublish = publish;
	g_lastSlot = slot;
	g_lastSeq = seq;
	g_stats.uploaded++;
	g_stats.width = w; g_stats.height = h;
	g_stats.mcX = g_mcPose.x; g_stats.mcY = g_mcPose.y; g_stats.mcZ = g_mcPose.z;
	g_stats.mcYaw = g_mcPose.yaw; g_stats.mcPitch = g_mcPose.pitch; g_stats.mcFov = g_mcPose.fov; g_stats.mcFar = g_mcPose.farP;
	return true;
}

// Minecraft camera basis for yaw/pitch (degrees): rows = camera right, up, back(+Z of camera space = -forward)
// in Minecraft world space. Minecraft yaw 0 looks +Z, yaw 90 looks -X; pitch positive looks down.
static void Basis(float yawDeg, float pitchDeg, double R[3][3]) {
	double y = yawDeg * 3.14159265358979 / 180.0, p = pitchDeg * 3.14159265358979 / 180.0;
	double fwd[3] = { -sin(y) * cos(p), -sin(p), cos(y) * cos(p) };
	// right = forward x up(0,1,0) in a right-handed system... Minecraft: facing +Z, +X is on the left.
	double right[3] = { -cos(y), 0.0, -sin(y) };
	double up[3] = {
		right[1] * fwd[2] - right[2] * fwd[1],
		right[2] * fwd[0] - right[0] * fwd[2],
		right[0] * fwd[1] - right[1] * fwd[0] };
	for (int i = 0; i < 3; i++) { R[0][i] = right[i]; R[1][i] = up[i]; R[2][i] = -fwd[i]; }
}

static void Composite(ID3D11DeviceContext* ctx, const Params& p) {
	ID3D11RenderTargetView* rtv = nullptr;
	ctx->OMGetRenderTargets(1, &rtv, nullptr);
	if (!rtv) { g_stats.lastError = 2; return; }
	ID3D11Resource* rtres = nullptr;
	rtv->GetResource(&rtres);
	D3D11_RENDER_TARGET_VIEW_DESC rdesc;
	rtv->GetDesc(&rdesc);
	g_stats.rtvFormat = rdesc.Format;
	D3D11_TEXTURE2D_DESC td = {};
	((ID3D11Texture2D*)rtres)->GetDesc(&td);
	g_stats.targetW = td.Width; g_stats.targetH = td.Height;
	if (p.lightMatch > 0.0f) CopyHostColor(ctx, (ID3D11Texture2D*)rtres, td);
	bool srgb = rdesc.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || rdesc.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
	bool floatTarget = rdesc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT || rdesc.Format == DXGI_FORMAT_R11G11B10_FLOAT;

	CB cb = {};
	cb.mcPlanes[0] = g_mcPose.nearP; cb.mcPlanes[1] = g_mcPose.farP; cb.mcPlanes[2] = (float)g_mcPose.flags;
	cb.hostPlanes[0] = p.hostNear; cb.hostPlanes[1] = p.hostFar; cb.hostPlanes[2] = p.hostDepthScale; cb.hostPlanes[3] = p.depthBias;
	// R_mc * R_host^T maps a host camera-space ray into Minecraft camera space; plus the camera offset
	double Rm[3][3], Rh[3][3];
	Basis(g_mcPose.yaw, g_mcPose.pitch, Rm);
	Basis(p.yaw, p.pitch, Rh);
	double W[3][3];
	for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) {
		W[i][j] = 0;
		for (int k = 0; k < 3; k++) W[i][j] += Rm[i][k] * Rh[j][k];
	}
	double dt[3] = { p.camX - g_mcPose.x, p.camY - g_mcPose.y, p.camZ - g_mcPose.z };
	float* rows[3] = { cb.warpRow0, cb.warpRow1, cb.warpRow2 };
	for (int i = 0; i < 3; i++) {
		rows[i][0] = (float)W[i][0]; rows[i][1] = (float)W[i][1]; rows[i][2] = (float)W[i][2];
		cb.warpT[i] = (float)(Rm[i][0] * dt[0] + Rm[i][1] * dt[1] + Rm[i][2] * dt[2]);
	}
	cb.warpTan[0] = p.hostTanHalfFov;
	cb.warpTan[1] = (float)tan(g_mcPose.fov * 3.14159265358979 / 360.0);
	cb.warpTan[2] = (float)g_texW / (float)(g_texH ? g_texH : 1);
	cb.warpTan[3] = p.hostAspect;
	cb.target[0] = (float)td.Width; cb.target[1] = (float)td.Height; cb.target[2] = 1.0f / td.Width; cb.target[3] = 1.0f / td.Height;
	cb.opts[0] = (float)p.showWorld; cb.opts[1] = (float)p.showOverlay; cb.opts[2] = (float)p.reproject; cb.opts[3] = (float)p.debugView;
	cb.light[0] = g_srvHostColor ? p.lightMatch : 0.0f; cb.light[1] = p.lightRef; cb.light[2] = p.lightMin; cb.light[3] = p.lightMip;
	cb.extra[0] = p.hostDepthFlipY ? 1.0f : 0.0f;
	if (p.hostProj[0] > 0.0f && p.hostProj[1] > 0.0f) {
		for (int i = 0; i < 4; i++) cb.hostProj[i] = p.hostProj[i];
	} else {
		// a plain perspective from Granny's fov and aspect
		cb.hostProj[1] = 1.0f / p.hostTanHalfFov;
		cb.hostProj[0] = cb.hostProj[1] / p.hostAspect;
	}
	cb.opts2[0] = p.overlayOpacity; cb.opts2[1] = (srgb || floatTarget) ? 1.0f : 0.0f; cb.opts2[2] = p.edgeSoftness; cb.opts2[3] = p.brightness;
	D3D11_MAPPED_SUBRESOURCE ms;
	if (SUCCEEDED(ctx->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &ms))) {
		memcpy(ms.pData, &cb, sizeof cb);
		ctx->Unmap(g_cb, 0);
	}

	// save the state we change
	ID3D11VertexShader* oVS; ID3D11PixelShader* oPS; ID3D11GeometryShader* oGS; ID3D11HullShader* oHS; ID3D11DomainShader* oDS;
	ctx->VSGetShader(&oVS, nullptr, nullptr); ctx->PSGetShader(&oPS, nullptr, nullptr); ctx->GSGetShader(&oGS, nullptr, nullptr);
	ctx->HSGetShader(&oHS, nullptr, nullptr); ctx->DSGetShader(&oDS, nullptr, nullptr);
	ID3D11ShaderResourceView* oSRV[5]; ctx->PSGetShaderResources(0, 5, oSRV);
	ID3D11SamplerState* oSamp[2]; ctx->PSGetSamplers(0, 2, oSamp);
	ID3D11Buffer* oCB; ctx->PSGetConstantBuffers(0, 1, &oCB);
	ID3D11BlendState* oBlend; float oBF[4]; UINT oMask; ctx->OMGetBlendState(&oBlend, oBF, &oMask);
	ID3D11DepthStencilState* oDSS; UINT oRef; ctx->OMGetDepthStencilState(&oDSS, &oRef);
	ID3D11RasterizerState* oRS; ctx->RSGetState(&oRS);
	UINT nvp = 1; D3D11_VIEWPORT oVP; ctx->RSGetViewports(&nvp, &oVP);
	ID3D11InputLayout* oIL; ctx->IAGetInputLayout(&oIL);
	D3D11_PRIMITIVE_TOPOLOGY oTopo; ctx->IAGetPrimitiveTopology(&oTopo);
	ID3D11RenderTargetView* oRTV = nullptr; ID3D11DepthStencilView* oDSV = nullptr; ctx->OMGetRenderTargets(1, &oRTV, &oDSV);

	ctx->OMSetRenderTargets(1, &rtv, nullptr);
	D3D11_VIEWPORT vp = { 0, 0, (float)td.Width, (float)td.Height, 0, 1 };
	ctx->RSSetViewports(1, &vp);
	ctx->RSSetState(g_rs);
	ctx->OMSetBlendState(g_blend, nullptr, 0xffffffff);
	ctx->OMSetDepthStencilState(g_dss, 0);
	ctx->IASetInputLayout(nullptr);
	ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
	ctx->VSSetShader(g_vs, nullptr, 0);
	ctx->PSSetShader(g_ps, nullptr, 0);
	ctx->GSSetShader(nullptr, nullptr, 0); ctx->HSSetShader(nullptr, nullptr, 0); ctx->DSSetShader(nullptr, nullptr, 0);
	ID3D11ShaderResourceView* hostDepth = g_sceneFresh && g_srvSceneDepth && g_depthSource.load() != 0 ? g_srvSceneDepth : g_srvHostDepth;
	g_sceneFresh = false;
	ID3D11ShaderResourceView* srvs[5] = { g_srvColor, g_srvDepth, g_srvOverlay, hostDepth, g_srvHostColor };
	ctx->PSSetShaderResources(0, 5, srvs);
	ID3D11SamplerState* samps[2] = { g_sampLinear, g_sampPoint };
	ctx->PSSetSamplers(0, 2, samps);
	ctx->PSSetConstantBuffers(0, 1, &g_cb);
	ctx->Draw(3, 0);
	g_stats.composited++;

	// restore
	ID3D11ShaderResourceView* nulls[5] = {};
	ctx->PSSetShaderResources(0, 5, nulls);
	ctx->OMSetRenderTargets(1, &oRTV, oDSV);
	ctx->VSSetShader(oVS, nullptr, 0); ctx->PSSetShader(oPS, nullptr, 0); ctx->GSSetShader(oGS, nullptr, 0);
	ctx->HSSetShader(oHS, nullptr, 0); ctx->DSSetShader(oDS, nullptr, 0);
	ctx->PSSetShaderResources(0, 5, oSRV); ctx->PSSetSamplers(0, 2, oSamp); ctx->PSSetConstantBuffers(0, 1, &oCB);
	ctx->OMSetBlendState(oBlend, oBF, oMask); ctx->OMSetDepthStencilState(oDSS, oRef); ctx->RSSetState(oRS);
	if (nvp) ctx->RSSetViewports(1, &oVP);
	ctx->IASetInputLayout(oIL); ctx->IASetPrimitiveTopology(oTopo);
	rtres->Release();
	IUnknown* rel[] = { oVS, oPS, oGS, oHS, oDS, oSRV[0], oSRV[1], oSRV[2], oSRV[3], oSRV[4], oSamp[0], oSamp[1], oCB, oBlend, oDSS, oRS, oIL, oRTV, oDSV };
	for (IUnknown* u : rel) if (u) u->Release();
	rtv->Release();
}

// ---- capture of the composited frame (testing): written as a 32-bit BMP after the composite pass ----
static char g_capturePath[512];
static char g_captureQueue[8][512];
static int g_captureCount = 0;
static std::mutex g_captureLock;

static void CaptureTarget(ID3D11DeviceContext* ctx) {
	ID3D11RenderTargetView* rtv = nullptr;
	ctx->OMGetRenderTargets(1, &rtv, nullptr);
	if (!rtv) return;
	ID3D11Resource* res = nullptr;
	rtv->GetResource(&res);
	rtv->Release();
	ID3D11Texture2D* tex = (ID3D11Texture2D*)res;
	D3D11_TEXTURE2D_DESC td;
	tex->GetDesc(&td);
	D3D11_TEXTURE2D_DESC sd = td;
	sd.BindFlags = 0; sd.MiscFlags = 0; sd.Usage = D3D11_USAGE_STAGING; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ; sd.MipLevels = 1; sd.ArraySize = 1;
	sd.SampleDesc.Count = 1; sd.SampleDesc.Quality = 0;
	ID3D11Texture2D* staging = nullptr;
	if (td.SampleDesc.Count == 1 && SUCCEEDED(g_dev->CreateTexture2D(&sd, nullptr, &staging))) {
		ctx->CopySubresourceRegion(staging, 0, 0, 0, 0, tex, 0, nullptr);
		D3D11_MAPPED_SUBRESOURCE m;
		if (SUCCEEDED(ctx->Map(staging, 0, D3D11_MAP_READ, 0, &m))) {
			FILE* f = nullptr;
			fopen_s(&f, g_capturePath, "wb");
			if (f) {
				bool bgr = td.Format == DXGI_FORMAT_B8G8R8A8_UNORM || td.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB || td.Format == DXGI_FORMAT_B8G8R8A8_TYPELESS;
				uint32_t w = td.Width, h = td.Height, row = w * 4, size = 54 + row * h;
				uint8_t hdr[54] = { 'B', 'M' };
				*(uint32_t*)(hdr + 2) = size; *(uint32_t*)(hdr + 10) = 54; *(uint32_t*)(hdr + 14) = 40;
				*(int32_t*)(hdr + 18) = (int32_t)w; *(int32_t*)(hdr + 22) = -(int32_t)h; *(uint16_t*)(hdr + 26) = 1; *(uint16_t*)(hdr + 28) = 32;
				fwrite(hdr, 1, 54, f);
				uint8_t* line = new uint8_t[row];
				for (uint32_t y = 0; y < h; y++) {
					uint8_t* src = (uint8_t*)m.pData + (size_t)m.RowPitch * y;
					for (uint32_t x = 0; x < w; x++) {
						uint8_t* p = src + x * 4;
						line[x * 4 + 0] = bgr ? p[0] : p[2]; line[x * 4 + 1] = p[1]; line[x * 4 + 2] = bgr ? p[2] : p[0]; line[x * 4 + 3] = 255;
					}
					fwrite(line, 1, row, f);
				}
				delete[] line;
				fclose(f);
				Log("captured %s (%ux%u fmt %d)", g_capturePath, w, h, (int)td.Format);
			}
			ctx->Unmap(staging, 0);
		}
		staging->Release();
	}
	res->Release();
}

EXPORT void GC_RequestCapture(const char* path) {
	std::lock_guard<std::mutex> lock(g_captureLock);
	if (g_captureCount < 8) strncpy_s(g_captureQueue[g_captureCount++], path, 511);
}

static bool NextCapture() {
	std::lock_guard<std::mutex> lock(g_captureLock);
	if (g_captureCount == 0) return false;
	strcpy_s(g_capturePath, g_captureQueue[0]);
	for (int i = 1; i < g_captureCount; i++) strcpy_s(g_captureQueue[i - 1], g_captureQueue[i]);
	g_captureCount--;
	return true;
}

// event GC_GRAB_DEPTH | slot: right after Granny's opaque geometry (CameraEvent.AfterForwardOpaque), copy her depth
static const int GC_GRAB_DEPTH = 0x100;

static void __stdcall OnRenderEvent(int eventId) {
	if (eventId & GC_GRAB_DEPTH) {
		if (!g_dev) return; // the first composite finds the device
		ID3D11DeviceContext* gctx = nullptr;
		g_dev->GetImmediateContext(&gctx);
		if (gctx) { GrabSceneDepth(gctx); gctx->Release(); }
		return;
	}
	Params p;
	{
		std::lock_guard<std::mutex> lock(g_paramLock);
		p = g_params[eventId & 7];
	}
	if (!p.enabled || !p.hostDepthTex) return;
	ID3D11Texture2D* depth = (ID3D11Texture2D*)p.hostDepthTex;
	if (!g_dev) {
		depth->GetDevice(&g_dev);
		if (!g_dev) return;
	}
	ID3D11DeviceContext* ctx = nullptr;
	g_dev->GetImmediateContext(&ctx);
	if (!ctx) return;
	if (InitPipeline(g_dev) && EnsureHostDepth(g_dev, depth) && UploadLatest(ctx, p.wantSlot, p.wantSeq)) {
		Composite(ctx, p);
	}
	if (NextCapture()) CaptureTarget(ctx); // one per frame
	ctx->Release();
}

EXPORT void* GC_GetRenderEventFunc() { return (void*)OnRenderEvent; }

// the camera's command buffer issues event `slot`; the plugin fills that slot during the same frame
EXPORT void GC_SetParams(int slot, const Params* p) {
	std::lock_guard<std::mutex> lock(g_paramLock);
	g_params[slot & 7] = *p;
}

// the newest complete Minecraft frame's camera (Minecraft coordinates), read on Unity's main thread
EXPORT void GC_PeekLatest(Peek* out) {
	memset(out, 0, sizeof *out);
	if (!OpenShm()) return;
	uint8_t* m = g_view;
	int slots = *(int32_t*)(m + 12);
	int slot = *(volatile int32_t*)(m + 40);
	if (slot < 0 || slot >= slots) return;
	uint8_t* d = m + SLOT_DESC + SLOT_DESC_BYTES * slot;
	int64_t seq = *(volatile int64_t*)d;
	if (seq & 1) return;
	MemoryBarrier();
	out->x = *(double*)(d + 48); out->y = *(double*)(d + 56); out->z = *(double*)(d + 64);
	out->yaw = *(float*)(d + 72); out->pitch = *(float*)(d + 76); out->roll = *(float*)(d + 80);
	out->fov = *(float*)(d + 40); out->firstPerson = *(int32_t*)(d + 84);
	MemoryBarrier();
	if (*(volatile int64_t*)d != seq) return; // rewritten while reading
	out->slot = slot; out->seq = seq; out->publish = *(volatile int64_t*)(m + 32); out->ok = 1;
}

EXPORT int GC_PeekSize() { return (int)sizeof(Peek); }

// 1: test Minecraft against the depth buffer Granny's picture was drawn with (default), 0: against _CameraDepthTexture
EXPORT void GC_SetDepthSource(int source) { g_depthSource.store(source); }

EXPORT int GC_ParamsSize() { return (int)sizeof(Params); }
EXPORT int GC_StatsSize() { return (int)sizeof(Stats); }

EXPORT void GC_GetStats(Stats* out) { *out = g_stats; }

// copies (and clears) the log; returns its length
EXPORT int GC_TakeLog(char* buf, int cap) {
	std::lock_guard<std::mutex> lock(g_paramLock);
	int n = (int)strlen(g_log);
	if (n >= cap) n = cap - 1;
	memcpy(buf, g_log, n);
	buf[n] = 0;
	g_log[0] = 0;
	return n;
}

// a different render target size or depth texture (resolution change): forget the cached views
EXPORT void GC_Reset() {
	std::lock_guard<std::mutex> lock(g_paramLock);
	g_hostDepthTex = nullptr;
}

BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID) {
	if (reason == DLL_PROCESS_DETACH) CloseShm();
	return TRUE;
}
