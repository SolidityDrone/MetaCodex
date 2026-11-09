// MetaCodex wrist menu sparks (Shuriken particles): a soft round point of light, no texture. Pure light over the
// passthrough (alpha 0, premultiplied), fades with the UI while the Horizon OS menu is open.
Shader "MetaCodex/WristSpark"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+25" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/MetaCodex/Shaders/McxOcclusion.hlsl"

            struct Attributes { float4 positionOS : POSITION; half4 color : COLOR; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; half4 color : COLOR; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.color = v.color;
                o.uv = v.uv;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float d = length(i.uv - 0.5) * 2.0;
                float g = saturate(1.0 - d);
                g = g * g * (1.0 + 2.0 * saturate(1.0 - d * 3.0)); // a hot centre
                return half4(i.color.rgb * i.color.a * g * (1.0 - saturate(_McxUiHide)), 0.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
