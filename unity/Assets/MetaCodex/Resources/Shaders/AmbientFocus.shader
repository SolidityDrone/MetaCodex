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
//
// The void (owner 2026-11-09: "in the fog some interesting effect, not simple black"): outside the dome, not black but
// deep blue-black space with a nebula and sparse stars that twinkle, drawn once into a sky map (ShellAmbient.VoidSky,
// octahedral, world-oriented, turning slowly round the vertical): one texel read per pixel, no trigonometry, no noise
// (0.7.7 computed the nebula and stars per pixel: far too much GPU at render scale 1.6, the owner saw lags); on the room's surfaces just past the edge, a faint holographic grid (50 cm,
// lines across each surface only, from the surface's own slope) with a pulse running outwards now and then; and where
// the room crosses the dome, a contour of light (a ring on the floor round the user). With BLUR only the grid and the
// contour, over the blurred room. All of it is skipped inside the dome (most of the picture), so it costs little.
// Premultiplied output (One OneMinusSrcAlpha): light can be added over the passthrough without darkening it.
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
        _Accent ("Accent (the UI's)", Color) = (0.21, 0.82, 1, 1)
        _Void ("Void effects 0..1", Range(0, 1)) = 1
        _McxVoidSky ("Sky (octahedral, sRGB; alpha = twinkle phase)", 2D) = "black" {}
        _VoidRot ("Sky turn (cos, sin)", Vector) = (1, 0, 0, 0)
        _VoidTime ("Void clock (s, wrapped)", Float) = 0
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
            Blend One OneMinusSrcAlpha // premultiplied; alpha = a over the cleared buffer (passthrough shows 1 - a)
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
                half4 _Accent;
                float _Void;
                float4 _VoidRot;
                float _VoidTime;
            CBUFFER_END
            TEXTURE2D(_McxVoidSky);
            SAMPLER(sampler_McxVoidSky);

            // a direction into the octahedral sky map (y up; ShellAmbient.VoidOctDecode is the inverse)
            float2 OctUv(float3 d)
            {
                d /= abs(d.x) + abs(d.y) + abs(d.z);
                float2 p = d.xz;
                if (d.y < 0.0) p = (1.0 - abs(p.yx)) * float2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
                return p * 0.5 + 0.5;
            }

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
                half3 premul = _Tint.rgb * a; // the darkness
                #if defined(MCX_BLUR)
                {
                    // the room in this direction, as the camera last saw it (the panorama is centred on the head)
                    float3 d = normalize(dir);
                    float2 puv = float2(atan2(d.x, d.z) / 6.28318531 + 0.5, asin(clamp(d.y, -1.0, 1.0)) / 3.14159265 + 0.5);
                    half4 pano = SAMPLE_TEXTURE2D_LOD(_McxBlurPano, sampler_McxBlurPano, puv, _BlurMip);
                    float cover = saturate(pano.a * 1.1);
                    half3 c = pano.a > 1e-3 ? pano.rgb / pano.a : 0;
                    // premultiplied: the blurred room shows through (1 - intensity), the passthrough is covered by m
                    a = m * lerp(_Intensity, 1.0, cover);
                    premul = (half3)(m * cover * (1.0 - _Intensity)) * c;
                }
                #endif
                // derivatives first (they must not sit in a branch): the contour's width and the surface's slope
                float aaD = min(fwidth(dist), 0.05);
                float3 sdx = ddx(surface), sdy = ddy(surface);
                if (_Void > 0.001 && _Intensity > 0.001)
                {
                    float edgeAt = _Radius + max(_Softness, 0.01) * 0.15;
                    float off = abs(dist - edgeAt);
                    float halo = dist < 50.0 ? exp(-off / 0.06) : 0.0;
                    if (m > 0.002 || halo > 0.01)
                    {
                        half3 light = 0;
                        // the contour where the room crosses the dome: a line of light, slowly running round (only near it)
                        if (halo > 0.01)
                        {
                            float contour = saturate(1.0 - off / (0.006 + aaD * 1.5));
                            float around = atan2(surface.x - _FeetPos.x, surface.z - _FeetPos.z);
                            light += _Accent.rgb * (contour * 0.65 + halo * 0.10) * (0.7 + 0.3 * sin(_VoidTime * 1.1 + around * 3.0));
                        }
                        // a holographic grid on the surfaces just past the edge (50 cm, only lines across a surface)
                        float near = m * exp(-max(dist - _Radius, 0.0) * 0.75) * saturate(2.8 - length(surface - cam) * 0.55);
                        if (near > 0.003)
                        {
                            float3 n = normalize(cross(sdx, sdy) + float3(0, 1e-6, 0));
                            float3 gd = (0.5 - abs(frac(surface / 0.5) - 0.5)) * 0.5;              // metres to the nearest grid plane
                            float3 ga = max((abs(sdx) + abs(sdy)) * 0.75, 1e-4); // fwidth, from the derivatives taken above
                            float3 gl = saturate(1.0 - (gd - 0.0018) / ga) * (1.0 - abs(n));      // lines across the surface only
                            float grid = max(gl.x, max(gl.y, gl.z));
                            float pulse = exp(-abs(frac((dist - _Radius) * 0.35 - _VoidTime * 0.11) - 0.5) * 14.0);
                            light += _Accent.rgb * grid * near * (0.10 + 0.22 * pulse);
                        }
                        #if !defined(MCX_BLUR)
                        if (m > 0.002)
                        {
                            // deep space from the sky map, turning slowly round the vertical; a star twinkles by its phase
                            float3 d = normalize(dir);
                            float3 r = float3(d.x * _VoidRot.x - d.z * _VoidRot.y, d.y, d.x * _VoidRot.y + d.z * _VoidRot.x);
                            half4 sky = SAMPLE_TEXTURE2D_LOD(_McxVoidSky, sampler_McxVoidSky, OctUv(r), 0);
                            half tw = sky.a > 0.02 ? (half)(0.55 + 0.45 * sin(_VoidTime * (0.5 + sky.a * 1.6) + sky.a * 40.0)) : (half)1.0;
                            half3 space = half3(0.0006, 0.0013, 0.0030) + sky.rgb * tw;
                            premul = lerp(premul, space * a, 1.0 - saturate(dot(_Tint.rgb, float3(1, 1, 1)) * 4.0)); // a tint set: it wins
                        }
                        #endif
                        premul += light * _Void * _Intensity;
                    }
                }
                return half4(premul, a);
            }
            ENDHLSL
        }
    }
}
