// MetaCodex Ambient Focus, the dome: real room geometry (Space Setup's scanned mesh and planes) and hand stand-ins,
// drawn right after the focus sphere and before any content. Each fragment lowers the eye buffer's darkness
// (BlendOp Min) to what the dome says for that real surface: surfaces inside the dome (closer than _Radius to the
// floor point under you) stay clear, beyond it they fade to _Intensity. Hands (_Hand = 1) are always clear, with a
// soft silhouette. The sphere before it darkens every direction where nothing real is known (above the horizon).
Shader "MetaCodex/FocusMesh"
{
    Properties
    {
        _Radius ("Radius", Float) = 1.5
        _Softness ("Softness", Float) = 0.5
        _Intensity ("Intensity", Range(0, 1)) = 0.8
        _FeetPos ("Floor point under the user", Vector) = (0, 0, 0, 0)
        _Hand ("Hand cut-out", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Geometry-99" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "FocusMesh"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest Always
            BlendOp Min
            Blend One One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Radius;
                float _Softness;
                float _Intensity;
                float4 _FeetPos;
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
                float a;
                if (_Hand > 0.5)
                {
                    float3 view = normalize(GetCameraPositionWS() - i.positionWS);
                    float facing = abs(dot(normalize(i.normalWS), view));
                    a = _Intensity * (1.0 - smoothstep(0.12, 0.55, facing)); // clear inside, soft at the outline
                }
                else
                {
                    float dist = length(i.positionWS - _FeetPos.xyz);            // the dome: from the floor under you
                    a = _Intensity * smoothstep(_Radius, _Radius + max(_Softness, 0.01), dist);
                }
                return half4(0, 0, 0, a);
            }
            ENDHLSL
        }
    }
}
