// MetaCodex physics stage bodies (agent views, scene3d): simple lit look, one light from above, rim and highlight.
// In Resources so it ships in the build without a material asset; colour per body via a property block.
Shader "MetaCodex/StageLit"
{
    Properties
    {
        _Color ("Color", Color) = (0.36, 0.6, 1, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "StageLit"
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/MetaCodex/Shaders/McxOcclusion.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 positionWS : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };

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
                float3 n = normalize(i.normalWS);
                float3 l = normalize(float3(0.3, 1.0, -0.45));
                float3 v = normalize(GetCameraPositionWS() - i.positionWS);
                float diffuse = saturate(dot(n, l)) * 0.62 + 0.38;
                float rim = pow(1.0 - saturate(dot(n, v)), 3.0) * 0.28;
                float spec = pow(saturate(dot(n, normalize(l + v))), 40.0) * 0.35;
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half vis = (half)McxVisibility(i.positionWS); // opaque: alpha 0 where something real is in front
                return half4((_Color.rgb * diffuse + rim + spec) * vis, vis);
            }
            ENDHLSL
        }
    }
}
