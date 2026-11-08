// MetaCodex, the hands: capsules on the tracked hand joints, drawn after everything (windows, focus darkness, blur),
// depth-tested against the opaque panels. Each fragment lowers the eye buffer (BlendOp Min, colour and alpha) so the
// real hand shows through, with a soft silhouette: clear where the capsule faces you, the outline keeps what is under
// it. A hand in front of a window is seen; one behind an opaque window stays hidden.
Shader "MetaCodex/FocusMesh"
{
    Properties
    {
        _Intensity ("Intensity", Range(0, 1)) = 0.8
        _Hand ("Hand cut-out", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Overlay+10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "FocusMesh"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest LEqual // in front of opaque panels (their depth), never through one the hand is behind
            BlendOp Min
            Blend One One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Intensity;
                float _Hand;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 view = normalize(GetCameraPositionWS() - i.positionWS);
                float facing = abs(dot(normalize(i.normalWS), view));
                float a = _Intensity * (1.0 - smoothstep(0.12, 0.55, facing)); // clear inside, soft at the outline
                return half4(0, 0, 0, a);
            }
            ENDHLSL
        }
    }
}
