// MetaCodex, the Whisp (UiOrb): a small prismatic crystal wisp that floats by the user, casts the panels and speaks.
// One small mesh made once by UiOrb (98 triangles: a halo, a tail ribbon, a heart, a crystal cluster, four shards; the
// part is TEXCOORD0.x), shaped and moved here in the vertex shader from a few uniforms. The fragment is a few dozen
// operations: a fresnel hue shift over the tint (each facet a little apart; a back facet, seen through the front ones,
// half a turn on), glowing facet edges (barycentric, TEXCOORD1), the light inside (the ray's distance to the centre),
// the halo, ripples and the wake flare. An original design. Far cheaper than the orb before it: the halo square is 1.7
// radii instead of 2.3, the room's depth is looked up with a matrix per vertex, and no pixel runs plasma or rings.
// Straight-line maths as since 0.7.10 (the headset once showed flat cream blocks over the old orb, never the Editor):
// no loops, no uniform arrays, no derivatives, every input an ordinary material property in UnityPerMaterial.
// URP, single-pass instanced stereo, premultiplied: its light is added over the passthrough and the crystal's glass
// darkens what is behind it a little (alpha), so it reads in a bright room. A first pass writes the crystal's depth,
// pushed back a radius (its own facets and halo still pass): a hand behind it does not clear it (FocusMesh draws the
// hands last, depth-tested), a fingertip in front of it still shows.
Shader "MetaCodex/Orb"
{
    Properties
    {
        _OrbCenter ("Centre (xyz), radius (w)", Vector) = (0, 0, 0, 0.032)
        _OrbState ("Energy, pulse, appear, exposure", Vector) = (0.35, 0, 0, 1.5)
        _OrbTint ("Tint (linear)", Vector) = (0.036, 0.63, 1, 1)
        _OrbDeep ("Body glass (linear)", Vector) = (0.01, 0.03, 0.075, 1)
        _OrbGold ("Gold (linear)", Vector) = (1, 0.6, 0.21, 1)
        _OrbPhaseA ("Prism phase 0..1 (x), wake flare 0..1 (y)", Vector) = (0, 0, 0, 1.4)
        _OrbPhaseB ("Prism strength (w)", Vector) = (0, 0, 0, -0.9)
        _OrbRotA0 ("Crystal rotation, row 0", Vector) = (1, 0, 0, 0)
        _OrbRotA1 ("Crystal rotation, row 1", Vector) = (0, 1, 0, 0)
        _OrbRotA2 ("Crystal rotation, row 2", Vector) = (0, 0, 1, 0)
        _OrbRotB0 ("Heart rotation, row 0", Vector) = (1, 0, 0, 0)
        _OrbRotB1 ("Heart rotation, row 1", Vector) = (0, 1, 0, 0)
        _OrbRotB2 ("Heart rotation, row 2", Vector) = (0, 0, 1, 0)
        _OrbLook ("Where it attends (xyz), light wave strength (w)", Vector) = (0, 0, -1, 0)
        _OrbShield ("Light wave 0..1 (x), halo half size in radii (y)", Vector) = (1, 2.3, 0, 0)
        _OrbRipples ("Ripple ages (< 0: none)", Vector) = (-1, -1, -1, -1)
        _OrbRing0N ("Tail point 1 (radii), tail width (w)", Vector) = (0, 1, 0, 0)
        _OrbRing0U ("Tail point 2 (radii), tail brightness (w)", Vector) = (1, 0, 0, 0)
        _OrbRing0P ("Spare", Vector) = (1.3, 0.02, 0, 0)
        _OrbRing0Q ("Spare", Vector) = (0, 1, 1, 0)
        _OrbRing1N ("Shard tumble angles", Vector) = (0, 1, 0, 0)
        _OrbRing1U ("Spare", Vector) = (1, 0, 0, 0)
        _OrbRing1P ("Spare", Vector) = (1.46, 0.012, 0, 0)
        _OrbRing1Q ("Spare", Vector) = (0, 1, 0, 0)
        _OrbRing2N ("Spare", Vector) = (0, 1, 0, 0)
        _OrbRing2U ("Spare", Vector) = (1, 0, 0, 0)
        _OrbRing2P ("Spare", Vector) = (1.64, 0.009, 0, 0)
        _OrbRing2Q ("Spare", Vector) = (0, 1, 1, 0)
        _OrbMote0 ("Shard 0 place (radii), size (w)", Vector) = (0, 0, 0, 0)
        _OrbMote1 ("Shard 1 place (radii), size (w)", Vector) = (0, 0, 0, 0)
        _OrbMote2 ("Shard 2 place (radii), size (w)", Vector) = (0, 0, 0, 0)
        _OrbMote3 ("Shard 3 place (radii), size (w)", Vector) = (0, 0, 0, 0)
        _OrbMoteW ("Shards: how bright", Vector) = (0, 0, 0, 0)
        _OrbMoteGold ("Shards: a message not read yet (gold)", Vector) = (0, 0, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+15" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Assets/MetaCodex/Shaders/McxOcclusion.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _OrbCenter, _OrbState, _OrbTint, _OrbDeep, _OrbGold, _OrbPhaseA, _OrbPhaseB;
            float4 _OrbRotA0, _OrbRotA1, _OrbRotA2, _OrbRotB0, _OrbRotB1, _OrbRotB2;
            float4 _OrbLook, _OrbShield, _OrbRipples;
            float4 _OrbRing0N, _OrbRing0U, _OrbRing0P, _OrbRing0Q;
            float4 _OrbRing1N, _OrbRing1U, _OrbRing1P, _OrbRing1Q;
            float4 _OrbRing2N, _OrbRing2U, _OrbRing2P, _OrbRing2Q;
            float4 _OrbMote0, _OrbMote1, _OrbMote2, _OrbMote3, _OrbMoteW, _OrbMoteGold;
        CBUFFER_END

        // the mesh (UiOrb.BuildMesh): positions in radii; TEXCOORD0 = (part, shard index, facet hue, -): part 0 the crystal,
        // 1 the heart, 2 a shard, 3 the halo, 4 the tail; TEXCOORD1 = a facet corner's barycentric place (xyz);
        // TEXCOORD2 = a shard's tumble axis (xyz)
        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 uv0 : TEXCOORD0;
            float4 uv1 : TEXCOORD1;
            float4 uv2 : TEXCOORD2;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
            float4 info : TEXCOORD2;   // part, gold (a shard while a message waits), facet hue, brightness (a shard)
            float3 bary : TEXCOORD3;   // a facet: barycentric; the halo and the tail: their place on the square or ribbon
            float4 occ : TEXCOORD4;    // McxOcclusionCoord: linear in the position, so it interpolates exactly
            UNITY_VERTEX_OUTPUT_STEREO
        };

        #define TURN(r0, r1, r2, p) float3(dot(r0.xyz, p), dot(r1.xyz, p), dot(r2.xyz, p))

        // p turned by the angle t round the unit axis a
        float3 SpinAround(float3 p, float3 a, float t)
        {
            float s = sin(t), c = cos(t);
            return p * c + cross(a, p) * s + a * (dot(a, p) * (1.0 - c));
        }

        // every part to its place this frame; `depthOnly`: the crystal alone, pushed back a radius from the eye (the other
        // parts fold to one point and draw nothing)
        Varyings vertAt(Attributes v, float depthOnly)
        {
            Varyings o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            float3 c = _OrbCenter.xyz;
            float3 toCam = GetCameraPositionWS() - c;
            toCam /= max(length(toCam), 1e-4);
            float3 right = cross(float3(0.0, 1.0, 0.0), toCam);
            right = dot(right, right) < 1e-6 ? float3(1.0, 0.0, 0.0) : normalize(right);
            float3 up = cross(toCam, right);
            float kind = v.uv0.x;
            float3 p = v.positionOS.xyz;
            float3 n = v.normalOS;
            float3 off = 0;
            float3 nw = toCam;
            float gold = 0.0, bright = 1.0;
            if (kind < 0.5)
            {
                // the crystal: the cluster turned by RotA, a little larger on a pulse
                off = TURN(_OrbRotA0, _OrbRotA1, _OrbRotA2, p) * (1.0 + 0.05 * _OrbState.y);
                nw = TURN(_OrbRotA0, _OrbRotA1, _OrbRotA2, n);
            }
            else if (kind < 1.5)
            {
                // the heart: turned the other way by RotB; it swells with the energy and the voice
                off = TURN(_OrbRotB0, _OrbRotB1, _OrbRotB2, p) * (0.75 + 0.35 * saturate(_OrbState.x) + 0.25 * _OrbState.y);
                nw = TURN(_OrbRotB0, _OrbRotB1, _OrbRotB2, n);
            }
            else if (kind < 2.5)
            {
                // a shard: its place and size (Mote k), tumbling round its own axis (Ring1N k)
                float k = v.uv0.y;
                float4 m = k < 0.5 ? _OrbMote0 : (k < 1.5 ? _OrbMote1 : (k < 2.5 ? _OrbMote2 : _OrbMote3));
                float ang = k < 0.5 ? _OrbRing1N.x : (k < 1.5 ? _OrbRing1N.y : (k < 2.5 ? _OrbRing1N.z : _OrbRing1N.w));
                gold = k < 0.5 ? _OrbMoteGold.x : (k < 1.5 ? _OrbMoteGold.y : (k < 2.5 ? _OrbMoteGold.z : _OrbMoteGold.w));
                bright = k < 0.5 ? _OrbMoteW.x : (k < 1.5 ? _OrbMoteW.y : (k < 2.5 ? _OrbMoteW.z : _OrbMoteW.w));
                float3 ax = v.uv2.xyz * rsqrt(max(dot(v.uv2.xyz, v.uv2.xyz), 1e-8));
                off = m.xyz + SpinAround(p, ax, ang) * m.w;
                nw = SpinAround(n, ax, ang);
            }
            else if (kind < 3.5)
            {
                // the halo: a square turned to this eye
                off = (right * p.x + up * p.y) * _OrbShield.y;
            }
            else
            {
                // the tail: a ribbon along a curve from the centre through P1 to P2 (Ring0N, Ring0U), turned to this eye,
                // narrowing to its end
                float tt = p.x;
                float3 p1 = _OrbRing0N.xyz, p2 = _OrbRing0U.xyz;
                float3 q = 2.0 * (1.0 - tt) * tt * p1 + tt * tt * p2;
                float3 d = 2.0 * (1.0 - tt) * p1 + 2.0 * tt * (p2 - p1);
                float3 side = cross(d, toCam);
                side = dot(side, side) < 1e-8 ? right : normalize(side);
                off = q + side * (p.y * _OrbRing0N.w * (1.0 - 0.85 * tt));
            }
            if (depthOnly > 0.5) off = kind < 0.5 ? off - toCam : float3(0.0, 0.0, 0.0);
            o.positionWS = c + off * _OrbCenter.w;
            o.positionCS = TransformWorldToHClip(o.positionWS);
            o.normalWS = nw;
            o.info = float4(kind, gold, v.uv0.z, bright);
            o.bary = kind < 2.5 ? v.uv1.xyz : float3(p.xy, 0.0);
            o.occ = McxOcclusionCoord(o.positionWS);
            return o;
        }
        ENDHLSL

        Pass
        {
            Name "OrbDepth"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex vertDepth
            #pragma fragment fragDepth
            #pragma multi_compile_instancing

            Varyings vertDepth(Attributes v) { return vertAt(v, 1.0); }

            half4 fragDepth(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                clip(min(_OrbState.z - 0.6, 0.5 - _McxUiHide)); // its crystal only, while it is there
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "Orb"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            Varyings vert(Attributes v) { return vertAt(v, 0.0); }

            // the hue wheel 0..1: the prism's colours
            float3 Spectrum(float h)
            {
                return saturate(abs(frac(h + float3(0.0, 0.6666667, 0.3333333)) * 6.0 - 3.0) - 1.0);
            }

            // a ripple of age 0..1 on its way out (< 0: none), at `imp` radii from the centre
            float Ripple(float age, float imp)
            {
                float on = step(0.0, age) * step(age, 0.999);
                float rho = 0.55 + 1.05 * age;
                return exp(-abs(imp - rho) * 18.0) * (1.0 - age) * (1.0 - age) * 0.8 * on;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 cam = GetCameraPositionWS();
                float3 V = normalize(cam - i.positionWS);                       // towards this eye
                float3 toC = (_OrbCenter.xyz - cam) / max(_OrbCenter.w, 1e-5);  // the eye to the centre, radii
                float3 pc = toC - V * dot(toC, V);                              // the centre's offset from this ray
                float d2 = dot(pc, pc);                                         // the ray's squared distance from the centre
                float kind = i.info.x;
                float E = _OrbState.x, P = _OrbState.y;
                float3 tint = _OrbTint.rgb;
                const float3 hot = float3(1.0, 1.0, 1.0);
                float vis = McxVisibilityAt(i.occ) * saturate(_OrbState.z);
                // the light inside: a hot point and a soft bloom round it, breathing with the energy, swelling with the voice
                float inner = (0.010 + 0.028 * E + 0.030 * P) / (d2 + 0.010) + (0.05 + 0.10 * P) * exp(-d2 * 6.0);
                float3 light = 0;
                float alpha = 0.0, glass = 0.0;
                if (kind > 3.5)
                {
                    // ---- the tail: soft across, fading along, the colours running down it
                    float tt = saturate(i.bary.x), side = i.bary.y;
                    float a = (1.0 - side * side) * (1.0 - tt) * (1.0 - tt) * _OrbRing0U.w;
                    light = lerp(tint, Spectrum(_OrbPhaseA.x + 0.5 + tt * 0.35), 0.35 * saturate(_OrbPhaseB.w)) * a * 0.55;
                }
                else if (kind > 2.5)
                {
                    // ---- the halo (the square turned to the eye): a soft glow, the light inside, a faint star, ripples,
                    // the wake flare (a ring of light that runs out, a bright star)
                    float2 uv = i.bary.xy * _OrbShield.y;
                    float imp = length(uv);
                    float glow = 0.26 * exp(-max(imp - 0.30, 0.0) * 3.0) * (0.45 + 0.7 * E + 0.6 * P);
                    float f = 1.0 - saturate(_OrbPhaseA.y);                     // 1 as it flares, 0 when that is over
                    float f2 = f * f;
                    float star = (exp(-abs(uv.y) * 40.0) * exp(-abs(uv.x) * 1.7) + exp(-abs(uv.x) * 40.0) * exp(-abs(uv.y) * 1.7)) * (0.05 + 0.10 * P + 1.4 * f2);
                    float rip = 0.0;
                    if (max(max(_OrbRipples.x, _OrbRipples.y), max(_OrbRipples.z, _OrbRipples.w)) >= 0.0) // a uniform: the same for every pixel
                        rip = Ripple(_OrbRipples.x, imp) + Ripple(_OrbRipples.y, imp) + Ripple(_OrbRipples.z, imp) + Ripple(_OrbRipples.w, imp);
                    float ring = 0.0;
                    if (f > 0.001) // a uniform
                        ring = exp(-abs(imp - (1.55 - 1.20 * f)) * 14.0) * f2 * 1.3;
                    float edge = saturate((_OrbShield.y - imp) / (_OrbShield.y * 0.40));
                    edge = edge * edge * (3.0 - 2.0 * edge);
                    light = (tint * (glow + rip + ring) + lerp(tint, hot, 0.7) * (inner * 0.8 + star)) * edge;
                }
                else
                {
                    // ---- a facet (the crystal, the heart, a shard)
                    float3 N = normalize(i.normalWS);
                    float ndv = dot(N, V);
                    float front = ndv >= 0.0 ? 1.0 : 0.0;
                    float cv = abs(ndv);
                    float fres = (1.0 - cv) * (1.0 - cv);
                    // the prism: the hue walks with the angle it is seen at, each facet a little apart; a back facet (seen
                    // through the front ones) half a turn on
                    float hue = _OrbPhaseA.x + i.info.z * 0.30 + fres * 0.85 + (1.0 - front) * 0.45;
                    float3 prism = lerp(tint, Spectrum(hue), saturate(_OrbPhaseB.w) * (0.25 + 0.75 * fres));
                    float e = min(i.bary.x, min(i.bary.y, i.bary.z));
                    float rim = exp(-e * 14.0);                                     // its edges catch the light
                    float3 Nf = N * (front * 2.0 - 1.0);                            // the side that faces the eye
                    float3 L = normalize(float3(-0.35, 0.80, 0.0) + V * 0.6);       // a key light above, a little towards the eye
                    float glint = pow(saturate(dot(reflect(-V, Nf), L)), 20.0);
                    // a cast: a band of light runs along it from the side it turns to
                    float bw = (dot(normalize(i.positionWS - _OrbCenter.xyz + 1e-5), _OrbLook.xyz) - (1.0 - 2.0 * _OrbShield.x)) / 0.35;
                    float band = exp(-bw * bw) * _OrbLook.w;
                    if (kind < 0.5)
                    {
                        // the crystal: dark glass with a prismatic sheen towards its rims, lit edges, a glint, the light inside
                        float body = 0.35 + 0.65 * front;
                        light = prism * (0.10 + 0.70 * fres) * (0.55 + 0.6 * E) * body
                              + lerp(prism, hot, 0.45) * rim * (0.30 + 0.45 * E + 0.5 * P + band) * body
                              + hot * glint * 0.7 * front
                              + lerp(tint, hot, 0.5) * (inner * 0.55 + band * 0.4);
                        alpha = front * (0.22 + 0.18 * fres);
                        glass = front;
                    }
                    else if (kind < 1.5)
                    {
                        // the heart: light, not glass
                        light = lerp(tint, hot, 0.6) * (0.35 + 0.9 * E + 0.9 * P) * (0.55 + 0.45 * cv) + hot * rim * (0.2 + 0.6 * P);
                    }
                    else
                    {
                        // a shard: small, bright edges; gold while a message waits
                        float3 col = lerp(prism, _OrbGold.rgb * 1.3, i.info.y);
                        light = (col * (0.25 + 0.75 * fres) + lerp(col, hot, 0.5) * rim * 0.9 + hot * glint * 0.6) * i.info.w;
                        alpha = front * 0.18 * saturate(i.info.w);
                    }
                }
                // tone map; the crystal's glass is dark over what is behind it; the light carries a little alpha too
                float3 rgb = 1.0 - exp(-max(light, 0.0) * _OrbState.w);
                rgb += _OrbDeep.rgb * glass * 0.8;
                alpha = saturate(alpha + max(rgb.r, max(rgb.g, rgb.b)) * 0.45);
                return half4(saturate(rgb) * vis, alpha * vis);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
