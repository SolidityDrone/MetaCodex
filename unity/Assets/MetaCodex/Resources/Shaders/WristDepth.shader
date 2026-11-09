// MetaCodex, the wrist menu's depth: drawn right after its light (WristHolo, Transparent-20) and long before the hands
// (FocusMesh: capsules on the tracked joints, drawn last, depth-tested, they clear the eye buffer so the real hands
// show). The menu is a hologram and writes no depth of its own, so before this the capsules of the hand that carries
// it cleared it wherever that hand was in the picture, although the hand is behind it ("the hand cuts it off"). With
// its depth written, a hand behind the menu stays behind it (seen softly through the glass) and a fingertip in front of
// it still shows through. Parts fainter than 0.15 (glow, fading rings) write nothing; nothing while the UI steps aside.
Shader "MetaCodex/WristDepth"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-19" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "WristDepth"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float _McxUiHide;

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half alpha : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.alpha = v.color.a * (half)(1.0 - saturate(_McxUiHide)); // the element's strength (UiWristMenu)
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                clip(i.alpha - 0.15);
                return 0;
            }
            ENDHLSL
        }
    }
}
