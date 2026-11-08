// MetaCodex UI shapes: every panel, button, header, selection frame and bar is one quad drawn by this
// shader from per-vertex data, so all shapes of a panel batch into one mesh and one material.
//   COLOR      tint (linear): border, glow and accent colour
//   TEXCOORD0  uv over the quad (the quad is the shape plus a glow margin on every side)
//   TEXCOORD1  x,y = shape size in metres, z = glow margin in metres, w = kind
//   TEXCOORD2  x = chamfer, y = small cut / fill style, z = border width, w = state 0..1
// Kinds: 0 panel body, 1 button, 2 header bar, 3 selection frame, 4 bar (state = fill), 5 line, 6 dot.
// The look: a sci-fi command console in the spirit of RTS game interfaces (an original design, no game assets):
// angular chamfered frames, deep-navy holographic glass with a faint hex lattice and scanlines, steel rims lit
// from above, cyan energy edges and corner brackets, an energy bar under each title, command-card buttons that
// charge up when hovered, segmented energy bars. Everything is computed per pixel from the vertex data.
// Two passes: PanelDepth writes depth where a panel body is opaque (so text and images of windows behind it are
// hidden, as they render later), then the colour pass. Alpha is blended with One OneMinusSrcAlpha: it only ever
// grows, so a translucent button, row or shadow drawn over an opaque panel never opens a hole to the passthrough
// (the eye buffer's alpha decides how much of the real world shows).
//   TEXCOORD2: x = chamfer (corner cut), y = small cut (panels) or fill style (buttons: 0 normal, 1 filled, 2 ghost), z = border, w = state.
Shader "MetaCodex/SciFiUI"
{
    Properties
    {
        _Scanlines ("Scanlines", Range(0, 1)) = 1
        _GlowBoost ("Shadow", Range(0, 2)) = 1
        _Opacity ("Panel opacity", Range(0.3, 1)) = 0.9
        _Motion ("Motion speed", Range(0, 2)) = 1
        _Grid ("Grid", Range(0, 1)) = 1
        _PanelIndex ("Panel slot (set per renderer)", Float) = -1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        // the bend of every panel: x = radius (0 flat), indexed by the renderer's _PanelIndex (UiPanel)
        float4 _PanelBend[128];

        float3 BendPanel(float3 p, float r)
        {
            if (r <= 0.01) return p;
            float a = p.x / r;
            return float3(r * sin(a), p.y, p.z - r * (1.0 - cos(a)));
        }

        CBUFFER_START(UnityPerMaterial)
            float _PanelIndex;
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
            float r = _PanelIndex >= 0.0 ? _PanelBend[(int)_PanelIndex].x : 0.0;
            o.positionCS = TransformObjectToHClip(BendPanel(v.positionOS.xyz, r));
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

        ENDHLSL

        Pass
        {
            Name "PanelDepth"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth

            // the body's depth sits 1.5 mm behind it: buttons and rows on a bent panel are split into other columns
            // than the body, so their chords can dip a hair behind the body's and would fail the depth test
            Varyings vertDepth(Attributes v)
            {
                v.positionOS.z += 0.0015;
                return vert(v);
            }
            #pragma multi_compile_instancing

            // depth only where a panel body is opaque enough to hide what is behind it
            half4 fragDepth(Varyings i) : SV_Target
            {
                int kind = (int)round(i.shape.w);
                if (kind != 0 || _Opacity < 0.75) discard;
                float2 h = i.shape.xy * 0.5;
                float2 p = (i.uv - 0.5) * (i.shape.xy + 2.0 * i.shape.z);
                // the chamfered panel body (the colour pass's shape, without the anti-aliased edge)
                float2 q = abs(p);
                float2 dd = q - h;
                float box = length(max(dd, 0.0)) + min(max(dd.x, dd.y), 0.0);
                float c = (p.x * p.y < 0.0) ? i.style.x : i.style.y;
                if (max(box, (q.x + q.y - (h.x + h.y - min(c, min(h.x, h.y)))) * 0.70710678) > 0.0) discard;
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "SciFiUI"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            // ---- the look: a sci-fi command console (angular chamfered frames, holographic glass, steel rims lit from
            // above, cyan energy edges and corner brackets, a faint hex lattice and scanlines). All procedural.

            // Signed distance to a box of half size h with the top-left and bottom-right corners cut by cBig and the
            // other two by cSmall (y up). Negative inside.
            float sdChamfer(float2 p, float2 h, float cBig, float cSmall)
            {
                float2 q = abs(p);
                float2 d = q - h;
                float box = length(max(d, 0.0)) + min(max(d.x, d.y), 0.0);
                float c = (p.x * p.y < 0.0) ? cBig : cSmall;
                c = min(c, min(h.x, h.y));
                float cut = (q.x + q.y - (h.x + h.y - c)) * 0.70710678;
                return max(box, cut);
            }

            // 1 on the edges of a hexagon lattice of cell size `cell` (metres), 0 inside the cells
            float hexLines(float2 p, float cell, float width)
            {
                const float2 s = float2(1.0, 1.7320508);
                float2 q = p / cell;
                float2 a = (frac(q / s) - 0.5) * s;
                float2 b = (frac((q - s * 0.5) / s) - 0.5) * s;
                float2 g = dot(a, a) < dot(b, b) ? a : b;
                g = abs(g);
                float hexD = max(dot(g, float2(0.5, 0.8660254)), g.x); // 0.5 at the cell border
                return smoothstep(0.5 - width, 0.5, hexD);
            }

            half4 frag(Varyings i) : SV_Target
            {
                float2 size = i.shape.xy;
                float margin = i.shape.z;
                int kind = (int)round(i.shape.w);
                float cBig = i.style.x, style2 = i.style.y, border = max(i.style.z, 0.0004), state = saturate(i.style.w);
                float2 h = size * 0.5;
                float2 p = (i.uv - 0.5) * (size + 2.0 * margin); // metres from the shape centre, y up
                float3 tint = i.color.rgb;
                float t = _Time.y;
                float3 white = float3(1.0, 1.0, 1.0);
                float yN = clamp(p.y / max(h.y, 1e-5), -1.0, 1.0);   // -1 bottom .. 1 top
                float xN = clamp(p.x / max(h.x, 1e-5), -1.0, 1.0);

                if (kind == 5) // line: a glowing energy line, bright core, fading at both ends
                {
                    bool vertical = size.y > size.x;
                    float2 q = vertical ? p.yx : p;
                    float2 hq = vertical ? h.yx : h;
                    float across = 1.0 - saturate(abs(q.y) / max(hq.y, 1e-5));
                    float along = saturate(1.0 - abs(q.x) / max(hq.x, 1e-5));
                    float a = smoothstep(0.0, 0.8, across) * smoothstep(0.0, 0.10, along) * i.color.a * (0.45 + 0.45 * state);
                    return half4(lerp(tint, white, 0.25) * (1.0 + 0.5 * state), a);
                }
                if (kind == 6) // dot / reticle: a ring and a soft core
                {
                    float r = length(p) / max(h.x, 1e-5);
                    float ring = smoothstep(0.14, 0.0, abs(r - 0.78));
                    float core = smoothstep(0.5, 0.0, r) * (0.35 + 0.65 * state);
                    float halo = exp(-max(r - 1.0, 0.0) * 5.0) * 0.2;
                    return half4(lerp(tint, white, 0.3) * (1.1 + 0.5 * state), saturate(max(max(ring * 0.9, core), halo)) * i.color.a);
                }

                // shape and its anti-aliased inside / border
                float d;
                bool pill = false;
                if (kind == 4)
                {
                    // bars: square pieces (radius 0: arcs), chamfered energy bars (thick and long), else pills (strokes, dots)
                    bool square = cBig < 0.00005;
                    pill = !square && !(h.y > 0.0022 && h.x > h.y * 4.0);
                    float2 q = abs(p) - h;
                    d = square ? length(max(q, 0.0)) + min(max(q.x, q.y), 0.0)
                       : pill ? length(max(q + h.y, 0.0)) + min(max(q.x + h.y, q.y + h.y), 0.0) - h.y
                       : sdChamfer(p, h, min(h.y, 0.004), min(h.y, 0.004) * 0.3);
                }
                else
                {
                    float small = kind == 0 ? style2 : cBig * 0.28;
                    d = sdChamfer(p, h, cBig, small);
                }
                float aa = max(fwidth(d), 1e-5);
                float inside = saturate(0.5 - d / aa);
                float ring = saturate(0.5 - (abs(d + border * 0.5) - border * 0.5) / aa);
                float outsideFall = (1.0 - inside) * exp(-max(d, 0.0) / max(margin * 0.42, 1e-4));

                float3 fill = 0;
                float fillA = 0;
                float3 edgeCol = tint;
                float edgeA = 0.0;
                float glowA = 0.0;      // cyan halo outside the shape
                float shadeA = 0.0;     // dark contact shadow just outside

                if (kind == 0) // panel: holographic glass in a steel frame
                {
                    float3 lo = float3(0.006, 0.016, 0.030), hi = float3(0.022, 0.052, 0.090);
                    fill = lerp(lo, hi, smoothstep(-1.1, 1.0, yN));
                    float edgeDist = -d;                                          // metres inside the frame
                    float nearEdge = exp(-edgeDist / 0.05);
                    fill += tint * 0.05 * hexLines(p, 0.022, 0.06) * (0.25 + 0.75 * nearEdge);   // hex lattice, strongest at the frame
                    fill *= 0.972 + 0.028 * sin(p.y * 2400.0 - t * 1.6);          // fine scanlines
                    fill += tint * 0.035 * smoothstep(0.6, 1.0, yN);              // light from the top edge
                    // inner inset line (the glass sits in the frame) and the steel frame itself
                    float inset = saturate(0.5 - (abs(d + 0.0032) - 0.00045) / aa);
                    fill = lerp(fill, tint * 0.55, inset * 0.55);
                    fillA = _Opacity;
                    float lit = smoothstep(-0.9, 1.0, yN);                       // the frame is lit from above
                    float3 steel = lerp(float3(0.16, 0.22, 0.30), float3(0.58, 0.68, 0.80), lit);
                    // corner brackets: the frame glows in the accent colour near each corner
                    float cx = smoothstep(h.x - 0.040, h.x - 0.030, abs(p.x)), cy = smoothstep(h.y - 0.040, h.y - 0.030, abs(p.y));
                    float bracket = max(cx, cy);
                    // a light running round the frame when the panel has focus
                    float ang = atan2(p.y * h.x, p.x * h.y) / 6.2831853 + 0.5;
                    float sweep = exp(-frac(ang - t * 0.07) * 24.0) * state;
                    edgeCol = lerp(steel, tint * 1.25 + 0.12, saturate(bracket * 0.85 + state * 0.6 + sweep));
                    edgeA = 0.85;
                    glowA = 0.10 + 0.22 * state;
                    shadeA = 0.30;
                }
                else if (kind == 1) // button: a command-card plate
                {
                    float topLight = smoothstep(0.2, 1.0, yN);
                    if (style2 > 1.5) // ghost: invisible until hovered
                    {
                        fill = tint * 0.22 + white * 0.03;
                        fillA = 0.6 * state;
                        edgeCol = tint * 1.2;
                        edgeA = 0.9 * state;
                        glowA = 0.25 * state;
                    }
                    else if (style2 > 0.5) // filled: primary / danger, charged with its colour
                    {
                        fill = lerp(tint * 0.38, tint * 0.80, smoothstep(-1.0, 1.0, yN)) * (0.85 + 0.3 * state) + white * 0.06 * topLight;
                        fill += tint * 0.25 * hexLines(p, 0.010, 0.10);
                        fillA = 0.97;
                        edgeCol = lerp(tint, white, 0.45);
                        edgeA = 0.75 + 0.25 * state;
                        glowA = 0.16 + 0.30 * state;
                    }
                    else // normal: a dark steel-blue plate that charges up with the accent colour
                    {
                        float3 plate = lerp(float3(0.016, 0.034, 0.058), float3(0.040, 0.078, 0.120), smoothstep(-1.0, 1.0, yN));
                        fill = plate + tint * (0.42 * state) + white * 0.025 * topLight;
                        float innerGlow = exp(d / 0.004) * state;                // light pooling at the rim when lit
                        fill += tint * 0.35 * innerGlow;
                        fillA = 0.95;
                        edgeCol = lerp(float3(0.30, 0.40, 0.52) * (0.7 + 0.5 * topLight), tint * 1.25, saturate(state * 1.2));
                        edgeA = 0.7 + 0.3 * state;
                        glowA = 0.22 * state;
                    }
                    shadeA = 0.12 * (1.0 - step(1.5, style2));
                }
                else if (kind == 2) // header: an energy bar under the title, bright at the left, hatched at the right
                {
                    float bottom = -h.y;
                    float lineD = abs(p.y - bottom - 0.0006);
                    float barLine = saturate(0.5 - (lineD - 0.0005) / max(fwidth(p.y), 1e-5));
                    float along = smoothstep(1.0, -1.0, xN);                      // brighter on the left
                    float seg = step(p.x, -h.x + 0.09) * step(-h.x, p.x);         // a glowing segment at the left end
                    float hatch = step(0.5, frac((p.x - p.y) * 90.0)) * smoothstep(0.55, 0.95, xN) * smoothstep(bottom, bottom + 0.012, p.y);
                    float3 col = tint * (0.55 + 0.9 * seg) + white * 0.15 * seg;
                    float a = barLine * (0.35 + 0.65 * along + 0.6 * seg) + hatch * 0.10;
                    float under = exp(-max(p.y - bottom, 0.0) / 0.004) * step(bottom, p.y) * along * 0.10; // light spilling up from the bar
                    return half4(col, saturate(max(a, under)) * i.color.a);
                }
                else if (kind == 3) // selection: a faint fill and marching dashes
                {
                    fill = tint * 0.12;
                    fillA = 0.16;
                    float dash = step(0.5, frac((p.x + p.y) * 45.0 - t * 1.6));
                    edgeCol = tint * 1.2;
                    edgeA = 0.25 + 0.75 * dash;
                    glowA = 0.2;
                }
                else // kind 4: bar (state = how full); energy bars are segmented
                {
                    float filled = step(i.uv.x, state + 1e-4);
                    float segGap = (!pill && h.x > 0.02) ? step(0.86, frac((p.x + h.x) / 0.006)) : 0.0;
                    float3 on = lerp(tint * 0.85, lerp(tint, white, 0.35), smoothstep(-1.0, 1.0, yN));
                    fill = lerp(float3(0.020, 0.034, 0.052), on, filled * (1.0 - segGap * 0.8));
                    fillA = 0.95;
                    edgeCol = tint * 0.8;
                    edgeA = pill ? 0.0 : 0.35;
                    glowA = pill ? 0.0 : 0.10 * state;
                }

                float3 col = fill;
                float a = fillA * inside;
                col = lerp(col, edgeCol, ring * edgeA);
                a = max(a, ring * edgeA);
                // outside the shape: a dark contact shadow close in, a soft accent halo further out
                float shade = outsideFall * shadeA * _GlowBoost;
                float halo = (1.0 - inside) * exp(-max(d, 0.0) / max(margin * 0.30, 1e-4)) * glowA;
                float3 outCol = lerp(float3(0.0, 0.0, 0.0), tint * 1.2, saturate(halo / max(halo + shade, 1e-4)));
                float outA = max(shade, halo);
                col = lerp(col, outCol, saturate(outA * (1.0 - inside) / max(a + outA, 1e-4)));
                a = max(a, outA);
                return half4(col, saturate(a * i.color.a));
            }
            ENDHLSL
        }
    }
}
