// MetaCodex Ambient Focus: a sphere around the head, drawn first (before opaque and transparent content, with no
// depth test), that darkens the passthrough beyond a radius. Distance per pixel: the floor plane under the user
// (looking down: the floor near you stays visible); above the horizon there is no distance, so it darkens fully.
// Panels, terminals and agent views draw afterwards, on top: at full intensity only the windows remain.
//   _Radius     where darkening starts (m)        _Softness  width of the fade (m)
//   _Intensity  0..1, darkness beyond the fade    _FloorY    floor height in world space
Shader "MetaCodex/AmbientFocus"
{
    Properties
    {
        _Radius ("Radius", Float) = 1.5
        _Softness ("Softness", Float) = 0.5
        _Intensity ("Intensity", Range(0, 1)) = 0.8
        _FloorY ("Floor height", Float) = 0
        _Tint ("Tint", Color) = (0, 0, 0, 1)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Geometry-100" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "AmbientFocus"
            Tags { "LightMode" = "UniversalForward" }
            Cull Front
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha // alpha = a over the cleared buffer (passthrough shows 1 - a)
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Radius;
                float _Softness;
                float _Intensity;
                float _FloorY;
                half4 _Tint;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 cam = GetCameraPositionWS();
                float3 dir = normalize(i.positionWS - cam);
                float h = cam.y - _FloorY;
                // distance to where this view ray meets the floor (none above the horizon)
                float t = dir.y < -0.02 && h > 0.05 ? h / -dir.y : 1e4;
                float ground = sqrt(max(t * t - h * h, 0.0)); // along the floor, from under the user
                float a = _Intensity * smoothstep(_Radius, _Radius + max(_Softness, 0.01), ground);
                return half4(_Tint.rgb, a);
            }
            ENDHLSL
        }
    }
}
