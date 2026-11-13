// MetaCodex start splash (ShellSplash): the logo over the passthrough, on top of everything. It comes in behind a scan
// line running down (like a window being cast), holds, then breaks up into light: a noise dissolve with a bright edge.
//   _Fade      overall alpha 0..1
//   _Scan      0..1: how far the scan line has run down the logo (1 = all of it shown)
//   _Dissolve  0..1: how much has broken up (0 = none)
// Blend as the UI: colour by alpha; the eye buffer's alpha (how much of the room is covered) only grows.
Shader "MetaCodex/Splash"
{
    Properties
    {
        _MainTex ("Logo", 2D) = "black" {}
        _Fade ("Fade", Range(0, 1)) = 1
        _Scan ("Scan", Range(0, 1)) = 1
        _Dissolve ("Dissolve", Range(0, 1)) = 0
        _Accent ("Accent", Color) = (0.25, 0.65, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Overlay" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Splash"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _Fade, _Scan, _Dissolve;
                float4 _Accent;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            float Hash(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x), lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), f.x), f.y);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                // coming in: shown above the scan line (it runs from the top down), the line itself a band of light ("line" is an HLSL keyword on Vulkan)
                float front = 1.0 - _Scan * 1.08;              // runs a little past the bottom, so the line leaves the logo
                float d = i.uv.y - front;
                float shown = smoothstep(-0.004, 0.004, d);
                float band = _Scan < 1.0 ? exp(-abs(d) / 0.010) : 0.0;
                // going: small cells break up in noise order; each one flares in the accent just before it goes
                float n = Noise(i.uv * float2(64.0, 36.0)) * 0.75 + Noise(i.uv * float2(9.0, 5.0)) * 0.25;
                float keep = smoothstep(_Dissolve - 0.02, _Dissolve + 0.02, n);
                float edge = _Dissolve > 0.0 ? saturate(1.0 - abs(n - _Dissolve) / 0.06) * c.a : 0.0;
                float3 rgb = c.rgb + _Accent.rgb * (band * 0.9 * c.a + edge * 1.4);
                float a = c.a * shown * keep;
                a = max(a, band * 0.55 * saturate(c.a * 4.0 + 0.15)); // the line shows across the logo's width, faint off it
                a = max(a, edge * 0.8);
                return half4(rgb, saturate(a * _Fade));
            }
            ENDHLSL
        }
    }
}
