// MetaCodex Ambient Focus: a sphere around the head, drawn first (before opaque and transparent content, with no
// depth test), that darkens the passthrough outside a dome standing on the floor around the user. Panels, terminals
// and agent views draw afterwards, on top: at full intensity only the windows (and the hands) remain.
//
// Where each pixel's real surface is (MCX_DEPTH): Meta's live environment depth made sharp and steady with the
// passthrough camera (Resources/Shaders/DepthSharp). A point 1 m along the view ray is projected into the depth map
// (_EnvironmentDepthReprojectionMatrices, world -> depth clip space, as in Meta's EnvironmentOcclusion.cginc), the
// sharp depth there is read and turned back into a world position (_McxDepthToWorld, the inverse matrices, set by
// ShellAmbient each frame). Walls, furniture and hands near you stay; far ones fade.
// Without depth (or where it has none): the floor plane under the user; above the horizon there is no distance.
//
// Blur (MCX_BLUR): outside the dome, instead of black, the room as the passthrough camera saw it: a panorama around
// the user that each camera frame paints into (Resources/Shaders/FocusBlurPano: world-oriented, round mask, no
// rectangle), read by view direction at a high mip (so soft) and darkened by _Intensity. It stays with the room when
// the head turns; where the camera has not looked yet (coverage 0) it is the plain darkness.
//   _Radius  where darkening starts (m)   _Softness  width of the fade (m)   _Intensity  0..1   _FloorY  floor height
//   _FeetPos the floor point under the user   _BlurMip  how soft the room looks (a mip level of the panorama)
Shader "MetaCodex/AmbientFocus"
{
    Properties
    {
        _Radius ("Radius", Float) = 1.5
        _Softness ("Softness", Float) = 0.5
        _Intensity ("Intensity", Range(0, 1)) = 0.8
        _FloorY ("Floor height", Float) = 0
        _FeetPos ("Floor point under the user", Vector) = (0, 0, 0, 0)
        _BlurMip ("Blur mip level", Float) = 3
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
            // runtime materials: every variant must be in the build (shader_feature would strip them)
            #pragma multi_compile_local __ MCX_DEPTH
            #pragma multi_compile_local __ MCX_BLUR
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Radius;
                float _Softness;
                float _Intensity;
                float _FloorY;
                float4 _FeetPos;
                float _BlurMip;
                half4 _Tint;
            CBUFFER_END

            #if defined(MCX_DEPTH)
            // Meta's matrices (EnvironmentDepthManager, or the self-tests) and the sharp depth made from its depth
            // (Resources/Shaders/DepthSharp: linear metres, 0 = no reading)
            Texture2DArray _McxDepthSharp;
            SamplerState sampler_McxDepthSharp;
            float4x4 _EnvironmentDepthReprojectionMatrices[2];
            float4 _EnvironmentDepthZBufferParams;
            float4x4 _McxDepthToWorld[2];
            #endif
            #if defined(MCX_BLUR)
            TEXTURE2D(_McxBlurPano);      // premultiplied colour, coverage in alpha; wraps around in x
            SAMPLER(sampler_McxBlurPano);
            #endif

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
                // the floor plane (fallback): where this view ray meets it, none above the horizon
                float h = cam.y - _FloorY;
                float t = dir.y < -0.02 && h > 0.05 ? h / -dir.y : 1e4;
                float dist = sqrt(max(t * t - h * h, 0.0)); // along the floor, from under the user
                float3 surface = cam + dir * min(t, 3.0);
                #if defined(MCX_DEPTH)
                {
                    #if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
                    uint eye = unity_StereoEyeIndex;
                    #else
                    uint eye = 0; // a mono camera (Editor, self-tests): the left eye's depth
                    #endif
                    float4 q = mul(_EnvironmentDepthReprojectionMatrices[eye], float4(cam + dir, 1.0));
                    float2 uv = (q.xy / q.w + 1.0) * 0.5;
                    if (q.w > 0.0 && all(uv > 0.002) && all(uv < 0.998))
                    {
                        float lin = _McxDepthSharp.SampleLevel(sampler_McxDepthSharp, float3(uv, eye), 0).r;
                        if (lin > 0.01)
                        {
                            float ndc = _EnvironmentDepthZBufferParams.x / lin - _EnvironmentDepthZBufferParams.y;
                            float4 w = mul(_McxDepthToWorld[eye], float4(uv * 2.0 - 1.0, ndc, 1.0));
                            surface = w.xyz / w.w;
                            dist = length(surface - _FeetPos.xyz); // the dome, from the floor under the user
                        }
                        else dist = 1e4; // nothing measured this far: beyond the dome
                    }
                }
                #endif
                float m = smoothstep(_Radius, _Radius + max(_Softness, 0.01), dist); // 0 inside the dome, 1 outside
                float a = m * _Intensity;
                half3 rgb = _Tint.rgb;
                #if defined(MCX_BLUR)
                {
                    // the room in this direction, as the camera last saw it (the panorama is centred on the head)
                    float3 d = normalize(dir);
                    float2 puv = float2(atan2(d.x, d.z) / 6.28318531 + 0.5, asin(clamp(d.y, -1.0, 1.0)) / 3.14159265 + 0.5);
                    half4 pano = SAMPLE_TEXTURE2D_LOD(_McxBlurPano, sampler_McxBlurPano, puv, _BlurMip);
                    float cover = saturate(pano.a * 1.1);
                    half3 c = pano.a > 1e-3 ? pano.rgb / pano.a : 0;
                    // premultiplied: the blurred room shows through (1 - intensity), the passthrough is covered by m
                    float aBlur = m * lerp(_Intensity, 1.0, cover);
                    half3 premul = (half3)(m * cover * (1.0 - _Intensity)) * c;
                    a = aBlur;
                    rgb = a > 1e-4 ? premul / a : 0;
                }
                #endif
                return half4(rgb, a);
            }
            ENDHLSL
        }
    }
}
