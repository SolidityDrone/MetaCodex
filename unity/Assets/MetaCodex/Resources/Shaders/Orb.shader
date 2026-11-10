// MetaCodex, the orb (UiOrb): a sphere of energy that floats by the user, casts the panels and speaks. One quad that
// faces each eye; every pixel follows its own ray through a unit sphere (orb units = radii), so the orb has true depth
// per eye: a hot core, two shells of plasma turning against each other, a glass membrane with a bright limb and the
// plates of a shield that light up in a wave, three thin rings of light round it (the nearest point between the ray
// and each circle, so a ring is a tube from every side and passes behind the sphere), motes, ripples that leave it
// while it speaks, a halo. An original design.
// Written as straight-line code (0.7.10, after the headset showed flat cream blocks and stripes over the orb in most
// frames, never in the Editor): no loops, no uniform arrays, no early return before a derivative, every input an
// ordinary material property in UnityPerMaterial. It is also about a third cheaper than the first version: the far side
// of the outer plasma shell and the motes' tails are gone, and the quad is smaller (2.3 radii instead of 2.7).
// URP, single-pass instanced stereo, premultiplied: its light is added over the passthrough and the body darkens what
// is behind it (alpha), so it reads in a bright room. A first pass writes the depth of its disc: a hand behind it does
// not clear it (FocusMesh draws the hands last, depth-tested), a fingertip in front of it still shows.
Shader "MetaCodex/Orb"
{
    Properties
    {
        _OrbCenter ("Centre (xyz), radius (w)", Vector) = (0, 0, 0, 0.032)
        _OrbState ("Energy, pulse, appear, exposure", Vector) = (0.35, 0, 0, 1.5)
        _OrbTint ("Tint (linear)", Vector) = (0.036, 0.63, 1, 1)
        _OrbDeep ("Body glass (linear)", Vector) = (0.01, 0.03, 0.075, 1)
        _OrbGold ("Gold (linear)", Vector) = (1, 0.6, 0.21, 1)
        _OrbPhaseA ("Inner plasma: phases xyz, twist w", Vector) = (0, 0, 0, 1.4)
        _OrbPhaseB ("Outer plasma: phases xyz, twist w", Vector) = (0, 0, 0, -0.9)
        _OrbRotA0 ("Inner shell rotation, row 0", Vector) = (1, 0, 0, 0)
        _OrbRotA1 ("Inner shell rotation, row 1", Vector) = (0, 1, 0, 0)
        _OrbRotA2 ("Inner shell rotation, row 2", Vector) = (0, 0, 1, 0)
        _OrbRotB0 ("Outer shell rotation, row 0", Vector) = (1, 0, 0, 0)
        _OrbRotB1 ("Outer shell rotation, row 1", Vector) = (0, 1, 0, 0)
        _OrbRotB2 ("Outer shell rotation, row 2", Vector) = (0, 0, 1, 0)
        _OrbLook ("Where it attends (xyz), shield strength (w)", Vector) = (0, 0, -1, 0)
        _OrbShield ("Shield wave (x), quad half size in radii (y)", Vector) = (1, 2.3, 0, 0)
        _OrbRipples ("Ripple ages (< 0: none)", Vector) = (-1, -1, -1, -1)
        _OrbRing0N ("Ring 0 normal", Vector) = (0, 1, 0, 0)
        _OrbRing0U ("Ring 0 in-plane axis", Vector) = (1, 0, 0, 0)
        _OrbRing0P ("Ring 0 radius, width, phase, brightness", Vector) = (1.3, 0.02, 0, 0)
        _OrbRing0Q ("Ring 0 dashes, duty, gold", Vector) = (0, 1, 1, 0)
        _OrbRing1N ("Ring 1 normal", Vector) = (0, 1, 0, 0)
        _OrbRing1U ("Ring 1 in-plane axis", Vector) = (1, 0, 0, 0)
        _OrbRing1P ("Ring 1 radius, width, phase, brightness", Vector) = (1.46, 0.012, 0, 0)
        _OrbRing1Q ("Ring 1 dashes, duty, gold", Vector) = (0, 1, 0, 0)
        _OrbRing2N ("Ring 2 normal", Vector) = (0, 1, 0, 0)
        _OrbRing2U ("Ring 2 in-plane axis", Vector) = (1, 0, 0, 0)
        _OrbRing2P ("Ring 2 radius, width, phase, brightness", Vector) = (1.64, 0.009, 0, 0)
        _OrbRing2Q ("Ring 2 dashes, duty, gold", Vector) = (0, 1, 1, 0)
        _OrbMote0 ("Mote 0 place (radii), size squared", Vector) = (0, 0, 0, 0)
        _OrbMote1 ("Mote 1 place (radii), size squared", Vector) = (0, 0, 0, 0)
        _OrbMote2 ("Mote 2 place (radii), size squared", Vector) = (0, 0, 0, 0)
        _OrbMote3 ("Mote 3 place (radii), size squared", Vector) = (0, 0, 0, 0)
        _OrbMoteW ("Motes: how bright", Vector) = (0, 0, 0, 0)
        _OrbMoteGold ("Motes: a message not read yet (gold)", Vector) = (0, 0, 0, 0)
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

        struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
        struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

        // the quad (-0.5..0.5) turned to this eye, `back` radii further from it
        Varyings vertAt(Attributes v, float back)
        {
            Varyings o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            float3 c = _OrbCenter.xyz;
            float3 toCam = GetCameraPositionWS() - c;
            float dist = max(length(toCam), 1e-4);
            toCam /= dist;
            float3 right = cross(float3(0.0, 1.0, 0.0), toCam);
            right = dot(right, right) < 1e-6 ? float3(1.0, 0.0, 0.0) : normalize(right);
            float3 up = cross(toCam, right);
            float size = _OrbCenter.w * _OrbShield.y * 2.0;
            o.positionWS = c + (right * v.positionOS.x + up * v.positionOS.y) * size - toCam * (back * _OrbCenter.w);
            o.positionCS = TransformWorldToHClip(o.positionWS);
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

            Varyings vertDepth(Attributes v) { return vertAt(v, 0.3); }

            half4 fragDepth(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 cam = GetCameraPositionWS();
                float3 ro = (cam - _OrbCenter.xyz) / _OrbCenter.w;
                float3 rd = normalize(i.positionWS - cam);
                float3 pc = ro - rd * dot(ro, rd);
                clip(min(0.80 - dot(pc, pc), min(_OrbState.z - 0.6, 0.5 - _McxUiHide))); // its body only, while it is there
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

            #define TURN(r0, r1, r2, p) float3(dot(r0.xyz, p), dot(r1.xyz, p), dot(r2.xyz, p))

            // a vortex of filaments on a shell: the pattern is sheared round the y axis by latitude, then warped twice
            void Plasma(float3 n, float3 ph, float k, float twist, out float fil, out float cloud)
            {
                float a = n.y * twist;
                float ca = cos(a), sa = sin(a);
                n = float3(n.x * ca - n.z * sa, n.y, n.x * sa + n.z * ca);
                float3 q = n * k;
                q += 0.55 * sin(q.yzx * 1.9 + ph.x);
                q += 0.30 * sin(q.zxy * 3.1 - ph.y);
                float v = sin(q.x * 2.0) * sin(q.y * 2.0) + 0.6 * sin(q.z * 2.0 + ph.z);
                float f = saturate(1.0 - abs(v) * 1.7);
                f = f * f * f;
                fil = f * f;
                cloud = saturate(0.5 + 0.5 * sin(q.x * 0.9 + q.y * 0.6 + q.z * 1.1));
            }

            // the plates of its shield: a cube's six faces, each a hexagon lattice; 1 on the seams
            float HexPlates(float3 n, float scale)
            {
                float3 a = abs(n);
                float2 uv = a.x >= a.y && a.x >= a.z ? n.yz : (a.y >= a.z ? n.xz : n.xy);
                float m = max(a.x, max(a.y, a.z));
                uv = uv / max(m, 1e-4) * scale;
                const float2 s = float2(1.0, 1.7320508);
                float2 pa = (frac(uv / s) - 0.5) * s, pb = (frac((uv - s * 0.5) / s) - 0.5) * s;
                float2 g = abs(dot(pa, pa) < dot(pb, pb) ? pa : pb);
                return smoothstep(0.40, 0.49, max(g.x * 0.5 + g.y * 0.8660254, g.x));
            }

            // a ring seen along one start of the ray (the near or the far arc): two steps of "nearest point of the
            // circle, nearest point of the ray"; returns how much of its light this pixel gets
            float RingFrom(float3 ro, float3 rd, float tt, float t0, float hit, float px, float4 N, float4 U, float4 P, float4 Q)
            {
                float rho = P.x, w = max(P.y, 1e-4);
                float3 p = ro + rd * tt;
                float3 q = p - N.xyz * dot(p, N.xyz);
                q *= rho / max(length(q), 1e-5);
                tt = dot(q - ro, rd);
                p = ro + rd * tt;
                q = p - N.xyz * dot(p, N.xyz);
                q *= rho / max(length(q), 1e-5);
                tt = dot(q - ro, rd);
                float dist = length(ro + rd * tt - q);
                float glow = exp(-dist / (w * 2.2));
                float line0 = saturate((w * 0.5 - dist) / px + 0.5);
                float dash = 1.0;
                if (Q.x > 0.5) // a uniform: the same for every pixel
                {
                    float3 V = cross(N.xyz, U.xyz);
                    float f = frac((atan2(dot(q, V), dot(q, U.xyz) + 1e-6) / 6.2831853 + P.z) * Q.x);
                    dash = smoothstep(0.0, 0.015, f) * smoothstep(Q.y, Q.y - 0.015, f);
                }
                float occ = (hit > 0.5 && tt > t0) ? 0.22 : 1.0; // behind the near surface: seen through the body
                return (line0 * dash + glow * 0.40 * lerp(1.0, dash, 0.55)) * occ;
            }

            float Ring(float3 ro, float3 rd, float tca, float d2, float t0, float hit, float px, float4 N, float4 U, float4 P, float4 Q)
            {
                float th = sqrt(max(P.x * P.x - d2, 0.0));
                return max(RingFrom(ro, rd, tca - th, t0, hit, px, N, U, P, Q), RingFrom(ro, rd, tca + th, t0, hit, px, N, U, P, Q));
            }

            float3 Mote(float3 ro, float3 rd, float t0, float hit, float4 m, float weight, float unread, float3 tint, float3 gold)
            {
                float3 dv = m.xyz - ro;
                float tm = dot(rd, dv);
                float dm2 = max(dot(dv, dv) - tm * tm, 0.0);
                float g = m.w / (dm2 + m.w + 1e-7);
                float occ = (hit > 0.5 && tm > t0) ? 0.15 : 1.0;
                return lerp((tint * 0.5 + 0.5) * 1.2, gold * 2.2, unread) * g * g * occ * weight;
            }

            float Ripple(float age, float imp, float px)
            {
                float on = step(0.0, age) * step(age, 0.999);
                float rho = 1.06 + 1.10 * age;
                float dr = abs(imp - rho);
                float rip = saturate(((0.016 + 0.03 * age) * 0.5 - dr) / px + 0.5) + exp(-dr / 0.06) * 0.25;
                return rip * (1.0 - age) * (1.0 - age) * 0.9 * on;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float3 cam = GetCameraPositionWS();
                float3 ro = (cam - _OrbCenter.xyz) / _OrbCenter.w;
                float3 rd = normalize(i.positionWS - cam);
                float tca = -dot(ro, rd);
                float3 pc = ro + rd * tca;                          // the ray's closest point to the centre
                float d2 = dot(pc, pc);
                float imp = sqrt(d2);
                float px = max(fwidth(imp), 1e-4);                  // about one pixel, in radii (first: no branch before it)
                float vis = McxVisibility(i.positionWS) * saturate(_OrbState.z);
                float E = _OrbState.x, P = _OrbState.y;
                float3 tint = _OrbTint.rgb, deep = _OrbDeep.rgb, gold = _OrbGold.rgb;
                const float3 hot = float3(1.0, 1.0, 1.0);
                float hit = d2 < 1.0 ? 1.0 : 0.0;
                float nz = sqrt(max(1.0 - d2, 0.0));                // cos of the angle at the near surface
                float t0 = tca - nz;
                float3 light = 0;

                // ---- the halo (outside), a faint fill towards the limb inside
                float outside = max(imp - 1.0, 0.0);
                float halo = (0.30 * exp(-outside * 4.6) + 0.085 * exp(-outside * 1.5)) * (0.50 + 0.8 * E + 0.7 * P);
                halo *= lerp(1.0, 0.25 + 0.75 * d2 * d2, hit);
                light += tint * halo;

                // ---- the core: a hot point with a soft bloom, two faint streaks through it (level with the world)
                float core = (0.016 + 0.040 * P + 0.008 * E) / (d2 + 0.0055) + (0.05 + 0.10 * P) * exp(-d2 * 9.0);
                light += (tint * 0.30 + hot * 0.70) * core;
                float3 side = cross(float3(0.0, 1.0, 0.0), rd);
                side = dot(side, side) < 1e-6 ? float3(1.0, 0.0, 0.0) : normalize(side);
                float3 upv = cross(rd, side);
                float su = dot(pc, side), sv = dot(pc, upv);
                float streak = exp(-abs(sv) * 30.0) * exp(-abs(su) * 1.1) * (0.07 + 0.9 * P) + exp(-abs(su) * 34.0) * exp(-abs(sv) * 2.4) * (0.03 + 0.3 * P);
                light += (tint * 0.6 + hot * 0.4) * streak * 0.5;

                // ---- plasma: the inner shell seen on its near and its far side, the outer shell on its near side
                const float3 blue = float3(0.0513, 0.2140, 1.0);
                float fil, cloud;
                {
                    const float r = 0.56;
                    float th = sqrt(max(r * r - d2, 0.0));
                    float inside = d2 < r * r ? 1.0 : 0.0;
                    float limb = (0.55 + 0.45 * (1.0 - th / r) * (1.0 - th / r)) * smoothstep(0.0, 0.30, th / r) * inside;
                    float3 c0 = lerp(tint, blue, 0.15), c1 = lerp(tint, hot, 0.75);
                    float3 pn = (ro + rd * (tca - th)) / r;
                    Plasma(TURN(_OrbRotA0, _OrbRotA1, _OrbRotA2, pn), _OrbPhaseA.xyz, 2.2, _OrbPhaseA.w, fil, cloud);
                    light += lerp(c0, c1, fil) * fil * (0.30 + 0.70 * cloud) * (0.60 + 1.9 * E + 0.9 * P) * limb + c0 * cloud * cloud * (0.05 + 0.10 * E) * inside;
                    float3 pf = (ro + rd * (tca + th)) / r;
                    Plasma(TURN(_OrbRotA0, _OrbRotA1, _OrbRotA2, pf), _OrbPhaseA.xyz, 2.2, _OrbPhaseA.w, fil, cloud);
                    light += (lerp(c0, c1, fil) * fil * (0.30 + 0.70 * cloud) * (0.60 + 1.9 * E + 0.9 * P) * limb + c0 * cloud * cloud * (0.05 + 0.10 * E) * inside) * 0.40;
                }
                {
                    const float r = 0.88;
                    float th = sqrt(max(r * r - d2, 0.0));
                    float inside = d2 < r * r ? 1.0 : 0.0;
                    float limb = (0.55 + 0.45 * (1.0 - th / r) * (1.0 - th / r)) * smoothstep(0.0, 0.30, th / r) * inside;
                    float3 c0 = lerp(tint, blue, 0.65), c1 = lerp(tint, hot, 0.35);
                    float3 pn = (ro + rd * (tca - th)) / r;
                    Plasma(TURN(_OrbRotB0, _OrbRotB1, _OrbRotB2, pn), _OrbPhaseB.xyz, 3.3, _OrbPhaseB.w, fil, cloud);
                    light += (lerp(c0, c1, fil) * fil * (0.30 + 0.70 * cloud) * (0.60 + 1.9 * E + 0.9 * P) * limb + c0 * cloud * cloud * (0.05 + 0.10 * E) * inside) * 0.52;
                }

                // ---- the membrane: a fresnel rim, a thin bright limb, a highlight, the shield's plates in a wave
                float fres = pow(1.0 - nz, 2.4);
                float rim = smoothstep(0.92, 0.997, imp) * hit;
                light += tint * (fres * 0.36 + rim * 0.60) * hit * (0.55 + 0.6 * E);
                float3 n0 = ro + rd * t0;
                float hx = dot(n0, side) + 0.40, hy = dot(n0, upv) - 0.52;
                light += hot * exp(-(hx * hx + hy * hy) * 10.0) * hit * 0.13;
                if (_OrbLook.w > 0.002) // a uniform
                {
                    float bw = (dot(n0, _OrbLook.xyz) - (1.0 - 2.0 * _OrbShield.x)) / 0.33;
                    float band = exp(-bw * bw) * hit;
                    float plates = HexPlates(TURN(_OrbRotB0, _OrbRotB1, _OrbRotB2, n0), 3.2);
                    light += (lerp(tint, hot, 0.35) * plates * (0.35 + 0.65 * fres) * 1.6 + tint * 0.10) * band * _OrbLook.w;
                }

                // ---- three rings (each a thin tube of light), motes, ripples
                light += lerp(tint, gold, _OrbRing0Q.z) * Ring(ro, rd, tca, d2, t0, hit, px, _OrbRing0N, _OrbRing0U, _OrbRing0P, _OrbRing0Q) * _OrbRing0P.w;
                light += lerp(tint, gold, _OrbRing1Q.z) * Ring(ro, rd, tca, d2, t0, hit, px, _OrbRing1N, _OrbRing1U, _OrbRing1P, _OrbRing1Q) * _OrbRing1P.w;
                light += lerp(tint, gold, _OrbRing2Q.z) * Ring(ro, rd, tca, d2, t0, hit, px, _OrbRing2N, _OrbRing2U, _OrbRing2P, _OrbRing2Q) * _OrbRing2P.w;
                light += Mote(ro, rd, t0, hit, _OrbMote0, _OrbMoteW.x, _OrbMoteGold.x, tint, gold);
                light += Mote(ro, rd, t0, hit, _OrbMote1, _OrbMoteW.y, _OrbMoteGold.y, tint, gold);
                light += Mote(ro, rd, t0, hit, _OrbMote2, _OrbMoteW.z, _OrbMoteGold.z, tint, gold);
                light += Mote(ro, rd, t0, hit, _OrbMote3, _OrbMoteW.w, _OrbMoteGold.w, tint, gold);
                light += tint * (Ripple(_OrbRipples.x, imp, px) + Ripple(_OrbRipples.y, imp, px) + Ripple(_OrbRipples.z, imp, px) + Ripple(_OrbRipples.w, imp, px));

                // ---- nothing reaches the edge of the quad (a tail cut off by it shows as a faint square of light)
                float edge = saturate((_OrbShield.y - imp) / max(_OrbShield.y * 0.36, 1e-3));
                light *= edge * edge * (3.0 - 2.0 * edge);

                // ---- tone map; the body is dark glass over what is behind it, the light carries some alpha too
                float3 rgb = 1.0 - exp(-max(light, 0.0) * _OrbState.w);
                float body = 0.80 * smoothstep(1.02, 0.86, imp);
                rgb += deep * body * (0.35 + 0.65 * nz) * 0.9;
                float alpha = saturate(body + max(rgb.r, max(rgb.g, rgb.b)) * 0.55);
                return half4(saturate(rgb) * vis, alpha * vis);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
