// MetaCodex UI shapes: every panel, button, header, selection frame and bar is one quad drawn by this
// shader from per-vertex data, so all shapes of a panel batch into one mesh and one material.
//   COLOR      tint (linear): border, glow and accent colour
//   TEXCOORD0  uv over the quad (the quad is the shape plus a glow margin on every side)
//   TEXCOORD1  x,y = shape size in metres, z = glow margin in metres, w = kind
//   TEXCOORD2  x = corner radius, y = fill style, z = border width, w = state 0..1
// Kinds: 0 panel body, 1 button, 2 header separator, 3 selection frame, 4 bar (state = fill), 5 line, 6 dot.
// A calm, standard look: rounded corners, dark frosted glass, a fine border lit from above, soft shadows.
//   TEXCOORD2 is read as x = corner radius, y = fill style (buttons: 0 normal, 1 filled, 2 ghost), z = border, w = state.
Shader "MetaCodex/SciFiUI"
{
    Properties
    {
        _Scanlines ("Scanlines", Range(0, 1)) = 1
        _GlowBoost ("Shadow", Range(0, 2)) = 1
        _Opacity ("Panel opacity", Range(0.3, 1)) = 0.9
        _Motion ("Motion speed", Range(0, 2)) = 1
        _Grid ("Grid", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "SciFiUI"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Scanlines;
                float _GlowBoost;
                float _Opacity;
                float _Motion;
                float _Grid;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 shape : TEXCOORD1;
                float4 style : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 shape : TEXCOORD1;
                float4 style : TEXCOORD2;
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
                o.shape = v.shape;
                o.style = v.style;
                return o;
            }

            // Signed distance to a rounded rectangle of half size h and corner radius r. Negative inside.
            float sdRound(float2 p, float2 h, float r)
            {
                r = min(r, min(h.x, h.y));
                float2 q = abs(p) - h + r;
                return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 size = i.shape.xy;
                float margin = i.shape.z;
                int kind = (int)round(i.shape.w);
                // style.x = corner radius, style.y = fill style (buttons: 0 normal, 1 filled, 2 ghost), style.z = border, style.w = state
                float radius = i.style.x, fillStyle = i.style.y, border = max(i.style.z, 0.0003), state = saturate(i.style.w);
                float2 h = size * 0.5;
                float2 p = (i.uv - 0.5) * (size + 2.0 * margin); // metres from the shape centre, y up
                float3 tint = i.color.rgb;
                float3 white = float3(1.0, 1.0, 1.0);

                if (kind == 5) // line: a hairline divider, fading at both ends
                {
                    bool vertical = size.y > size.x;
                    float2 q = vertical ? p.yx : p;
                    float2 hq = vertical ? h.yx : h;
                    float across = 1.0 - saturate(abs(q.y) / max(hq.y, 1e-5));
                    float along = saturate(1.0 - abs(q.x) / max(hq.x, 1e-5));
                    float a = smoothstep(0.0, 0.7, across) * smoothstep(0.0, 0.12, along) * i.color.a * (0.35 + 0.4 * state);
                    return half4(lerp(white * 0.35, tint, 0.6), a);
                }
                if (kind == 6) // dot / reticle: a ring and a soft core
                {
                    float r = length(p) / max(h.x, 1e-5);
                    float ring = smoothstep(0.14, 0.0, abs(r - 0.78));
                    float core = smoothstep(0.5, 0.0, r) * (0.3 + 0.7 * state);
                    return half4(lerp(tint, white, 0.35) * (1.0 + 0.5 * state), saturate(max(ring * 0.85, core)) * i.color.a);
                }

                float d = sdRound(p, h, kind == 4 ? (radius < 0.00005 ? 0.0 : h.y) : radius); // bars: pills, square when radius is 0 (arc pieces)
                float aa = max(fwidth(d), 1e-5);
                float inside = saturate(0.5 - d / aa);
                float ring = saturate(0.5 - (abs(d + border * 0.5) - border * 0.5) / aa);
                float yN = clamp(p.y / max(h.y, 1e-5), -1.0, 1.0);   // -1 bottom .. 1 top
                float shadow = (1.0 - inside) * exp(-max(d, 0.0) / max(margin * 0.45, 1e-4));

                float3 fill = 0;
                float fillA = 0;
                float3 edgeCol = white * 0.12;
                float edgeA = 0.0;
                float shadowA = 0.0;

                if (kind == 0) // panel: dark frosted glass, a fine border lit from above, a soft shadow
                {
                    fill = lerp(float3(0.0105, 0.0118, 0.0160), float3(0.0230, 0.0262, 0.0345), yN * 0.5 + 0.5);
                    fill += white * 0.012 * smoothstep(0.82, 1.0, yN);            // faint top sheen
                    fillA = _Opacity;
                    float lit = 0.10 + 0.14 * smoothstep(-0.2, 1.0, yN);          // the top edge catches the light
                    edgeCol = lerp(white * lit, tint * 1.1, state * 0.85);        // state: focus highlight in the accent colour
                    edgeA = 0.55 + 0.45 * state;
                    shadowA = 0.38;
                }
                else if (kind == 1) // button
                {
                    if (fillStyle > 1.5) // ghost: invisible until hovered
                    {
                        fill = white * 0.16;
                        fillA = 0.55 * state;
                        edgeA = 0.0;
                    }
                    else if (fillStyle > 0.5) // filled (primary, danger): the colour itself
                    {
                        fill = tint * (0.62 + 0.30 * state) + white * 0.04 * smoothstep(0.3, 1.0, yN);
                        fillA = 0.97;
                        edgeCol = lerp(tint, white, 0.35);
                        edgeA = 0.35 + 0.4 * state;
                    }
                    else // normal: a quiet surface that lights up in the accent colour when hovered, selected, pressed
                    {
                        fill = lerp(float3(0.040, 0.044, 0.056), tint * 0.42, smoothstep(0.0, 1.0, state));
                        fill += white * 0.015 * smoothstep(0.3, 1.0, yN);
                        fillA = 0.92;
                        edgeCol = lerp(white * 0.10, tint, state);
                        edgeA = 0.35 + 0.6 * state;
                    }
                    shadowA = 0.10 * (1.0 - step(1.5, fillStyle));
                }
                else if (kind == 2) // header: no strip, a hairline separator under the title row
                {
                    float sep = saturate(0.5 - abs(p.y + h.y - 0.0004) / max(0.0005, fwidth(p.y)));
                    float along = smoothstep(h.x, h.x - 0.02, abs(p.x));
                    return half4(white * 0.30, sep * along * 0.45 * i.color.a);
                }
                else if (kind == 3) // selection: a faint fill and marching dashes
                {
                    fill = tint * 0.12;
                    fillA = 0.16;
                    float dash = step(0.5, frac((p.x + p.y) * 45.0 - _Time.y * 1.5));
                    edgeCol = tint;
                    edgeA = 0.25 + 0.75 * dash;
                }
                else // kind 4: progress bar / pill, filled up to state
                {
                    float filled = step(i.uv.x, state + 1e-4);
                    fill = lerp(float3(0.055, 0.060, 0.075), tint, filled);
                    fillA = 0.95;
                    edgeA = 0.0;
                }

                float3 col = fill;
                float a = fillA * inside;
                col = lerp(col, edgeCol, ring * edgeA);
                a = max(a, ring * edgeA);
                float s = shadow * shadowA * _GlowBoost;                            // a dark shadow, never a glow
                col = lerp(col, 0.0, saturate(s * (1.0 - inside)));
                a = max(a, s);
                return half4(col, saturate(a * i.color.a));
            }
            ENDHLSL
        }
    }
}
