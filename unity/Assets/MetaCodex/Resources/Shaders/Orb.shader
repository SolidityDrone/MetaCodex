// MetaCodex, the orb (UiOrb): a sphere of energy that floats by the user, casts the panels and speaks. One quad that
// faces each eye; every pixel follows its own ray through a unit sphere (orb units = radii), so the orb has true depth
// per eye: a hot core, two shells of plasma turning against each other (seen on their near and far side), a glass
// membrane with a bright limb and the plates of a shield that light up in a wave, three thin rings of light round it
// (the nearest point between the ray and each circle, so a ring is a tube from every side and passes behind the
// sphere), motes with tails, ripples that leave it while it speaks, a halo. An original design.
// URP, single-pass instanced stereo, premultiplied: its light is added over the passthrough and the body darkens what
// is behind it (alpha), so it reads in a bright room. A first pass writes the depth of its disc: a hand behind it does
// not clear it (FocusMesh draws the hands last, depth-tested), a fingertip in front of it still shows.
// All state comes from UiOrb each frame (plain uniforms, one orb): the centre and radius, energy, the pulse of the
// voice, colours, the phases of the plasma, the rings' frames, the look direction, ripples, motes.
Shader "MetaCodex/Orb"
{
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+15" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Assets/MetaCodex/Shaders/McxOcclusion.hlsl"

        float4 _OrbCenter;      // xyz world, w = radius (metres)
        float4 _OrbState;       // x energy 0..1.5, y pulse 0..1, z appear 0..1, w exposure
        float4 _OrbTint;        // rgb linear: the colour of its state
        float4 _OrbDeep;        // rgb: the dark glass of its body
        float4 _OrbGold;        // rgb: its warm rings
        float4 _OrbPhaseA;      // xyz the inner plasma's three phases (radians, wrapped), w its twist
        float4 _OrbPhaseB;      // the outer plasma's
        float4 _OrbRotA[3];     // rows of the inner shell's rotation
        float4 _OrbRotB[3];     // the outer shell's (the shield's plates turn with it)
        float4 _OrbLook;        // xyz where it attends (world, unit), w = shield strength 0..1
        float4 _OrbShield;      // x = where the shield's wave is 0..1 (from the look direction round to the back), y = the quad's half size in radii
        float4 _OrbRipples;     // four ages 0..1 (< 0: none)
        float4 _OrbRingN[3];    // each ring's normal (world, unit)
        float4 _OrbRingU[3];    // a unit vector in its plane (dashes are counted from it)
        float4 _OrbRingP[3];    // radius (radii), tube width, phase (turns), brightness
        float4 _OrbRingQ[3];    // dashes round it (0: whole), duty 0..1, gold 0..1, unused
        float4 _OrbMotes[12];   // xyz place (radii, world axes), w = size * size * weight ... (size in radii)
        float4 _OrbMoteW[12];   // x = weight 0..1, y = gold 0..1 (a message not read yet rides round the orb as a gold spark)

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
                if (dot(pc, pc) > 0.80 || _OrbState.z < 0.6 || _McxUiHide > 0.5) discard; // its body only, while it is there
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
                float2 uv;
                float m;
                if (a.x >= a.y && a.x >= a.z) { uv = n.yz; m = a.x; }
                else if (a.y >= a.z) { uv = n.xz; m = a.y; }
                else { uv = n.xy; m = a.z; }
                uv = uv / max(m, 1e-4) * scale;
                const float2 s = float2(1.0, 1.7320508);
                float2 pa = (frac(uv / s) - 0.5) * s, pb = (frac((uv - s * 0.5) / s) - 0.5) * s;
                float2 g = abs(dot(pa, pa) < dot(pb, pb) ? pa : pb);
                return smoothstep(0.40, 0.49, max(g.x * 0.5 + g.y * 0.8660254, g.x));
            }

            #define TURN(rows, p) float3(dot(rows[0].xyz, p), dot(rows[1].xyz, p), dot(rows[2].xyz, p))

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float appear = saturate(_OrbState.z);
                float vis = McxVisibility(i.positionWS) * appear;
                if (vis < 0.003) return half4(0, 0, 0, 0);
                float E = _OrbState.x, P = _OrbState.y;
                float3 tint = _OrbTint.rgb, deep = _OrbDeep.rgb, gold = _OrbGold.rgb;
                const float3 hot = float3(1.0, 1.0, 1.0);
                float3 cam = GetCameraPositionWS();
                float3 ro = (cam - _OrbCenter.xyz) / _OrbCenter.w;
                float3 rd = normalize(i.positionWS - cam);
                float tca = -dot(ro, rd);
                float3 pc = ro + rd * tca;                          // the ray's closest point to the centre
                float d2 = dot(pc, pc);
                float imp = sqrt(d2);
                float px = max(fwidth(imp), 1e-4);                  // about one pixel, in radii
                float hit = d2 < 1.0 ? 1.0 : 0.0;
                float nz = sqrt(max(1.0 - d2, 0.0));                // cos of the angle at the near surface
                float t0 = tca - nz;
                float3 light = 0;

                // ---- the halo (outside), a faint fill towards the limb inside
                float outside = max(imp - 1.0, 0.0);
                float halo = (0.30 * exp(-outside * 4.6) + 0.085 * exp(-outside * 1.5)) * (0.50 + 0.8 * E + 0.7 * P);
                float imp4 = d2 * d2;
                halo *= lerp(1.0, 0.25 + 0.75 * imp4, hit);
                light += tint * halo;

                // ---- the core: a hot point with a soft bloom, two faint streaks through it (level with the world)
                float core = (0.016 + 0.040 * P + 0.008 * E) / (d2 + 0.0055) + (0.05 + 0.10 * P) * exp(-d2 * 9.0);
                light += (tint * 0.30 + hot * 0.70) * core;
                float3 side = cross(float3(0.0, 1.0, 0.0), rd);
                side = dot(side, side) < 1e-6 ? float3(1.0, 0.0, 0.0) : normalize(side);
                float su = dot(pc, side), sv = dot(pc, cross(rd, side));
                float streak = exp(-abs(sv) * 30.0) * exp(-abs(su) * 1.1) * (0.07 + 0.9 * P) + exp(-abs(su) * 34.0) * exp(-abs(sv) * 2.4) * (0.03 + 0.3 * P);
                light += (tint * 0.6 + hot * 0.4) * streak * 0.5;

                // ---- two shells of plasma, each seen on its near and its far side
                const float3 blue = float3(0.0513, 0.2140, 1.0);
                if (d2 < 0.7744)
                {
                    for (int s = 0; s < 2; s++)
                    {
                        float r = s == 0 ? 0.56 : 0.88;
                        if (d2 < r * r)
                        {
                            float th = sqrt(r * r - d2);
                            float k = s == 0 ? 2.2 : 3.3, gain = s == 0 ? 1.0 : 0.42;
                            float4 ph = s == 0 ? _OrbPhaseA : _OrbPhaseB;
                            float3 c0 = lerp(tint, blue, s == 0 ? 0.15 : 0.65), c1 = lerp(tint, hot, s == 0 ? 0.75 : 0.35);
                            float limb = (0.55 + 0.45 * (1.0 - th / r) * (1.0 - th / r)) * smoothstep(0.0, 0.30, th / r); // no hard circle at its silhouette
                            for (int f = 0; f < 2; f++)
                            {
                                float3 p = (ro + rd * (tca + (f == 0 ? -th : th))) / r;
                                float3 n = s == 0 ? TURN(_OrbRotA, p) : TURN(_OrbRotB, p);
                                float fil, cloud;
                                Plasma(n, ph.xyz, k, ph.w, fil, cloud);
                                float w = (f == 0 ? 1.0 : 0.40) * gain;
                                float valF = fil * (0.30 + 0.70 * cloud) * (0.60 + 1.9 * E + 0.9 * P) * limb * w;
                                float valC = cloud * cloud * (0.05 + 0.10 * E) * w;
                                light += lerp(c0, c1, saturate(fil)) * valF + c0 * valC;
                            }
                        }
                    }
                }

                // ---- the membrane: a fresnel rim, a thin bright limb, a highlight, the shield's plates in a wave
                float fres = pow(1.0 - nz, 2.4);
                float rim = smoothstep(0.92, 0.997, imp) * hit;
                light += tint * (fres * 0.36 + rim * 0.60) * hit * (0.55 + 0.6 * E);
                float3 n0 = ro + rd * t0;
                float3 hlDir = float3(dot(n0, side), dot(n0, cross(rd, side)), 0.0);
                float hl = exp(-((hlDir.x + 0.40) * (hlDir.x + 0.40) + (hlDir.y - 0.52) * (hlDir.y - 0.52)) * 10.0) * hit;
                light += hot * hl * 0.13;
                if (_OrbLook.w > 0.002 && hit > 0.5)
                {
                    float front = 1.0 - 2.0 * _OrbShield.x;
                    float bw = (dot(n0, _OrbLook.xyz) - front) / 0.33;
                    float band = exp(-bw * bw);
                    float plates = HexPlates(TURN(_OrbRotB, n0), 3.2);
                    light += lerp(tint, hot, 0.35) * plates * band * _OrbLook.w * (0.35 + 0.65 * fres) * 1.6 + tint * band * _OrbLook.w * 0.10;
                }

                // ---- three rings: thin tubes of light. The distance between the ray and the circle, from two starts
                // (the near and the far arc), two steps each of "nearest point of the circle, nearest point of the ray".
                // Only where a ring can be: a ray that passes further from the centre than the ring's radius (and its
                // glow) cannot meet it; the dashes (an atan2) only where its light is.
                if (imp < 2.1)
                {
                    for (int rg = 0; rg < 3; rg++)
                    {
                        float4 rp = _OrbRingP[rg], rq = _OrbRingQ[rg];
                        if (rp.w > 0.002 && imp < rp.x + 0.22)
                        {
                            float3 N = _OrbRingN[rg].xyz, U = _OrbRingU[rg].xyz;
                            float rho = rp.x, w = rp.y;
                            float th = sqrt(max(rho * rho - d2, 0.0));
                            float best = 0.0;
                            for (int a2 = 0; a2 < 2; a2++)
                            {
                                float tt = tca + (a2 == 0 ? -th : th);
                                float3 q = 0;
                                for (int it = 0; it < 2; it++)
                                {
                                    float3 p = ro + rd * tt;
                                    q = p - N * dot(p, N);
                                    q *= rho / max(length(q), 1e-5);
                                    tt = dot(q - ro, rd);
                                }
                                float dist = length(ro + rd * tt - q);
                                float glow = exp(-dist / (w * 2.2));
                                if (glow > 0.004)
                                {
                                    float line0 = saturate((w * 0.5 - dist) / px + 0.5);
                                    float dash = 1.0;
                                    if (rq.x > 0.5)
                                    {
                                        float3 V = cross(N, U);
                                        float f = frac((atan2(dot(q, V), dot(q, U)) / 6.2831853 + rp.z) * rq.x);
                                        dash = smoothstep(0.0, 0.015, f) * smoothstep(rq.y, rq.y - 0.015, f);
                                    }
                                    float occ = (hit > 0.5 && tt > t0) ? 0.22 : 1.0; // behind the near surface: seen through the body
                                    best = max(best, (line0 * dash + glow * 0.40 * lerp(1.0, dash, 0.55)) * occ);
                                }
                            }
                            light += lerp(tint, gold, rq.z) * best * rp.w;
                        }
                    }
                }

                // ---- motes with short tails (UiOrb gives each place); none further out than 1.8 radii
                if (imp < 1.8)
                {
                    for (int m = 0; m < 12; m++)
                    {
                        float4 mt = _OrbMotes[m];
                        if (mt.w > 1e-7)
                        {
                            float3 dv = mt.xyz - ro;
                            float tm = dot(rd, dv);
                            float dm2 = max(dot(dv, dv) - tm * tm, 0.0);
                            float g = mt.w / (dm2 + mt.w);
                            float occ = (hit > 0.5 && tm > t0) ? 0.15 : 1.0;
                            light += lerp((tint * 0.5 + 0.5) * 1.2, gold * 2.2, _OrbMoteW[m].y) * g * g * occ * _OrbMoteW[m].x;
                        }
                    }
                }

                // ---- ripples that leave it while it speaks (spheres seen as circles)
                for (int rr = 0; rr < 4; rr++)
                {
                    float age = _OrbRipples[rr];
                    if (age >= 0.0 && age < 1.0)
                    {
                        float rho = 1.06 + 1.55 * age;
                        float dr = abs(imp - rho);
                        float rip = saturate(((0.016 + 0.03 * age) * 0.5 - dr) / px + 0.5) + exp(-dr / 0.06) * 0.25;
                        light += tint * rip * (1.0 - age) * (1.0 - age) * 0.9;
                    }
                }

                // ---- nothing reaches the edge of the quad: the halo's long tail and the core's fade to zero inside it
                // (a tail cut off by the quad showed as a faint square of light round the orb)
                float edge = saturate((_OrbShield.y - imp) / (_OrbShield.y * 0.36));
                light *= edge * edge * (3.0 - 2.0 * edge);

                // ---- tone map; the body is dark glass over what is behind it, the light carries some alpha too
                float3 rgb = 1.0 - exp(-light * _OrbState.w);
                float body = 0.80 * smoothstep(1.02, 0.86, imp);
                rgb += deep * body * (0.35 + 0.65 * nz) * 0.9;
                float alpha = saturate(body + max(rgb.r, max(rgb.g, rgb.b)) * 0.55);
                return half4(rgb * vis, alpha * vis);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
