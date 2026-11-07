// Hand ray beam for LineRenderer (uv.x along the line 0..1, uv.y across): bright at the hand, fading
// towards the target, soft across. Additive so it reads as light over passthrough.
Shader "MetaCodex/Beam"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+20" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Beam"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            Blend SrcAlpha One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

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
                float along = pow(saturate(1.0 - i.uv.x), 1.2);
                float across = 1.0 - abs(i.uv.y * 2.0 - 1.0);
                float a = along * across * across * i.color.a;
                return half4(i.color.rgb, a);
            }
            ENDHLSL
        }
    }
}
