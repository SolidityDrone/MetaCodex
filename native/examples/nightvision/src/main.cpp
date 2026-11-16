// Night Vision: a mixed-reality Quest app built in MetaCodex with the native XR layer (mcx_xr.h).
//   * environment depth (XR_META_environment_depth) shaded as a night-vision image: green phosphor, brightness from
//     distance, edges from depth discontinuities, grain and scanlines
//   * passthrough (XR_FB_passthrough) underneath, turned green by a mono -> RGBA colour map; toggle it
//   * hands (XR_EXT_hand_tracking + XR_FB_hand_tracking_aim) switch the modes; controllers (Touch / Touch Plus) add
//     the trigger, the stick, A and the grip: every action works with the hands
//   * the room (XR_FB_scene) is queried and counted; an anchor (XR_FB_spatial_entity + storage) marks a spot
//   * a HUD panel with the state, drawn with mcx::Text, following the head lazily
// Controls: a pinch with either hand (or the right trigger): next mode | both hands pinch together (or A): passthrough
//           on/off | thumbstick up/down: gain | left grip (squeeze): drop an anchor at the left controller
// The app menu (a hand's menu gesture, or the left controller's button) is MetaCodex's: this app does not use it.
// Needs the spatial data permission (asked at the first start; or Settings > Apps > Night Vision > Permissions).
#include <mcx_xr.h>
#include <cmath>
#include <cstdio>
#include <cstring>

namespace {

const char* kVs = R"(#version 300 es
out vec2 ndc;
void main() {
    vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2)) * 2.0 - 1.0; // a full-screen triangle
    ndc = p;
    gl_Position = vec4(p, 0.0, 1.0);
}
)";

// per eye: the fragment's ray (eye pose + fov) -> a point 3 m out -> the depth camera's clip space -> depth
const char* kFs = R"(#version 300 es
precision highp float;
precision highp sampler2DArray;
in vec2 ndc;
uniform sampler2DArray depthTex;
uniform mat4 depthViewProj;
uniform mat3 eyeRot;
uniform vec3 eyePos;
uniform vec4 tans;          // left, right, down, up
uniform float layer, nearZ, infiniteFar, farZ, time, gain;
uniform int mode;           // 0 phosphor, 1 edges, 2 range
uniform float passthrough;  // 1: the camera shows underneath
uniform vec2 texel;
out vec4 o;

float linearDepth(float d) {
    float n = d * 2.0 - 1.0;
    if (infiniteFar > 0.5) return 2.0 * nearZ / max(1.0 - n, 1e-4);
    return 2.0 * farZ * nearZ / (farZ + nearZ - n * (farZ - nearZ));
}
float sampleZ(vec2 uv) { return linearDepth(texture(depthTex, vec3(uv, layer)).r); }
float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

void main() {
    vec2 t = ndc * 0.5 + 0.5;
    vec3 dirView = vec3(mix(tans.x, tans.y, t.x), mix(tans.z, tans.w, t.y), -1.0);
    vec3 p = eyePos + eyeRot * normalize(dirView) * 3.0;
    vec4 c = depthViewProj * vec4(p, 1.0);
    vec2 uv = c.xy / c.w * 0.5 + 0.5;
    if (c.w <= 0.0 || any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) { o = vec4(0.0, 0.0, 0.0, passthrough > 0.5 ? 0.0 : 1.0); return; }
    float z = sampleZ(uv);
    float zx = sampleZ(uv + vec2(texel.x, 0.0)), zx2 = sampleZ(uv - vec2(texel.x, 0.0));
    float zy = sampleZ(uv + vec2(0.0, texel.y)), zy2 = sampleZ(uv - vec2(0.0, texel.y));
    float edge = clamp((abs(zx + zx2 - 2.0 * z) + abs(zy + zy2 - 2.0 * z)) / max(z, 0.2) * 6.0, 0.0, 1.0);
    float bright = clamp(1.7 - z * 0.38, 0.06, 1.0);
    float v;
    vec3 col;
    if (mode == 0) { v = bright * 0.85 + edge * 0.6; col = vec3(0.25, 1.0, 0.35); }
    else if (mode == 1) { v = edge * 1.2 + bright * 0.08; col = vec3(0.35, 1.0, 0.45); }
    else { float band = fract(z * 2.0); v = 0.35 + 0.65 * smoothstep(0.0, 0.08, band) * (1.0 - smoothstep(0.92, 1.0, band)) * bright + edge * 0.5;
           col = mix(vec3(0.1, 1.0, 0.3), vec3(1.0, 0.9, 0.2), clamp(z / 5.0, 0.0, 1.0)); }
    float grain = (hash(gl_FragCoord.xy * 0.5 + fract(time * 37.0) * 91.0) - 0.5) * 0.22;
    float scan = 0.84 + 0.16 * sin(gl_FragCoord.y * 1.6);
    v = max(0.0, (v + grain) * scan * gain);
    float a = passthrough > 0.5 ? clamp(mode == 1 ? edge * 1.5 : 0.25 + edge * 0.6 + bright * 0.25, 0.0, 0.9) : 1.0;
    o = vec4(col * v * a, a); // premultiplied: the compositor blends it over the passthrough layer
}
)";

GLuint Compile(GLenum type, const char* src)
{
    GLuint s = glCreateShader(type);
    glShaderSource(s, 1, &src, nullptr);
    glCompileShader(s);
    GLint ok = 0;
    glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
    if (!ok) { char log[1024]; glGetShaderInfoLog(s, sizeof log, nullptr, log); mcx::Log("shader: %s", log); }
    return s;
}

struct Night
{
    GLuint prog = 0, vao = 0;
    GLint uDepthVP, uEyeRot, uEyePos, uTans, uLayer, uNear, uInf, uFar, uTime, uGain, uMode, uPass, uTexel, uTex;
    int mode = 0;
    bool passthroughOn = true;
    float gain = 1.f;
    XrPosef hud{{0, 0, 0, 1}, {0, -0.25f, -1.2f}};
    bool hudPlaced = false;
    bool sceneAsked = false, anchorsLoaded = false;
    bool bothPinched = false;   // both hands pinching last frame (the passthrough gesture starts on its edge)
    int depthFrames = 0;
    double lastLog = 0;
};

const char* kModes[3] = {"PHOSPHOR", "EDGES", "RANGE"};

void ApplyPassthroughLook(mcx::App& app, const Night& n)
{
    // night-vision camera: dark stays dark, the rest climbs a green phosphor ramp
    float g = n.gain;
    app.passthrough.SetMonoToRgba([g](int i) {
        float v = powf(i / 255.f, 0.7f) * g;
        return XrColor4f{0.15f * v, 1.0f * v > 1.f ? 1.f : 1.0f * v, 0.25f * v, 1.f};
    }, 1.f, XrColor4f{0.4f, 1.f, 0.5f, n.mode == 1 ? 0.6f : 0.f});
}

void Init(Night& n)
{
    n.prog = glCreateProgram();
    GLuint vs = Compile(GL_VERTEX_SHADER, kVs), fs = Compile(GL_FRAGMENT_SHADER, kFs);
    glAttachShader(n.prog, vs);
    glAttachShader(n.prog, fs);
    glLinkProgram(n.prog);
    glDeleteShader(vs);
    glDeleteShader(fs);
    n.uDepthVP = glGetUniformLocation(n.prog, "depthViewProj");
    n.uEyeRot = glGetUniformLocation(n.prog, "eyeRot");
    n.uEyePos = glGetUniformLocation(n.prog, "eyePos");
    n.uTans = glGetUniformLocation(n.prog, "tans");
    n.uLayer = glGetUniformLocation(n.prog, "layer");
    n.uNear = glGetUniformLocation(n.prog, "nearZ");
    n.uInf = glGetUniformLocation(n.prog, "infiniteFar");
    n.uFar = glGetUniformLocation(n.prog, "farZ");
    n.uTime = glGetUniformLocation(n.prog, "time");
    n.uGain = glGetUniformLocation(n.prog, "gain");
    n.uMode = glGetUniformLocation(n.prog, "mode");
    n.uPass = glGetUniformLocation(n.prog, "passthrough");
    n.uTexel = glGetUniformLocation(n.prog, "texel");
    n.uTex = glGetUniformLocation(n.prog, "depthTex");
    glGenVertexArrays(1, &n.vao);
}

void DrawNight(const Night& n, const mcx::Frame& f, const mcx::Eye& eye)
{
    const mcx::DepthFrame& d = f.depth;
    glUseProgram(n.prog);
    mcx::Mat4 r = mcx::Mat4::FromPose(eye.pose);
    float rot[9] = {r.m[0], r.m[1], r.m[2], r.m[4], r.m[5], r.m[6], r.m[8], r.m[9], r.m[10]};
    const XrFovf& fov = f.views[eye.index].fov;
    glUniformMatrix4fv(n.uDepthVP, 1, GL_FALSE, d.viewProj[eye.index].m);
    glUniformMatrix3fv(n.uEyeRot, 1, GL_FALSE, rot);
    glUniform3f(n.uEyePos, eye.pose.position.x, eye.pose.position.y, eye.pose.position.z);
    glUniform4f(n.uTans, tanf(fov.angleLeft), tanf(fov.angleRight), tanf(fov.angleDown), tanf(fov.angleUp));
    glUniform1f(n.uLayer, (float)eye.index);
    glUniform1f(n.uNear, d.nearZ);
    glUniform1f(n.uInf, std::isinf(d.farZ) ? 1.f : 0.f);
    glUniform1f(n.uFar, std::isinf(d.farZ) ? 1000.f : d.farZ);
    glUniform1f(n.uTime, (float)f.seconds);
    glUniform1f(n.uGain, n.gain);
    glUniform1i(n.uMode, n.mode);
    glUniform1f(n.uPass, n.passthroughOn ? 1.f : 0.f);
    glUniform2f(n.uTexel, 1.f / (float)(d.width ? d.width : 1), 1.f / (float)(d.height ? d.height : 1));
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D_ARRAY, d.texture);
    glUniform1i(n.uTex, 0);
    glDisable(GL_DEPTH_TEST);
    glBindVertexArray(n.vao);
    glDrawArrays(GL_TRIANGLES, 0, 3);
    glBindVertexArray(0);
}

void PlaceHud(Night& n, const XrPosef& head, float dt)
{
    // lazy follow: 1.2 m ahead, 25 cm low, turned to the head's yaw; catches up when you look away
    XrVector3f fwd = mcx::Forward(head);
    float len = sqrtf(fwd.x * fwd.x + fwd.z * fwd.z);
    if (len < 1e-3f) return;
    fwd.x /= len;
    fwd.z /= len;
    XrVector3f want{head.position.x + fwd.x * 1.2f, head.position.y - 0.25f, head.position.z + fwd.z * 1.2f};
    float yaw = atan2f(-fwd.x, -fwd.z);
    float k = n.hudPlaced ? 1.f - expf(-dt * 1.5f) : 1.f;
    n.hud.position.x += (want.x - n.hud.position.x) * k;
    n.hud.position.y += (want.y - n.hud.position.y) * k;
    n.hud.position.z += (want.z - n.hud.position.z) * k;
    n.hud.orientation = {0.f, sinf(yaw * 0.5f), 0.f, cosf(yaw * 0.5f)};
    n.hudPlaced = true;
}

// a hand for the HUD: "palm" = the palm faces you (the system gesture): a pinch of that hand does not count
const char* HandState(const mcx::Hand& h)
{
    return !h.tracked ? "-" : h.systemGesture ? "palm" : h.pinching ? "PINCH" : "ok";
}

void DrawHud(mcx::App& app, const Night& n, const mcx::Frame& f, const mcx::Eye& eye)
{
    char text[640];
    const mcx::DepthFrame& d = f.depth;
    char depth[160];
    if (app.depth.Running()) snprintf(depth, sizeof depth, "depth %ux%u near %.2f m %s", d.width, d.height, d.nearZ, d.valid ? "LIVE" : "waiting");
    else snprintf(depth, sizeof depth, "depth OFF: allow spatial data (Settings > Apps > Night Vision)");
    const mcx::Hand& L = app.hands.hand[0];
    const mcx::Hand& R = app.hands.hand[1];
    snprintf(text, sizeof text,
             "NIGHT VISION   %s   gain %.1f   passthrough %s\n%s\nhands L %s R %s   controllers L %s R %s\nroom %zu objects   anchors %zu   %.0f Hz\n"
             "pinch: mode   both pinch or A: passthrough   stick: gain",
             kModes[n.mode], n.gain, n.passthroughOn ? "ON" : "OFF", depth,
             HandState(L), HandState(R),
             app.controllers.ctl[0].active ? "ok" : "-", app.controllers.ctl[1].active ? "ok" : "-",
             app.spatial.scene.size(), app.spatial.anchors.size(), app.refreshRate);
    mcx::Mat4 model = mcx::Mat4::FromPose(n.hud);
    glDisable(GL_DEPTH_TEST);
    app.text.Rect(eye.viewProj, model, -0.42f, 0.11f, 0.84f, 0.27f, XrColor4f{0.0f, 0.06f, 0.02f, 0.75f});
    app.text.Draw(eye.viewProj, model, text, -0.40f, 0.09f, 0.038f, XrColor4f{0.45f, 1.f, 0.55f, 1.f});
}

} // namespace

void android_main(android_app* android)
{
    mcx::Config cfg;
    cfg.name = "Night Vision";
    cfg.passthrough = true;
    cfg.depth = true;
    cfg.hands = true;
    cfg.controllers = true;
    cfg.scene = true;
    cfg.anchors = true;
    cfg.foveation = 2;
    cfg.cpuLevel = 2;
    cfg.gpuLevel = 2;
    mcx::App app;
    if (!app.Init(android, cfg))
    {
        mcx::Log("Night Vision: XR init failed (see above)");
        while (app.Pump()) {}
        return;
    }
    Night n;
    Init(n);
    ApplyPassthroughLook(app, n);
    while (app.Pump())
    {
        mcx::Frame f;
        if (!app.BeginFrame(f)) continue;
        // input only with focus (a system menu takes it away)
        if (f.focused)
        {
            const mcx::Hand& L = app.hands.hand[0];
            const mcx::Hand& R = app.hands.hand[1];
            const mcx::Controller& cl = app.controllers.ctl[0];
            const mcx::Controller& cr = app.controllers.ctl[1];
            // hands: a pinch of EITHER hand is the mode; a pinch that starts while the other hand is pinching belongs to
            // the two-hand gesture. Both hands pinching together (its edge) is the passthrough. A palm towards you
            // (systemGesture) never counts: pinchStarted is false then.
            bool both = L.tracked && R.tracked && L.pinching && R.pinching && !L.systemGesture && !R.systemGesture;
            bool modePinch = (L.pinchStarted && !R.pinching) || (R.pinchStarted && !L.pinching);
            bool look = false;
            if (modePinch || cr.triggerPressed) { n.mode = (n.mode + 1) % 3; look = true; app.controllers.Haptic(1, 0.4f, 0.05f); mcx::Log("mode %s", kModes[n.mode]); }
            if ((both && !n.bothPinched) || cr.primaryPressed || cl.primaryPressed)
            {
                n.passthroughOn = !n.passthroughOn;
                if (n.passthroughOn) app.passthrough.Start(); else app.passthrough.Pause();
                app.projectionFlags = n.passthroughOn ? XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT : 0;
                mcx::Log("passthrough %s", n.passthroughOn ? "on" : "off");
            }
            n.bothPinched = both;
            float stick = cr.stick.y != 0.f ? cr.stick.y : cl.stick.y;
            if (fabsf(stick) > 0.3f) { n.gain = fmaxf(0.3f, fminf(2.5f, n.gain + stick * f.dt)); look = true; }
            if (cl.squeeze > 0.8f && app.spatial.anchorsAvailable && app.spatial.anchors.size() < 8 && f.seconds - n.lastLog > 1.0)
            {
                app.spatial.CreateAnchor(app.local, cl.grip, f.time, true);
                n.lastLog = f.seconds;
            }
            if (look) ApplyPassthroughLook(app, n);
        }
        if (!n.sceneAsked && app.HasPermission(mcx::kPermScene)) { n.sceneAsked = true; app.spatial.QueryScene(); }
        if (!n.anchorsLoaded && app.spatial.anchorsAvailable) { n.anchorsLoaded = true; app.spatial.LoadAnchors(); }
        if (f.depth.valid && (++n.depthFrames == 1 || n.depthFrames % 720 == 0))
            mcx::Log("depth frame %d: %ux%u near %.3f far %s", n.depthFrames, f.depth.width, f.depth.height, f.depth.nearZ, std::isinf(f.depth.farZ) ? "inf" : "finite");
        // the head (between the eyes) for the HUD
        XrPosef head = f.views[0].pose;
        head.position.x = (f.views[0].pose.position.x + f.views[1].pose.position.x) * 0.5f;
        head.position.y = (f.views[0].pose.position.y + f.views[1].pose.position.y) * 0.5f;
        head.position.z = (f.views[0].pose.position.z + f.views[1].pose.position.z) * 0.5f;
        PlaceHud(n, head, f.dt);
        for (int e = 0; e < 2; e++)
        {
            mcx::Eye eye;
            app.BeginEye(e, eye);
            glClearColor(0.f, 0.f, 0.f, app.passthrough.Running() ? 0.f : 1.f);
            glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
            if (f.depth.valid) DrawNight(n, f, eye);
            DrawHud(app, n, f, eye);
            app.EndEye(e);
        }
        app.EndFrame();
    }
    app.Shutdown();
}
