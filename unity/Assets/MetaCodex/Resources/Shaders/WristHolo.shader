// MetaCodex wrist menu: holographic glass blades, rings, ticks and glows (UiWristMenu builds the mesh each frame it
// moves). URP, single-pass instanced stereo, premultiplied: over the passthrough, light is added with alpha 0 and the
// glass darkens what is behind it a little (alpha), so the labels read on any room.
// Vertex data: colour (linear rgb, a = strength), uv0 (across 0..1, along 0..1 of the shape),
// uv1 = (width m, length m, kind, state): kind 0 glass blade (dark fill, a 1 mm rim, a finer inner line, scanlines
// running outwards), 1 line / tick (glow across its width), 2 soft glow (radar sweep, its tail along uv.y);
// state 0..1 brightens it (hover, lit, a press flash).
// Fades out with the rest of the UI while the Horizon OS menu is open (_McxUiHide).
Shader "MetaCodex/WristHolo"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-20" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Holo"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/MetaCodex/Shaders/McxOcclusion.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 info : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 info : TEXCOORD1;
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
                o.info = v.info;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float shown = 1.0 - saturate(_McxUiHide);
                float kind = i.info.z, state = saturate(i.info.w);
                half3 c = i.color.rgb;
                half s = i.color.a;
                half3 rgb;
                half a;
                if (kind < 0.5)
                {
                    // glass: distance to the nearest edge in metres, from the shape's own size
                    float2 m = i.uv * i.info.xy;
                    float e = min(min(m.x, i.info.x - m.x), min(m.y, i.info.y - m.y));
                    float rim = 1.0 - smoothstep(0.00045, 0.0012, e);
                    float inner = 1.0 - smoothstep(0.0002, 0.0005, abs(e - 0.0021));
                    float scan = 0.5 + 0.5 * sin(m.x * 1300.0 - _Time.y * 4.5);
                    float grad = lerp(0.2, 1.0, i.uv.x); // brighter towards the outside of a blade
                    half fillA = s * (0.40 + 0.08 * scan * grad) * (1.0 + 0.45 * state);
                    half3 fill = lerp(half3(0.012, 0.024, 0.04), c * 0.30, grad * (0.35 + 0.65 * state));
                    half3 glow = c * s * (rim * (1.25 + 1.6 * state) + inner * (0.22 + 0.3 * state) + grad * 0.10 * (1.0 + 2.0 * state));
                    rgb = fill * fillA + glow;
                    a = fillA;
                }
                else if (kind < 1.5)
                {
                    float d = abs(i.uv.x - 0.5) * 2.0;
                    float core = 1.0 - smoothstep(0.30, 1.0, d);
                    rgb = c * s * core * (1.0 + 1.2 * state);
                    a = s * core * 0.18;
                }
                else
                {
                    float d = abs(i.uv.x - 0.5) * 2.0;
                    float g = (1.0 - d * d) * i.uv.y * i.uv.y;
                    rgb = c * s * g;
                    a = 0.0;
                }
                return half4(rgb * shown, a * shown);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
