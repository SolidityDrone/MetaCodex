// MetaCodex agent views: an image (PNG / JPG from Linux) on a quad over a view panel. Unlit, alpha-blended.
Shader "MetaCodex/ViewImage"
{
    Properties
    {
        _MainTex ("Image", 2D) = "white" {}
        _PanelIndex ("Panel slot (set per renderer)", Float) = -1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-5" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "ViewImage"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/MetaCodex/Shaders/McxOcclusion.hlsl"
            #include "Assets/MetaCodex/Shaders/McxPanelFx.hlsl" // the panel being cast (the scan line)

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float _PanelIndex; // set per renderer (UiPanel.TagRenderer); -1: not in a panel
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 positionWS : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.uv = v.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
                // its panel being cast: the picture is there once the scan line has passed, bright for a moment
                float4 pfx = McxPanelFx(_PanelIndex);
                float bd = McxBehindScan(_PanelIndex, i.positionWS);
                c.a *= (half)(McxBuilt(bd) * (1.0 - saturate(pfx.z)));
                c.rgb = lerp(c.rgb, c.rgb + 0.3, saturate(saturate(pfx.y) * 0.5 + McxHeat(bd) * 0.8));
                c.a *= (half)McxVisibility(i.positionWS); // something real in front of the image shows through
                return c;
            }
            ENDHLSL
        }
    }
}
