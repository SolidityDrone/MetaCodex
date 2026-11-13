// MetaCodex: the room's depth made sharper and steadier, once per new depth frame and eye (ShellAmbient.RecordSharpDepth),
// for the Ambient Focus dome and for real things in front of the UI (McxOcclusion.hlsl). Output: linear depth in metres
// (0 = no reading) in the depth map's own uv (so Meta's matrices still apply): 1.5 x the sensor's resolution when the
// camera guides it ("Sharp depth" on: 480 x 480 from 320 x 320), the sensor's own resolution without the camera.
//   1. The 2 x 2 sensor readings around each output texel (Meta's depth: (ndc + 1) / 2, linearised with
//      _EnvironmentDepthZBufferParams); the nearest of them is the reference surface (2026-11-13: before, the median of
//      3 x 3 at 2 x the resolution, about 6 x the work: GPU 98 % busy on the headset).
//   2. MCX_GUIDE, where the passthrough camera sees the point: joint bilateral upsampling (Kopf 2007) guided by the
//      camera's picture - each reading weighs by how close it is (bilinear) and by how alike its colour is to this
//      texel's colour (both projected into the camera through one matrix per eye, depth clip -> camera), so depth
//      edges move to the picture's edges (SandyBridge docs/DEPTH_UPSCALE_PLAN.md, option A). Elsewhere: an
//      edge-preserving filter on depth alone (readings far from the reference count little), so an edge stays crisp.
//   3. Temporal: the point is looked up in last frame's output (one matrix per eye, depth clip -> last frame's depth
//      clip); where both agree within 2 cm + 2 % the result moves towards it by _McxHistory (sensor noise averages
//      out, the edge stops flickering); where they do not (something moved), the new reading wins at once.
Shader "Hidden/MetaCodex/DepthSharp"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            ZWrite Off ZTest Always Cull Off Blend Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local __ MCX_GUIDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            Texture2DArray _EnvironmentDepthTexture;
            float4x4 _EnvironmentDepthReprojectionMatrices[2];
            float4 _EnvironmentDepthZBufferParams;
            float4 _McxDepthSize;           // the sensor's width, height, 1 / width, 1 / height
            float _McxSlice;                // the eye written by this draw
            Texture2DArray _McxSharpPrev;   // last frame's output
            SamplerState sampler_McxSharpPrev;
            float4x4 _McxDepthToPrev[2];    // this frame's depth clip -> last frame's depth clip, per eye
            float _McxHistory;              // 0 = no temporal filter
            #if defined(MCX_GUIDE)
            Texture2D _McxCamTex;
            SamplerState sampler_McxCamTex;
            float4x4 _McxDepthToCam[2];     // depth clip -> the camera's space, per eye
            float4 _McxCamIntr;             // focal length xy, principal point zw (sensor pixels)
            float4 _McxCamCrop;             // the sensor region of the picture
            #endif

            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(uint id : SV_VertexID)
            {
                float2 p = float2(id == 1 ? 3.0 : -1.0, id == 2 ? 3.0 : -1.0); // one triangle over the target
                Varyings o;
                o.positionCS = float4(p, 0.0, 1.0);
                o.uv = p * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                o.uv.y = 1.0 - o.uv.y; // the target's rows in the depth map's order
                #endif
                return o;
            }

            float Linear(float raw)
            {
                float ndc = raw * 2.0 - 1.0;
                return ndc >= 0.9999 ? 0.0 : _EnvironmentDepthZBufferParams.x / (ndc + _EnvironmentDepthZBufferParams.y);
            }

            float NdcOf(float lin) { return _EnvironmentDepthZBufferParams.x / lin - _EnvironmentDepthZBufferParams.y; }

            #if defined(MCX_GUIDE)
            bool CamUv(float4 clip, uint eye, out float2 uvc)
            {
                float4 p = mul(_McxDepthToCam[eye], clip);
                uvc = 0;
                if (abs(p.w) < 1e-6) return false;
                float3 pc = p.xyz / p.w;
                if (pc.z < 0.05) return false;
                float2 s = pc.xy / pc.z * _McxCamIntr.xy + _McxCamIntr.zw;
                uvc = (s - _McxCamCrop.xy) / _McxCamCrop.zw;
                return all(uvc > 0.003) && all(uvc < 0.997);
            }
            float3 CamRgb(float2 uvc) { return _McxCamTex.SampleLevel(sampler_McxCamTex, uvc, 0).rgb; }
            #endif

            float frag(Varyings i) : SV_Target
            {
                uint eye = (uint)(_McxSlice + 0.5);
                int2 size = (int2)_McxDepthSize.xy;
                float2 st = i.uv * _McxDepthSize.xy - 0.5; // sensor texel coordinates (centres on integers)
                int2 b = (int2)floor(st);                  // the cell's first reading
                float2 f = st - (float2)b;                 // where this texel sits in the cell, 0..1
                int nearest = (f.x >= 0.5 ? 1 : 0) + (f.y >= 0.5 ? 2 : 0);
                float z[4], nd[4], ws[4];
                int n = 0;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    int2 o = int2(k & 1, k >> 1);
                    int2 t = clamp(b + o, int2(0, 0), size - 1);
                    float raw = _EnvironmentDepthTexture.Load(int4(t, eye, 0)).r;
                    nd[k] = raw * 2.0 - 1.0;
                    z[k] = Linear(raw);
                    n += z[k] > 0.0 ? 1 : 0;
                    float2 wb = lerp(1.0 - f, f, (float2)o);   // bilinear: the nearer reading counts more
                    ws[k] = wb.x * wb.y + 1e-4;
                }
                if (n == 0) return 0.0;
                // the reference surface: the nearest reading, else the first valid one
                float zr = 0.0;
                [unroll] for (int r = 0; r < 4; r++) if (r == nearest) zr = z[r];
                [unroll] for (int v = 0; v < 4; v++) if (zr <= 0.0 && z[v] > 0.0) zr = z[v];
                #if defined(MCX_GUIDE)
                float2 uvc0;
                bool guided = CamUv(float4(i.uv * 2.0 - 1.0, NdcOf(zr), 1.0), eye, uvc0);
                float3 c0 = guided ? CamRgb(uvc0) : 0;
                #endif
                float wsum = 0.0, zsum = 0.0;
                [unroll] for (int j = 0; j < 4; j++)
                {
                    if (z[j] <= 0.0) continue;
                    float w = ws[j];
                    float dz = (z[j] - zr) / (0.02 + 0.02 * zr);
                    #if defined(MCX_GUIDE)
                    if (guided)
                    {
                        int2 t = clamp(b + int2(j & 1, j >> 1), int2(0, 0), size - 1);
                        float2 uvk;
                        float3 ck = CamUv(float4(((float2)t + 0.5) * _McxDepthSize.zw * 2.0 - 1.0, nd[j], 1.0), eye, uvk) ? CamRgb(uvk) : c0;
                        float3 dc = ck - c0;
                        // the picture decides which side of an edge this texel is on; a weak depth term keeps
                        // a colour match far off the surface (a white box before a white wall) from pulling
                        w *= exp(-dot(dc, dc) * 78.0) * (0.25 + 0.75 * exp(-0.125 * dz * dz)); // colour sigma 0.08
                    }
                    else
                    #endif
                    w *= exp(-0.5 * dz * dz); // depth alone: readings off the reference surface count little
                    w += 1e-7;
                    wsum += w;
                    zsum += w * z[j];
                }
                float zo = zsum / wsum;
                // temporal: towards last frame's output where it agrees
                if (_McxHistory > 0.0)
                {
                    float4 q = mul(_McxDepthToPrev[eye], float4(i.uv * 2.0 - 1.0, NdcOf(zo), 1.0));
                    if (q.w > 1e-5)
                    {
                        float2 uvp = (q.xy / q.w + 1.0) * 0.5;
                        if (all(uvp > 0.0) && all(uvp < 1.0))
                        {
                            float zexp = _EnvironmentDepthZBufferParams.x / (q.z / q.w + _EnvironmentDepthZBufferParams.y);
                            float zh = _McxSharpPrev.SampleLevel(sampler_McxSharpPrev, float3(uvp, eye), 0).r;
                            if (zh > 0.01 && abs(zh - zexp) < 0.02 + 0.02 * zo) zo += _McxHistory * (zh - zexp);
                        }
                    }
                }
                return zo;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
