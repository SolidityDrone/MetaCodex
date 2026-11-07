// MetaCodex UI shapes: every panel, button, header, selection frame and bar is one quad drawn by this
// shader from per-vertex data, so all shapes of a panel batch into one mesh and one material.
//   COLOR      tint (linear): border, glow and accent colour
//   TEXCOORD0  uv over the quad (the quad is the shape plus a glow margin on every side)
//   TEXCOORD1  x,y = shape size in metres, z = glow margin in metres, w = kind
//   TEXCOORD2  x = big corner cut (top-left, bottom-right), y = small cut, z = border width, w = state 0..1
// Kinds: 0 panel body, 1 button, 2 header strip, 3 selection frame, 4 bar (state = fill), 5 line, 6 dot.
// An original "command console" look: cut corners, bevelled frame, faint grid and scanlines, outer glow.
Shader "MetaCodex/SciFiUI"
{
    Properties
    {
        _Scanlines ("Scanlines", Range(0, 1)) = 1
        _GlowBoost ("Glow", Range(0, 2)) = 1
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

            // Signed distance to a rectangle of half size h whose top-left and bottom-right corners are cut
            // by cBig and the other two by cSmall (y up). Negative inside.
            float sdCut(float2 p, float2 h, float cBig, float cSmall)
            {
                float2 q = abs(p);
                float2 d = q - h;
                float box = length(max(d, 0.0)) + min(max(d.x, d.y), 0.0);
                float c = (p.x * p.y < 0.0) ? cBig : cSmall;
                float cut = (q.x + q.y - (h.x + h.y - c)) * 0.70710678;
                return max(box, cut);
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 size = i.shape.xy;
                float margin = i.shape.z;
                int kind = (int)round(i.shape.w);
                float cBig = i.style.x, cSmall = i.style.y, border = max(i.style.z, 0.0003), state = saturate(i.style.w);
                float2 h = size * 0.5;
                float2 p = (i.uv - 0.5) * (size + 2.0 * margin); // metres from the shape centre, y up
                float3 tint = i.color.rgb;
                float t = _Time.y;

                if (kind == 5) // line: soft-edged bar along its longer side, fades at both ends
                {
                    bool vertical = size.y > size.x;
                    float2 q = vertical ? p.yx : p;
                    float2 hq = vertical ? h.yx : h;
                    float across = 1.0 - saturate(abs(q.y) / max(hq.y, 1e-5));
                    float along = saturate(1.0 - abs(q.x) / max(hq.x, 1e-5));
                    float a = smoothstep(0.0, 0.6, across) * (0.35 + 0.65 * smoothstep(0.0, 0.08, along)) * i.color.a;
                    return half4(tint * (1.0 + 0.6 * state), a);
                }
                if (kind == 6) // dot / reticle: ring plus a soft core
                {
                    float r = length(p) / max(h.x, 1e-5);
                    float ring = smoothstep(0.12, 0.0, abs(r - 0.78));
                    float core = smoothstep(0.45, 0.0, r) * (0.35 + 0.65 * state);
                    float glowDot = exp(-max(r - 1.0, 0.0) * 4.0) * 0.25;
                    return half4(tint * (1.2 + state), saturate(max(ring, core) + glowDot) * i.color.a);
                }

                float d = sdCut(p, h, cBig, cSmall);
                float aa = max(fwidth(d), 1e-5);
                float inside = saturate(0.5 - d / aa);
                float ring = saturate(0.5 - (abs(d + border * 0.5) - border * 0.5) / aa);
                float glow = (1.0 - inside) * exp(-max(d, 0.0) / max(margin * 0.3, 1e-4));
                float yN = clamp(p.y / max(h.y, 1e-5), -1.0, 1.0);   // -1 bottom .. 1 top
                float xN = clamp(p.x / max(h.x, 1e-5), -1.0, 1.0);

                float3 fill = 0;
                float fillA = 0;
                float edge = 1.0;
                float glowA = 0.18;

                if (kind == 0) // panel body
                {
                    fill = lerp(float3(0.004, 0.012, 0.022), float3(0.016, 0.040, 0.066), yN * 0.5 + 0.5);
                    float2 g = abs(frac(p * 40.0) - 0.5);                        // 2.5 cm grid
                    float grid = step(0.47, max(g.x, g.y));
                    fill += tint * 0.035 * grid;
                    float scan = 0.94 + 0.06 * sin(p.y * 1400.0 - t * 3.0);
                    fill *= lerp(1.0, scan, _Scanlines);
                    float bevel = saturate(0.5 - (abs(d + border * 3.2) - border * 0.35) / aa);
                    fill += tint * 0.22 * bevel;
                    fillA = 0.90;
                    // corner brackets: brighter frame near the corners
                    float cx = smoothstep(h.x - 0.028, h.x - 0.022, abs(p.x));
                    float cy = smoothstep(h.y - 0.028, h.y - 0.022, abs(p.y));
                    edge = 0.75 + 1.1 * max(cx, cy) * step(0.5, max(cx, cy));
                    // a comet running round the frame
                    float ang = atan2(p.y * h.x, p.x * h.y) / 6.2831853 + 0.5;
                    float s = frac(ang - t * 0.06);
                    edge += 1.6 * exp(-s * 28.0) * (0.4 + 0.6 * state);
                    glowA = 0.22;
                }
                else if (kind == 1) // button
                {
                    float3 baseCol = lerp(float3(0.010, 0.030, 0.050), float3(0.030, 0.075, 0.110), yN * 0.5 + 0.5);
                    fill = baseCol + tint * (0.07 + 0.33 * state);
                    float shine = smoothstep(0.55, 1.0, yN) * 0.08;
                    fill += shine;
                    fillA = 0.86 + 0.1 * state;
                    edge = 0.85 + 0.9 * state;
                    glowA = 0.10 + 0.55 * state;
                }
                else if (kind == 2) // header strip: bright at the left, fading right
                {
                    float fade = smoothstep(1.0, -1.0, xN);
                    fill = tint * (0.10 + 0.32 * fade) + float3(0.01, 0.02, 0.03);
                    float stripes = step(0.5, frac((p.x - p.y) * 70.0)) * smoothstep(0.55, 1.0, xN) * 0.10;
                    fill += tint * stripes;
                    fillA = 0.92;
                    edge = 1.0 + 0.6 * state;
                    glowA = 0.12;
                }
                else if (kind == 3) // selection frame: faint fill, marching dashes
                {
                    fill = tint * 0.10;
                    fillA = 0.14;
                    float dash = step(0.5, frac((p.x + p.y) * 45.0 - t * 1.8));
                    edge = 0.35 + 1.4 * dash;
                    glowA = 0.30;
                }
                else // kind 4: bar, filled up to state
                {
                    float filled = step(i.uv.x, state + 1e-4);
                    fill = lerp(float3(0.01, 0.025, 0.04), tint * 0.85, filled);
                    fillA = 0.9;
                    edge = 0.8;
                    glowA = 0.15 * filled;
                }

                float3 col = fill * inside;
                col = lerp(col, tint * (1.05 * edge), ring);
                float a = max(fillA * inside, ring * saturate(0.55 + 0.45 * edge));
                float g = glow * glowA * _GlowBoost;
                col = lerp(col, tint, saturate(g * (1.0 - inside)));
                a = max(a, g);
                return half4(col, saturate(a * i.color.a));
            }
            ENDHLSL
        }
    }
}
