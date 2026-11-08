// MetaCodex: the room's depth made sharper and steadier, once a frame per eye (ShellAmbient.RunSharpDepth), for the
// Ambient Focus dome and for real things in front of the UI (McxOcclusion.hlsl). Output: linear depth in metres
// (0 = no reading), twice the sensor's resolution, in the depth map's own uv (so Meta's matrices still apply).
//   1. The 3 x 3 sensor readings around each output texel (Meta's depth: (ndc + 1) / 2, linearised with
//      _EnvironmentDepthZBufferParams) and their median, the surface most of the neighbourhood lies on.
//   2. MCX_GUIDE, where the passthrough camera sees the point: joint bilateral upsampling (Kopf 2007) guided by the
//      camera's picture - each reading weighs by how close it is and by how alike its colour is to this texel's
//      colour (both projected into the camera through its pose and intrinsics), so depth edges move to the picture's
//      edges (SandyBridge docs/DEPTH_UPSCALE_PLAN.md, option A). Elsewhere: an edge-preserving filter on depth alone
//      (readings far from the median count little), so an edge stays crisp instead of smeared.
//   3. Temporal: the point is looked up in last frame's output through last frame's matrices; where both agree
//      within 2 cm + 2 % the result moves towards it by _McxHistory (sensor noise averages out, the edge stops
//      flickering); where they do not (something moved), the new reading wins at once.
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
            float4x4 _McxDepthToWorld[2];
            float4 _McxDepthSize;           // the sensor's width, height, 1 / width, 1 / height
            float _McxSlice;                // the eye written by this draw
            Texture2DArray _McxSharpPrev;   // last frame's output
            SamplerState sampler_McxSharpPrev;
            float4x4 _McxPrevWorldToDepth[2];
            float _McxHistory;              // 0 = no temporal filter
            #if defined(MCX_GUIDE)
            Texture2D _McxCamTex;
            SamplerState sampler_McxCamTex;
            float4x4 _McxCamWorldToCam;
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

            float3 WorldOf(float2 uv, float lin, uint eye)
            {
                float ndc = _EnvironmentDepthZBufferParams.x / lin - _EnvironmentDepthZBufferParams.y;
                float4 w = mul(_McxDepthToWorld[eye], float4(uv * 2.0 - 1.0, ndc, 1.0));
                return w.xyz / w.w;
            }

            #if defined(MCX_GUIDE)
            bool CamUv(float3 p, out float2 uvc)
            {
                float3 pc = mul(_McxCamWorldToCam, float4(p, 1.0)).xyz;
                uvc = 0;
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
                int2 c = (int2)floor(st + 0.5);            // the nearest reading
                float z[9];
                int n = 0;
                [unroll] for (int k = 0; k < 9; k++)
                {
                    int2 t = clamp(c + int2(k % 3 - 1, k / 3 - 1), int2(0, 0), size - 1);
                    z[k] = Linear(_EnvironmentDepthTexture.Load(int4(t, eye, 0)).r);
                    n += z[k] > 0.0 ? 1 : 0;
                }
                if (n == 0) return 0.0;
                // median of the valid readings, by rank (ties broken by index)
                float zm = 0.0;
                int want = (n - 1) / 2;
                [unroll] for (int a = 0; a < 9; a++)
                {
                    int r = 0;
                    [unroll] for (int b = 0; b < 9; b++) r += (z[b] > 0.0 && (z[b] < z[a] || (z[b] == z[a] && b < a))) ? 1 : 0;
                    if (z[a] > 0.0 && r == want) zm = z[a];
                }
                #if defined(MCX_GUIDE)
                float2 uvc0;
                bool guided = CamUv(WorldOf(i.uv, zm, eye), uvc0);
                float3 c0 = guided ? CamRgb(uvc0) : 0;
                #endif
                float wsum = 0.0, zsum = 0.0;
                [unroll] for (int j = 0; j < 9; j++)
                {
                    if (z[j] <= 0.0) continue;
                    int2 t = clamp(c + int2(j % 3 - 1, j / 3 - 1), int2(0, 0), size - 1);
                    float2 d = (float2)t - st;
                    float w = exp(-dot(d, d) * 1.0); // spatial: ~0.7 texel sigma
                    float dz = (z[j] - zm) / (0.02 + 0.02 * zm);
                    #if defined(MCX_GUIDE)
                    if (guided)
                    {
                        float2 uvk;
                        float3 ck = CamUv(WorldOf(((float2)t + 0.5) * _McxDepthSize.zw, z[j], eye), uvk) ? CamRgb(uvk) : c0;
                        float3 dc = ck - c0;
                        // the picture decides which side of an edge this texel is on; a weak depth term keeps
                        // a colour match far off the surface (a white box before a white wall) from pulling
                        w *= exp(-dot(dc, dc) * 78.0) * (0.25 + 0.75 * exp(-0.125 * dz * dz)); // colour sigma 0.08
                    }
                    else
                    #endif
                    w *= exp(-0.5 * dz * dz); // depth alone: readings off the median surface count little
                    w += 1e-7;
                    wsum += w;
                    zsum += w * z[j];
                }
                float zo = zsum / wsum;
                // temporal: towards last frame's output where it agrees
                if (_McxHistory > 0.0)
                {
                    float4 q = mul(_McxPrevWorldToDepth[eye], float4(WorldOf(i.uv, zo, eye), 1.0));
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
