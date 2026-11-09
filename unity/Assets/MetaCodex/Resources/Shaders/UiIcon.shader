// MetaCodex UI icons (tool logos): textured quads from one atlas, tinted, bent with their panel like the shapes
// (MetaCodex/SciFiUI: _PanelBend[_PanelIndex]), drawn right after the panel shapes and before the text.
Shader "MetaCodex/UiIcon"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
        _PanelIndex ("Panel slot (set per renderer)", Float) = -1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-9" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "UiIcon"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/MetaCodex/Shaders/McxOcclusion.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _PanelBend[128];
            float4 _PanelFx[128]; // a panel opening or closing (UiPanelFx): x flash, y ghost, z hidden, w half height
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _PanelIndex;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; half4 color : COLOR; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; half4 color : COLOR; float2 uv : TEXCOORD0; float3 positionWS : TEXCOORD1; half2 fx : TEXCOORD2; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 p = v.positionOS.xyz;
                float r = _PanelIndex >= 0.0 ? _PanelBend[(int)_PanelIndex].x : 0.0;
                if (r > 0.01) { float a = p.x / r; p = float3(r * sin(a), p.y, p.z - r * (1.0 - cos(a))); }
                o.positionWS = TransformObjectToWorld(p);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.color = v.color;
                o.uv = v.uv;
                // opening / closing: icons come with their part of the panel (from the top down), brighter in the flash
                float4 pfx = _PanelIndex >= 0.0 ? _PanelFx[(int)_PanelIndex] : float4(0, 0, 0, 0);
                float top = saturate(0.5 - v.positionOS.y / (2.0 * max(pfx.w, 1e-3)));
                o.fx = pfx.z > 0.0005 || pfx.x > 0.0005 ? half2(saturate((1.0 - pfx.z) * 1.6 - top * 0.6), pfx.x) : half2(1, 0);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                half3 rgb = c.rgb * i.color.rgb;
                rgb = lerp(rgb, rgb + 0.35, saturate(i.fx.y) * 0.6);
                return half4(rgb, c.a * i.color.a * i.fx.x * (half)McxVisibility(i.positionWS));
            }
            ENDHLSL
        }
    }
}
