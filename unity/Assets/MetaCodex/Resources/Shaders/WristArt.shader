// MetaCodex wrist menu (UiWristMenu builds the mesh every frame it shows): holographic glass tiles, lines and ticks,
// glows, line icons (UiIconAtlas, a signed distance field), the projector beam from the wrist and the watch face at
// its core. URP, single-pass instanced stereo, premultiplied: light is added with alpha 0 over the passthrough, and the
// glass darkens what is behind it a little (alpha), so icons and labels read on any room. Every edge is anti-aliased
// from its own distance (fwidth), so it stays crisp at the wrist's 30 cm and when the hand comes close.
// Vertex data: colour (linear rgb, a = strength), uv0, uv1 = (x, y, kind, state), uv2 = extra, per kind:
//   0 tile    uv0 = (metres from the inner edge, metres along the arc from its start); x = radial width, y = arc length
//             at this radius; extra = (corner radius m, fingertip proximity 0..1, lit 0..1, press flash 0..1)
//   1 line    uv0.x across the quad 0..1, uv0.y along 0..1; x = line width m, y = quad width m;
//             extra = (glow, dashes along, dash duty, 0)
//   2 glow    uv0.x across 0..1, uv0.y along 0..1 (bright at 1): the radar sweep
//   3 icon    uv0 in the icon's quad 0..1; x = glow, y = reveal 0..1 (a scan line draws it upwards);
//             extra = the icon's rectangle in the atlas (offset xy, size zw)
//   4 beam    uv0.x across 0..1, uv0.y along 0..1 (0 at the wrist); x = 0 a ray, 1 the cone between them
//   5 face    uv0 -1..1 over the core's quad; y = the seconds hand 0..1 (clockwise from 12);
//             extra = (outline drawn 0..1, glass 0..1, outer ring traced 0..1, ticks drawn 0..1)
// state 0..1 brightens it (hover, lit, a press). Everything fades with the UI while the Horizon OS menu is open.
Shader "MetaCodex/WristArt"
{
    Properties
    {
        _IconAtlas ("Icons (signed distance)", 2D) = "black" {}
        _IconRange ("Icon distance range", Float) = 0.42
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent-20" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Art"
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

            TEXTURE2D(_IconAtlas);
            SAMPLER(sampler_IconAtlas);
            float _IconRange;

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 info : TEXCOORD1;
                float4 extra : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 info : TEXCOORD1;
                float4 extra : TEXCOORD2;
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
                o.extra = v.extra;
                return o;
            }

            // a hexagon with flat top and bottom, `r` its inner radius (Inigo Quilez); negative inside
            float sdHexagon(float2 p, float r)
            {
                const float3 k = float3(-0.866025404, 0.5, 0.577350269);
                p = abs(p);
                p -= 2.0 * min(dot(k.xy, p), 0.0) * k.xy;
                p -= float2(clamp(p.x, -k.z * r, k.z * r), r);
                return length(p) * sign(p.y);
            }

            // a thin line at distance 0 of `d`, `w` wide, anti-aliased by `aa`
            float LineAt(float d, float w, float aa) { return saturate((w * 0.5 - abs(d)) / aa + 0.5); }

            half4 Tile(Varyings i, half3 c, half s, float state)
            {
                float er0 = i.uv.x, er1 = i.info.x - i.uv.x, ea0 = i.uv.y, ea1 = i.info.y - i.uv.y;
                float ex = min(er0, er1), ey = min(ea0, ea1), cr = i.extra.x;
                float2 q = float2(cr - ex, cr - ey);
                float e = (q.x > 0.0 && q.y > 0.0) ? cr - length(q) : min(ex, ey); // metres inside the rounded outline
                float aa = max(fwidth(e), 1e-5);
                float cov = saturate(e / aa + 0.5);
                float radial = saturate(i.uv.x / max(i.info.x, 1e-5));          // 0 inner edge .. 1 outer edge
                float along = saturate(i.uv.y / max(i.info.y, 1e-5));
                float prox = i.extra.y, lit = i.extra.z, flash = i.extra.w;
                float lift = max(state, lit * 0.6);
                // the glass: deep and clear, lighter towards the outer edge, fine lines along the arc
                float scan = 0.5 + 0.5 * sin(i.uv.x * 2900.0);
                half fillA = s * cov * (0.40 + 0.20 * state + 0.08 * lit + 0.25 * flash) * (0.94 + 0.06 * scan);
                half3 deep = half3(0.006, 0.014, 0.026);
                half3 fill = lerp(deep, c * 0.20, (0.18 + 0.82 * radial * radial) * (0.30 + 0.70 * lift));
                // the light: a hair-thin rim (brighter on the outer edge, where the light catches it), a glow bleeding
                // in from it, a fainter inner line, a strip of light in the middle of the outer edge when lit or
                // hovered, and a slow sheen crossing the glass
                float rim = LineAt(e - 0.00042, 0.00040, aa) * lerp(0.42, 1.25, radial);
                float bleed = exp(-max(e, 0.0) / 0.0016);
                float bevel = LineAt(e - 0.0019, 0.00022, aa);
                float strip = (1.0 - smoothstep(0.0005, 0.0013, er1)) * smoothstep(0.26, 0.34, along) * (1.0 - smoothstep(0.66, 0.74, along));
                float sweep = frac(along * 0.7 + radial * 0.5 - _Time.y * 0.16);
                float sheen = pow(saturate(1.0 - abs(sweep - 0.5) * 5.0), 3.0);
                float3 light = c * (rim * (1.0 + 1.7 * state + 0.9 * prox)
                    + bleed * 0.16 * (1.0 + 1.5 * state)
                    + bevel * (0.10 + 0.45 * state)
                    + strip * (0.15 + 1.6 * lift)
                    + sheen * 0.07);
                light += half3(0.85, 0.95, 1.0) * flash * 0.55;
                half3 rgb = fill * fillA + light * s * cov;
                return half4(rgb, fillA);
            }

            half4 Line(Varyings i, half3 c, half s, float state)
            {
                float dist = abs(i.uv.x - 0.5) * i.info.y; // metres from the line's centre
                float aa = max(fwidth(dist), 1e-6);
                float core = saturate((i.info.x * 0.5 - dist) / aa + 0.5);
                float glow = exp(-dist / max(i.info.x * 1.4, 1e-5)) * i.extra.x;
                float dash = 1.0;
                if (i.extra.y > 0.5)
                {
                    float k = i.uv.y * i.extra.y;
                    float daa = max(fwidth(k), 1e-4);
                    float f = frac(k);
                    dash = saturate((i.extra.z - f) / daa + 0.5) * saturate(f / daa + 0.5);
                }
                half3 rgb = c * s * (core + glow * 0.55) * dash * (1.0 + 1.3 * state);
                return half4(rgb, s * core * dash * 0.10);
            }

            half4 Sweep(Varyings i, half3 c, half s)
            {
                float d = abs(i.uv.x - 0.5) * 2.0;
                float g = (1.0 - d * d) * i.uv.y * i.uv.y * i.uv.y;
                return half4(c * s * g, 0.0);
            }

            half4 IconFrag(Varyings i, half3 c, half s, float state)
            {
                float2 auv = i.extra.xy + saturate(i.uv) * i.extra.zw;
                float sd = SAMPLE_TEXTURE2D(_IconAtlas, sampler_IconAtlas, auv).r;
                float d = (0.5 - sd) * 2.0 * _IconRange;           // canvas units, < 0 inside a stroke
                float aa = max(fwidth(d), 1e-4);
                float core = saturate(0.5 - d / aa);
                // the glow fades to nothing before the edge of the icon's cell (no square of light around it)
                float halo = pow(saturate(1.0 - max(d, 0.0) / (_IconRange * 0.92)), 3.0) * (0.30 + 0.60 * state) * i.info.x;
                // drawn upwards by a scan line while it appears; erased downwards when it goes
                float rv = i.info.y;
                float scanY = lerp(-0.12, 1.12, rv);
                float vis = saturate((scanY - i.uv.y) / 0.035);
                float live = step(0.001, rv) * step(rv, 0.999);
                float scanGlow = exp(-abs(i.uv.y - scanY) * 26.0) * live;
                float scanLine = exp(-abs(i.uv.y - scanY) * 150.0) * live * saturate(1.0 - abs(i.uv.x - 0.5) * 2.2);
                half3 ink = lerp(c, half3(1.0, 1.0, 1.0), 0.28 + 0.42 * state);
                half3 rgb = s * (ink * core * 1.25 * vis + c * halo * vis + ink * core * scanGlow * 2.2 + c * scanLine * 0.9);
                return half4(rgb, s * core * vis * 0.32);
            }

            half4 Beam(Varyings i, half3 c, half s)
            {
                if (i.info.x < 0.5)
                {
                    float d = abs(i.uv.x - 0.5) * 2.0;
                    float core = exp(-d * d * 7.0);
                    float travel = frac(i.uv.y * 1.6 - _Time.y * 2.2);
                    float pulse = smoothstep(0.70, 0.97, travel) * (1.0 - smoothstep(0.97, 1.0, travel));
                    return half4(c * s * core * (0.25 + 0.75 * i.uv.y) * (0.65 + 1.2 * pulse), 0.0);
                }
                float side = 1.0 - abs(i.uv.x - 0.5) * 2.0;
                return half4(c * s * 0.085 * i.uv.y * i.uv.y * (0.6 + 0.4 * side), 0.0);
            }

            half4 Face(Varyings i, half3 c, half s, float state)
            {
                float2 p = i.uv;
                float r = length(p);
                float a01 = frac(atan2(p.x, p.y) / 6.2831853 + 1.0);             // 0 at 12 o'clock, clockwise
                float aa = max(fwidth(r), 1e-4);
                float aaA = aa / max(6.2831853 * r, 0.05);                         // the same in turns
                float drawn = i.extra.x, glass = i.extra.y, trace = i.extra.z, ticks = i.extra.w;
                // the hexagonal glass (a corner at 12), a turned hexagon inside it
                float dh = sdHexagon(p.yx, 0.70 * 0.8660254);
                float fillCov = saturate(-dh / aa + 0.5);
                half3 deep = half3(0.006, 0.016, 0.030);
                float lens = exp(-length(p - float2(-0.22, 0.30)) * 3.6);
                half fillA = s * fillCov * glass * (0.55 + 0.15 * state);
                half3 fill = lerp(deep, c * 0.22, saturate(0.15 + 0.35 * (1.0 - r) + 0.25 * lens + 0.3 * state));
                float reveal = saturate((drawn - a01) / max(aaA * 2.0, 0.004));
                float outline = LineAt(dh, 0.026, aa) * reveal;
                float head = exp(-abs(a01 - drawn) * 60.0) * step(0.001, drawn) * step(drawn, 0.999) * LineAt(dh, 0.05, aa);
                float inner = LineAt(sdHexagon(p, 0.38 * 0.8660254), 0.012, aa) * glass;
                // sixty ticks of a watch, every fifth longer; drawn round from 12
                float ta = a01 * 60.0;
                float td = abs(frac(ta + 0.5) - 0.5);                               // ticks' spacing units from the nearest
                float isMajor = step(abs(frac((floor(ta + 0.5)) / 5.0 + 0.5) - 0.5), 0.05);
                float r0 = lerp(0.805, 0.780, isMajor), r1 = lerp(0.865, 0.885, isMajor);
                float inBand = saturate((r - r0) / aa + 0.5) * saturate((r1 - r) / aa + 0.5);
                float tw = lerp(0.11, 0.20, isMajor);
                float tick = saturate((tw * 0.5 - td) / max(aaA * 60.0, 0.02) + 0.5) * inBand;
                tick *= saturate((ticks - a01) / 0.01) * lerp(0.45, 1.0, isMajor);
                // the seconds hand: a bright point running round the ticks
                float sa = i.info.y * 6.2831853;
                float2 sp = float2(sin(sa), cos(sa)) * 0.835;
                float sec = exp(-length(p - sp) * 34.0) * ticks;
                // the outer ring, traced round with a bright head while it appears
                float ringRev = saturate((trace - a01) / max(aaA * 2.0, 0.004));
                float ring = LineAt(r - 0.955, 0.016, aa) * ringRev;
                float ringHead = exp(-abs(a01 - trace) * 45.0) * step(0.001, trace) * step(trace, 0.999) * LineAt(r - 0.955, 0.06, aa);
                float3 light = c * (outline * (1.25 + 1.4 * state) + head * 2.2 + inner * (0.35 + 0.5 * state) + tick * 0.62 + ring * 0.85 + ringHead * 2.0)
                    + half3(1.0, 0.72, 0.30) * sec * 1.1;
                half3 rgb = fill * fillA + light * s;
                return half4(rgb, fillA);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float shown = 1.0 - saturate(_McxUiHide);
                int kind = (int)round(i.info.z);
                float state = saturate(i.info.w);
                half3 c = i.color.rgb;
                half s = i.color.a;
                half4 o;
                if (kind == 0) o = Tile(i, c, s, state);
                else if (kind == 1) o = Line(i, c, s, state);
                else if (kind == 2) o = Sweep(i, c, s);
                else if (kind == 3) o = IconFrag(i, c, s, state);
                else if (kind == 4) o = Beam(i, c, s);
                else o = Face(i, c, s, state);
                return o * shown;
            }
            ENDHLSL
        }
    }
    FallBack Off
}
