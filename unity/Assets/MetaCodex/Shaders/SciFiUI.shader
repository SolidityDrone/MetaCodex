// MetaCodex UI shapes: every panel, button, header, selection frame and bar is one quad drawn by this
// shader from per-vertex data, so all shapes of a panel batch into one mesh and one material.
//   COLOR      tint (linear): border, glow and accent colour
//   TEXCOORD0  uv over the quad (the quad is the shape plus a glow margin on every side)
//   TEXCOORD1  x,y = shape size in metres, z = glow margin in metres, w = kind
//   TEXCOORD2  x = chamfer (corner cut), y = small cut (panels) or fill style (buttons: 0 normal, 1 filled, 2 ghost,
//              3 list row), z = border width, w = state 0..1
// Kinds: 0 panel body, 1 button, 2 header bar, 3 selection frame, 4 bar (state = fill), 5 line, 6 dot.
// The look: a sci-fi command console in the spirit of RTS game interfaces (an original design, no game assets):
// angular chamfered frames of steel lit from above, holographic glass with a hexagon lattice and a grid of points
// printed on its surface (flat: 0.7.9 drew them behind the surface with parallax and the owner did not want windows to
// look deep), light pooling on the lattice under a fingertip or a ray's cursor, corner brackets, a ruler down the left
// edge, a conduit of energy along the bottom, a stepped energy
// bar under each title with a pulse running in it, command-card buttons with a bevel that charge up when hovered,
// quiet list rows the accent floods into when chosen. Everything is computed per pixel from the vertex data.
// A panel is cast by a line of light (McxPanelFx.hlsl): behind the line the glass is a hot lattice whose cells close
// one by one and cool into glass; controls come online just behind it.
// Two passes: PanelDepth writes depth where a panel body is opaque (so text and images of windows behind it are
// hidden, as they render later), then the colour pass. Alpha is blended with One OneMinusSrcAlpha: it only ever
// grows, so a translucent button, row or shadow drawn over an opaque panel never opens a hole to the passthrough
// (the eye buffer's alpha decides how much of the real world shows).
Shader "MetaCodex/SciFiUI"
{
    Properties
    {
        _Scanlines ("Scanlines (unused)", Range(0, 1)) = 1
        _GlowBoost ("Shadow", Range(0, 2)) = 1
        _Opacity ("Panel opacity", Range(0.3, 1)) = 0.9
        _Motion ("Motion speed (unused)", Range(0, 2)) = 1
        _Grid ("Grid (unused)", Range(0, 1)) = 1
        _Accent ("Accent colour (linear rgb)", Vector) = (0.0356, 0.631, 1, 1)
        _PanelIndex ("Panel slot (set per renderer)", Float) = -1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Assets/MetaCodex/Shaders/McxOcclusion.hlsl"
        #include "Assets/MetaCodex/Shaders/McxPanelFx.hlsl"

        // the hands' pointers (UiInputRouter): xyz = a fingertip, or a point just in front of a ray's cursor (world),
        // w = how strong its light is (0 = none). Two plain globals: no array indexed in a loop (see Orb.shader).
        float4 _McxPointer0, _McxPointer1;

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
            float4 _Accent;
        CBUFFER_END

        float Bump01(float x) { return sin(3.14159265 * saturate(x)); }

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
            float3 positionWS : TEXCOORD3; // after the bend: what real things in front are compared with
            float3 scan : TEXCOORD5;       // xy = how the distance behind the scan line changes along the panel's x and
                                           // y (per metre of panel space), z = metres behind the scan line (10 at rest)
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings vert(Attributes v)
        {
            Varyings o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            float r = _PanelIndex >= 0.0 ? _PanelBend[(int)_PanelIndex].x : 0.0;
            float3 bent = BendPanel(v.positionOS.xyz, r);
            o.positionWS = TransformObjectToWorld(bent);
            o.positionCS = TransformWorldToHClip(o.positionWS);
            o.color = v.color;
            o.uv = v.uv;
            o.shape = v.shape;
            o.style = v.style;
            // the panel's own tangent at this vertex (it turns along a bent panel): how the scan line's distance changes
            float a = r > 0.01 ? v.positionOS.x / r : 0.0;
            float3 tOS = float3(cos(a), 0.0, -sin(a));
            float3 tWS = mul((float3x3)UNITY_MATRIX_M, tOS), uWS = mul((float3x3)UNITY_MATRIX_M, float3(0.0, 1.0, 0.0));
            float3 n = _PanelIndex >= 0.0 ? _PanelScan[(int)_PanelIndex].xyz : float3(0.0, 0.0, 0.0);
            o.scan = float3(dot(n, tWS), dot(n, uWS), McxBehindScan(_PanelIndex, o.positionWS));
            return o;
        }

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
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                int kind = (int)round(i.shape.w);
                float4 fx = McxPanelFx(_PanelIndex);
                // a panel still being cast hides nothing where its glass is not closed yet
                if (kind != 0 || _Opacity < 0.75 || i.scan.z < 0.06 || fx.z > 0.3 || McxVisibility(i.positionWS) < 0.5) discard;
                float2 h = i.shape.xy * 0.5;
                float2 p = (i.uv - 0.5) * (i.shape.xy + 2.0 * i.shape.z);
                if (sdChamfer(p, h, i.style.x, i.style.y) > 0.0) discard;
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

            // A hexagon lattice of cell size `cell` (metres): returns 0 at a cell's centre .. 0.5 at its border, and
            // `toCentre`, the way from p to the centre of its cell (metres).
            float HexCell(float2 p, float cell, out float2 toCentre)
            {
                const float2 s = float2(1.0, 1.7320508);
                float2 q = p / cell;
                float2 a = (frac(q / s) - 0.5) * s;
                float2 b = (frac((q - s * 0.5) / s) - 0.5) * s;
                float2 g = dot(a, a) < dot(b, b) ? a : b;
                toCentre = -g * cell;
                g = abs(g);
                return max(dot(g, float2(0.5, 0.8660254)), g.x);
            }

            // the lattice's lines, `width` of a cell wide, from HexCell's distance; `fw` = one pixel in cells
            float HexLine(float hd, float width, float fw) { return smoothstep(0.5 - width - fw, 0.5 - width + fw, hd); }

            // 1 on a line `w` wide (metres) at distance 0 of `d`, anti-aliased by `aa`
            float LineAt(float d, float w, float aa) { return saturate(0.5 - (abs(d) - w * 0.5) / aa); }

            half4 fragBody(Varyings i);

            // everything a panel draws fades where something real (a hand, a person) is in front of it
            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half4 c = fragBody(i);
                float4 fx = McxPanelFx(_PanelIndex);
                if (fx.x > 0.5)
                {
                    // cast (UiPanelFx): a control comes online just behind the scan line, its colour running hot for a
                    // moment (the body builds its own glass in fragBody)
                    int kind = (int)round(i.shape.w);
                    if (kind != 0)
                    {
                        c.a *= (half)McxBuilt(i.scan.z);
                        c.rgb += i.color.rgb * (half)(McxHeat(i.scan.z) * 0.9);
                    }
                }
                c.a *= (half)(1.0 - saturate(fx.z));
                c.a *= (half)McxVisibility(i.positionWS);
                return c;
            }

            half4 fragBody(Varyings i)
            {
                float2 size = i.shape.xy;
                float margin = i.shape.z;
                int kind = (int)round(i.shape.w);
                float cBig = i.style.x, style2 = i.style.y, border = max(i.style.z, 0.0004), state = saturate(i.style.w);
                float2 h = size * 0.5;
                float2 p = (i.uv - 0.5) * (size + 2.0 * margin); // metres from the shape centre, y up
                float3 tint = i.color.rgb;
                float3 accent = _Accent.rgb;
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
                float px = aa; // about one pixel, in metres (a distance field's gradient is 1: its fwidth is the pixel size)
                float inside = saturate(0.5 - d / aa);
                float ring = saturate(0.5 - (abs(d + border * 0.5) - border * 0.5) / aa);
                float outsideFall = (1.0 - inside) * exp(-max(d, 0.0) / max(margin * 0.42, 1e-4));

                float3 fill = 0;
                float fillA = 0;
                float3 edgeCol = tint;
                float edgeA = 0.0;
                float glowA = 0.0;      // accent halo outside the shape
                float shadeA = 0.0;     // dark contact shadow just outside
                float4 pfx = McxPanelFx(_PanelIndex);
                float bd = i.scan.z;    // metres behind the scan line while the panel is cast (10 at rest)
                float heat = 0.0;

                if (kind == 0) // panel: holographic glass in a steel frame
                {
                    float3 lo = float3(0.0045, 0.0125, 0.0245), hi = float3(0.017, 0.042, 0.076);
                    fill = lerp(lo, hi, smoothstep(-1.1, 1.0, yN));
                    fill *= 1.0 - 0.30 * smoothstep(0.55, 1.35, length(float2(xN, yN)));  // darker into the corners
                    float nearEdge = exp(d / 0.05);                                        // 1 at the frame, fading inwards
                    // the hexagon lattice on the glass: strongest at the frame
                    float2 toCentre;
                    float hd = HexCell(p, 0.022, toCentre);
                    float fw = px / 0.022;
                    float farFade = saturate(1.3 - fw * 10.0);                               // lines thinner than a pixel fade out
                    float lattice = HexLine(hd, 0.055, fw) * farFade;
                    fill += tint * lattice * (0.030 + 0.11 * nearEdge);
                    // a grid of points between the cells
                    float2 g2 = frac(p / 0.011) - 0.5;
                    float dots = saturate((0.085 - length(g2)) / (px / 0.011) + 0.5) * saturate(1.5 - px / 0.0014);
                    fill += accent * dots * 0.050 * (0.35 + 0.65 * smoothstep(-0.2, 1.0, yN));
                    fill += tint * 0.040 * smoothstep(0.55, 1.0, yN);                      // light from the top edge
                    // a fingertip, or a ray's cursor, lights the lattice round it
                    float3 dq0 = i.positionWS - _McxPointer0.xyz, dq1 = i.positionWS - _McxPointer1.xyz;
                    float pl = _McxPointer0.w * 0.0009 / (dot(dq0, dq0) + 0.0009) + _McxPointer1.w * 0.0009 / (dot(dq1, dq1) + 0.0009);
                    fill += accent * pl * (0.10 + 0.9 * lattice) * 0.55;
                    // the glass sits in the frame: an inner inset line
                    fill = lerp(fill, tint * 0.55, LineAt(d + 0.0032, 0.0009, aa) * 0.55);
                    // a ruler down the left edge: a tick every 10 mm, a longer one every 50 mm
                    if (p.x < -h.x + 0.011 && h.y > 0.12)
                    {
                        float ty = (h.y - p.y) / 0.010;
                        float major = step(abs(frac(floor(ty + 0.5) / 5.0 + 0.5) - 0.5), 0.05);
                        float xin = p.x + h.x - 0.0050;
                        float tick = LineAt((frac(ty + 0.5) - 0.5) * 0.010, 0.00064, aa) * step(0.0, xin) * step(xin, lerp(0.0028, 0.0052, major));
                        tick *= smoothstep(h.y - 0.034, h.y - 0.060, abs(p.y));
                        fill = lerp(fill, tint * 1.1, tick * 0.50);
                    }
                    // a conduit along the bottom, from the right cut: an accent line fading to the left, a pulse in it
                    if (p.y < -h.y + 0.016 && h.y > 0.05)
                    {
                        float cy = p.y + h.y - 0.0062;
                        float cx = h.x - cBig - 0.004 - p.x;                               // metres from its right end
                        float clen = min(0.22, h.x * 0.9);
                        float on = step(0.0, cx) * step(cx, clen);
                        float cfade = pow(saturate(1.0 - cx / clen), 1.5);
                        float pulse = exp(-abs(cx - frac(t * 0.22) * clen * 1.3) / 0.012);
                        fill = lerp(fill, accent * 1.1, LineAt(cy, 0.00064, aa) * on * saturate(0.25 + 0.55 * cfade + 0.9 * pulse * cfade));
                        fill += accent * exp(-abs(cy) / 0.0022) * on * cfade * (0.05 + 0.30 * pulse);
                    }
                    fillA = _Opacity;
                    if (pfx.x > 0.5)
                    {
                        // being cast: behind the scan line the glass is a hot lattice; each cell closes from its rim
                        // inwards as the line leaves it behind, and cools into glass
                        heat = bd > 0.0 ? exp(-bd / 0.045) : 0.0;
                        float cb = bd - dot(toCentre, i.scan.xy);                          // the cell's centre behind the line
                        float cf = saturate(cb / 0.040);                                   // 0 just born .. 1 closed
                        float open = smoothstep(cf * 0.5, cf * 0.5 + 0.03, 0.5 - hd) * step(cf, 0.999);
                        fill += accent * HexLine(hd, 0.075, fw) * heat * 1.5 + (accent * 0.6 + 0.4) * heat * heat * heat * 0.10;
                        fillA *= (1.0 - open * 0.92) * step(0.0, bd);
                    }
                    fill += accent * lattice * saturate(pfx.y) * 0.5;                      // the flash is energy in the lattice
                    float lit = smoothstep(-0.9, 1.0, yN);                                 // the frame is lit from above
                    float3 steel = lerp(float3(0.16, 0.22, 0.30), float3(0.58, 0.68, 0.80), lit);
                    // the frame glows in its colour near each corner
                    float cx2 = smoothstep(h.x - 0.040, h.x - 0.030, abs(p.x)), cy2 = smoothstep(h.y - 0.040, h.y - 0.030, abs(p.y));
                    float bracket = max(cx2, cy2);
                    float sweep = 0.0;
                    if (state > 0.01)
                    {
                        // a light running round the frame when the panel is lit (a toast, a focused window)
                        float ang = atan2(p.y * h.x, p.x * h.y) / 6.2831853 + 0.5;
                        sweep = exp(-frac(ang - t * 0.07) * 24.0) * state;
                    }
                    edgeCol = lerp(steel, tint * 1.25 + 0.12, saturate(bracket * 0.85 + state * 0.6 + sweep));
                    edgeCol += accent * (heat * 1.6 + saturate(pfx.y) * 1.1) + accent * pl * 0.8; // the frame carries the cast's heat and flash
                    edgeA = 0.85 * step(0.0, bd);
                    glowA = 0.10 + 0.22 * state + 0.5 * heat;
                    shadeA = 0.30 * step(0.0, bd);
                }
                else if (kind == 1) // button: a command-card plate
                {
                    float topLight = smoothstep(0.2, 1.0, yN);
                    float hover = saturate(state * 4.0) * (1.0 - smoothstep(0.5, 0.65, state));   // hovered, not chosen or pressed
                    float sw = (xN * 0.5 + 0.5 + yN * 0.12 - (frac(t * 0.8) * 2.4 - 0.7)) / 0.10;
                    float sheen = exp(-sw * sw) * hover;                                           // a light crossing it while hovered
                    if (style2 > 2.5) // list row: quiet glass, a notch at the left, a hair line under it; the accent floods in from the left when chosen
                    {
                        float on = saturate(state * 1.35);
                        float chosen = smoothstep(0.45, 0.8, state);
                        float u = xN * 0.5 + 0.5;
                        fill = lerp(float3(0.020, 0.040, 0.066), float3(0.032, 0.062, 0.098), smoothstep(-1.0, 1.0, yN)) + tint * (0.05 + 0.50 * exp(-u * 2.2)) * on;
                        float notch = saturate(0.5 - max(abs(p.x + h.x - 0.0016) - 0.0011, abs(p.y) - h.y * 0.56) / aa);
                        fill = lerp(fill, lerp(float3(0.30, 0.40, 0.52), tint * 1.35 + 0.1, on), notch);
                        float under = LineAt(p.y + h.y - 0.0004, 0.0007, aa);
                        fill = lerp(fill, lerp(float3(0.20, 0.28, 0.38), tint, on * 0.8), under * (0.55 + 0.4 * on) * (1.0 - 0.6 * u));
                        fill += (tint * 0.6 + 0.4) * sheen * 0.10;
                        fillA = 0.42 + 0.50 * on;
                        edgeCol = lerp(float3(0.30, 0.40, 0.52), tint * 1.25, on);
                        edgeA = 0.10 + 0.80 * chosen;
                        glowA = 0.20 * chosen;
                    }
                    else if (style2 > 1.5) // ghost: invisible until hovered
                    {
                        fill = tint * 0.22 + white * 0.03;
                        fillA = 0.6 * state;
                        edgeCol = tint * 1.2;
                        edgeA = 0.9 * state;
                        glowA = 0.25 * state;
                    }
                    else if (style2 > 0.5) // filled: primary / danger, charged with its colour
                    {
                        float2 hc;
                        float hx1 = HexCell(p, 0.010, hc);
                        fill = lerp(tint * 0.38, tint * 0.80, smoothstep(-1.0, 1.0, yN)) * (0.85 + 0.3 * state) + white * 0.06 * topLight;
                        fill += tint * 0.25 * HexLine(hx1, 0.10, px / 0.010) * saturate(1.3 - px / 0.010 * 6.0);
                        fill += white * sheen * 0.10;
                        fillA = 0.97;
                        edgeCol = lerp(tint, white, 0.45);
                        edgeA = 0.75 + 0.25 * state;
                        glowA = 0.16 + 0.30 * state;
                    }
                    else // normal: a dark steel-blue plate that charges up with the accent colour
                    {
                        float3 plate = lerp(float3(0.016, 0.034, 0.058), float3(0.040, 0.078, 0.120), smoothstep(-1.0, 1.0, yN));
                        fill = plate + tint * (0.42 * state) + white * 0.025 * topLight;
                        fill += tint * 0.35 * exp(d / 0.004) * state;                    // light pooling at the rim when lit
                        // a bevel: a hair of light under the top edge, a hair of shade over the bottom edge
                        float bev = LineAt(d + 0.0024, 0.0006, aa);
                        fill += white * bev * smoothstep(0.3, 1.0, yN) * 0.055;
                        fill *= 1.0 - bev * smoothstep(-0.3, -1.0, yN) * 0.45;
                        // accent ticks hugging the two big cuts
                        float tk = LineAt(d + 0.0042, 0.0007, aa) * step(p.x * p.y, 0.0) * step(h.x - cBig - 0.004, abs(p.x)) * step(h.y - cBig - 0.004, abs(p.y));
                        fill = lerp(fill, tint * 1.2, tk * (0.40 + 0.6 * state));
                        fill += (tint * 0.6 + 0.4) * sheen * 0.16;
                        fillA = 0.95;
                        edgeCol = lerp(float3(0.30, 0.40, 0.52) * (0.7 + 0.5 * topLight), tint * 1.25, saturate(state * 1.2));
                        edgeA = 0.7 + 0.3 * state;
                        glowA = 0.22 * state;
                    }
                    shadeA = style2 > 1.5 ? 0.0 : 0.12;
                }
                else if (kind == 2) // header: a stepped energy bar under the title, a lead block before it, hatched at the right
                {
                    float bottom = -h.y;
                    // the bar steps up 4.5 mm two thirds of the way along: ____/----
                    const float rise = 0.0045;
                    float ramp = saturate((p.x - h.x * 0.30) / rise);
                    float lineY = bottom + 0.0006 + ramp * rise;
                    float lineD = abs(p.y - lineY) * ((ramp > 0.0 && ramp < 1.0) ? 0.70710678 : 1.0);
                    float barLine = saturate(0.5 - (lineD - 0.0005) / aa);
                    float along = smoothstep(1.0, -1.0, xN);                      // brighter on the left
                    float seg = step(p.x, -h.x + 0.09) * step(-h.x, p.x);         // a glowing segment at the left end
                    float above = step(lineY, p.y);
                    float hatch = step(0.5, frac((p.x - p.y) * 90.0)) * smoothstep(0.55, 0.95, xN) * smoothstep(bottom + rise, bottom + rise + 0.012, p.y) * above;
                    float pulse = exp(-abs(p.x + h.x - frac(t * 0.13) * h.x * 2.6) / 0.018); // a pulse running along the bar
                    float3 col = tint * (0.55 + 0.9 * seg + 1.2 * pulse) + white * 0.15 * seg;
                    float a = barLine * (0.35 + 0.65 * along + 0.6 * seg + 0.8 * pulse) + hatch * 0.10;
                    float under = exp(-max(p.y - lineY, 0.0) / 0.004) * above * along * 0.10; // light spilling up from the bar
                    // the lead: a bright block before the title
                    float lead = saturate(0.5 - max(abs(p.x + h.x - 0.0078) - 0.0013, abs(p.y + 0.002) - 0.0072) / aa);
                    col = lerp(col, tint * 1.3 + 0.15, lead);
                    a = max(saturate(max(a, under)), lead * 0.95);
                    return half4(col, a * i.color.a);
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
                if (kind == 0)
                {
                    // corner brackets: the frame's outline offset 4 mm outwards, near each corner only, with a node of light
                    // in the middle of each cut; drawn exactly like the transition's brackets (UiPanelFx: a 1.2 mm line
                    // with a soft glow, the same node), which hand over to these (1 - hidden) once they have locked on:
                    // the last frames of a cast and the panel at rest look the same (owner 2026-11-09)
                    float cc = (p.x * p.y < 0.0) ? cBig : style2;
                    float arm = clamp(max(min(0.030, min(h.x, h.y) * 0.32), cc + 0.006), 0.0, min(h.x, h.y));
                    float near = step(h.x - arm, abs(p.x)) * step(h.y - arm, abs(p.y));
                    near *= step(0.055, min(h.x, h.y)); // not on a small card (a callout, the voice HUD): brackets would crowd it
                    float keep = near * (1.0 - saturate(pfx.w));
                    if (keep > 0.0)
                    {
                        float off = abs(d - 0.004) - 0.0006;
                        float brk = saturate(0.5 - off / aa);
                        float brkGlow = exp(-max(off, 0.0) / 0.0017) * 0.45;
                        // the node: the middle of the cut's offset line (as UiPanelFx draws it)
                        float2 sg = float2(p.x >= 0.0 ? 1.0 : -1.0, p.y >= 0.0 ? 1.0 : -1.0);
                        const float o = 0.004, k = 0.41421356;
                        float2 a1 = sg * float2(h.x + o, h.y - cc + o * k), b1 = sg * float2(h.x - cc + o * k, h.y + o);
                        float2 m = (a1 + b1) * 0.5, u = normalize(b1 - a1);
                        float2 q = p - m;
                        float dn = length(q - clamp(dot(q, u), -0.0015, 0.0015) * u) - 0.0014;
                        float node = saturate(0.5 - dn / aa);
                        float3 lineCol = tint * 1.35 + 0.06, hot = lerp(tint, white, 0.55) * 1.2;
                        float lit = saturate(brk + brkGlow) * keep, nodeA = node * keep;
                        col = lerp(col, lineCol, saturate(brk * keep + brkGlow * keep * 0.6));
                        col = lerp(col, hot, nodeA);
                        a = max(a, max(lit * 0.82, nodeA * 0.95));
                    }
                }
                return half4(col, saturate(a * i.color.a));
            }
            ENDHLSL
        }
    }
}
