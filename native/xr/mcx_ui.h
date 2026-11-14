// mcx_ui.h: a small world-space UI kit for native apps on mcx_xr.h (MetaCodex). Panels of dark glass (a thin border, an
// optional accent glow and header bar), buttons, toggles, sliders, labels and rows. Shapes come from one signed-distance
// shader, text from mcx::Text. Input is hands first: poke with the index fingertip (a press when the tip crosses the panel
// inside a widget) and the aim ray with a pinch for far panels. Controllers only when `controllers` is set.
// Header-only. Needs nothing but mcx_xr.h.
//
//   mcx::Ui ui;
//   mcx::UiPanel menu;
//   menu.title = "Start";
//   ui.Init(app);                                // once, after app.Init
//   while (app.Pump()) {
//       mcx::Frame f;
//       if (!app.BeginFrame(f)) continue;
//       ui.Begin(app, f);                        // this frame's pointers and timers
//       if (first) menu.PlaceInFront(f, 1.0f);   // 1 m ahead, a little below eye level, facing the user
//       ui.Open(menu);
//       ui.Label("Pinch a button", mcx::UiSize::Small);
//       if (ui.Button("Start", mcx::UiStyle::Primary)) StartGame();
//       ui.Close();
//       for (int e = 0; e < 2; e++) {
//           mcx::Eye eye;
//           app.BeginEye(e, eye);
//           /* the scene */
//           ui.Draw(eye);                        // the UI last, over the scene
//           app.EndEye(e);
//       }
//       ui.End();
//       app.EndFrame();
//   }
//   ui.Destroy();
//
// Units: metres, Y up, right-handed. A panel's box (width, height) is in world metres. Its content (text heights, padding,
// gaps, corner radius, button height, header bar) is sized for 1 m in UiTheme and multiplied by UiPanel::scale. PlaceInFront
// sets the scale to the distance, so the text keeps its angle. A panel's local space: origin at the centre, +X right, +Y up,
// +Z towards the user.
// Colour: UiTheme holds sRGB hex. The kit converts it to linear once per frame (the eye buffers are sRGB).
// Alpha: the kit writes premultiplied colour (rgb * a, a) and blends ONE, ONE_MINUS_SRC_ALPHA, so panels compose over
// passthrough. Where no panel is drawn, the alpha the app cleared with stays.
// GL state: Draw sets what it needs and restores the program, VAO, depth test, culling, depth writes and whether
// blending is on; the blend function, the array buffer and the bound texture stay as mcx::Text left them (set yours).
// The depth test is off for the UI: draw it last, over the scene.
// Input: no pointer while the app menu is open or the app has no focus. A panel with input = false never takes one.
// Widgets keep their state by call order: make the calls in the same order every frame.
#pragma once
#include <mcx_xr.h>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

namespace mcx {

// sRGB hex (0xRRGGBB) to linear RGBA. The eye buffers are sRGB, so shaders and mcx::Text take linear colour.
inline XrColor4f UiSrgb(uint32_t rgb, float alpha = 1.f)
{
    auto lin = [](float c) { return c <= 0.04045f ? c / 12.92f : powf((c + 0.055f) / 1.055f, 2.4f); };
    return XrColor4f{lin(((rgb >> 16) & 255) / 255.f), lin(((rgb >> 8) & 255) / 255.f), lin((rgb & 255) / 255.f), alpha};
}

enum class UiStyle { Secondary, Primary, Danger };   // Primary: the one action of a panel
enum class UiSize { Title, Body, Small };
enum class UiTone { Text, Dim, Accent, Ok, Warn, Danger };

// Palette and sizes. Sizes are for a panel 1 m away; a panel multiplies them by its scale.
struct UiTheme
{
    // palette: MetaCodex's look (sRGB hex)
    uint32_t accent = 0x35D0FF, accentSoft = 0x2A9FD6, frame = 0x5F7C9C, warn = 0xFFB547, danger = 0xFF4D5E, ok = 0x46F28C,
             text = 0xE2F4FF, textDim = 0x9DBDD2, textAccent = 0x8FEBFF, glass = 0x08121B, onAccent = 0x061018;
    float glassAlpha = 0.86f;      // the panel and secondary fills (dark glass): at 0.86 the main text keeps 4.5:1 over a white wall
    float glowAlpha = 0.45f;       // the accent glow outside a panel with glow = true
    // metres at 1 m: text cell heights (mcx::Text cells are 0.5 wide for each unit of height), spacing, shapes
    // body text 2.6 cm at 1 m (quest-native-quality: at least 2.5 cm), small 2 cm; buttons 6.6 cm at 1 m, about Meta's
    // 60 dp for primary controls (quest-native-ui-design)
    float titleText = 0.036f, bodyText = 0.026f, smallText = 0.020f;
    float padding = 0.024f, gap = 0.016f, radius = 0.022f, border = 0.0016f;
    float buttonHeight = 0.066f, headerHeight = 0.060f, glow = 0.03f;
    float cursor = 0.009f;         // the pointer dot's radius as an angle (radians): the same size at any distance
    // interaction, metres in the world
    float reach = 0.10f;           // poke: a fingertip this close in front of a panel hovers the widget under it
    float rayRange = 4.f;          // an aim ray reaches a panel this far
    float touchGap = 0.012f;       // the least gap between widgets (poke: 12 mm)
    // motion, seconds
    float appearTime = 0.18f;      // a panel fades and grows in over this time
    float flashTime = 0.25f;       // a button flashes this long after a click
};

struct UiPanel
{
    XrPosef pose{{0.f, 0.f, 0.f, 1.f}, {0.f, 0.f, -1.f}};  // the centre of the box; local +Z faces the user
    float width = 0.40f, height = 0.30f;   // the box, metres in the world
    float scale = 1.f;                     // content scale: 1 = sized for 1 m; PlaceInFront sets the distance
    const char* title = nullptr;           // a header bar with this text (nullptr: no bar)
    bool glow = false;                     // a soft accent glow outside the edge
    bool input = true;                     // false: a HUD, never a pointer target
    // turns the panel to the user: `distance` ahead of the head, `drop` below eye level, yaw only; scale = distance
    void PlaceInFront(const Frame& f, float distance = 1.f, float drop = 0.1f);
    float appear = 0.f;                    // kit state: do not set
    uint64_t lastFrame = 0;                // kit state: do not set
};

inline void UiPanel::PlaceInFront(const Frame& f, float distance, float drop)
{
    const XrVector3f a = f.views[0].pose.position, b = f.views[1].pose.position;
    XrVector3f fwd = Forward(f.views[0].pose);
    float len = sqrtf(fwd.x * fwd.x + fwd.z * fwd.z);
    if (len < 1e-3f) { fwd = {0.f, 0.f, -1.f}; len = 1.f; }
    fwd.x /= len;
    fwd.z /= len;
    pose.position = {(a.x + b.x) * 0.5f + fwd.x * distance, (a.y + b.y) * 0.5f - drop, (a.z + b.z) * 0.5f + fwd.z * distance};
    const float yaw = atan2f(-fwd.x, -fwd.z);   // local +Z = the direction back to the user
    pose.orientation = {0.f, sinf(yaw * 0.5f), 0.f, cosf(yaw * 0.5f)};
    scale = distance > 0.f ? distance : 1.f;
}

namespace detail {

// ---- small helpers (inline, private to the kit)
inline XrVector3f UiAdd(XrVector3f a, XrVector3f b) { return {a.x + b.x, a.y + b.y, a.z + b.z}; }
inline XrVector3f UiSub(XrVector3f a, XrVector3f b) { return {a.x - b.x, a.y - b.y, a.z - b.z}; }
inline XrVector3f UiScale(XrVector3f a, float s) { return {a.x * s, a.y * s, a.z * s}; }
inline float UiDot(XrVector3f a, XrVector3f b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
inline float UiLen(XrVector3f a) { return sqrtf(UiDot(a, a)); }
inline bool UiInBox(float x, float y, float w, float h, float slop) { return fabsf(x) <= w * 0.5f + slop && fabsf(y) <= h * 0.5f + slop; }
inline XrColor4f UiMix(XrColor4f a, XrColor4f b, float t) { return {a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t}; }
// the largest text height up to `t` whose `chars` cells fit in `avail` metres (cells are 0.5 x height)
inline float UiFit(float t, float chars, float avail)
{
    if (chars <= 0.f) return t;
    const float fit = avail / (chars * 0.5f);
    return fit < t ? (fit > 0.f ? fit : 0.f) : t;
}

// the palette in linear colour (the alphas of the theme are kept)
struct UiPal { XrColor4f accent, accentSoft, frame, warn, danger, ok, text, dim, textAccent, glass, onAccent; };
inline UiPal UiMakePal(const UiTheme& t)
{
    UiPal p;
    p.accent = UiSrgb(t.accent);
    p.accentSoft = UiSrgb(t.accentSoft);
    p.frame = UiSrgb(t.frame);
    p.warn = UiSrgb(t.warn);
    p.danger = UiSrgb(t.danger);
    p.ok = UiSrgb(t.ok);
    p.text = UiSrgb(t.text);
    p.dim = UiSrgb(t.textDim);
    p.textAccent = UiSrgb(t.textAccent);
    p.glass = UiSrgb(t.glass, t.glassAlpha);
    p.onAccent = UiSrgb(t.onAccent);
    return p;
}

inline GLuint UiCompile(GLenum type, const char* src)
{
    GLuint s = glCreateShader(type);
    glShaderSource(s, 1, &src, nullptr);
    glCompileShader(s);
    GLint ok = 0;
    glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
    if (!ok) { char log[512]; glGetShaderInfoLog(s, sizeof log, nullptr, log); Log("ui: shader: %s", log); }
    return s;
}

// one shader for every box: a rounded box with a fill, a border, an outer glow and a header bar, all as a signed distance
inline const char* kUiVs = R"(#version 300 es
precision highp float;
layout(location = 0) in vec2 aCorner;       // a unit square corner, -0.5 .. 0.5
uniform mat4 uViewProj;
uniform mat4 uModel;
uniform vec2 uCenter;                       // the box centre (panel metres)
uniform vec2 uQuad;                         // the quad: the box plus its glow on each side
out vec2 vP;
void main() {
    vP = uCenter + aCorner * uQuad;
    gl_Position = uViewProj * uModel * vec4(vP, 0.0, 1.0);
}
)";

inline const char* kUiFs = R"(#version 300 es
precision highp float;
in vec2 vP;
uniform vec2 uCenter, uHalf;                     // the box: centre and half sizes (panel metres)
uniform float uRadius, uBorder, uGlow, uHeader;  // corner radius, border width, glow reach, header height (0: none)
uniform vec4 uFill, uEdge, uGlowCol, uHeadCol;   // linear RGB and alpha, not premultiplied
uniform float uAlpha;
out vec4 o;
float sdBox(vec2 p, vec2 b, float r) {
    vec2 q = abs(p) - b + vec2(r);
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}
vec4 over(vec4 back, vec4 front) { return front + back * (1.0 - front.a); }             // premultiplied over
vec4 layer(vec4 c, float cover) { return vec4(c.rgb * c.a * cover, c.a * cover); }     // premultiplied, with coverage
void main() {
    vec2 p = vP - uCenter;
    float d = sdBox(p, uHalf, min(uRadius, min(uHalf.x, uHalf.y)));   // metres, < 0 inside the box
    float aa = max(fwidth(d), 1e-6);                                 // one pixel, in metres
    float inside = 1.0 - smoothstep(-aa, aa, d);
    vec4 c = vec4(0.0);
    if (uGlow > 0.0) {                                               // the glow lives outside the box only
        float g = clamp(1.0 - max(d, 0.0) / uGlow, 0.0, 1.0);
        c = over(c, layer(uGlowCol, g * g * (1.0 - inside)));
    }
    c = over(c, layer(uFill, inside));
    if (uHeader > 0.0) {
        float top = p.y - (uHalf.y - uHeader);                       // > 0 inside the header bar
        c = over(c, layer(uHeadCol, smoothstep(-aa, aa, top) * inside));
        c = over(c, layer(uEdge, 0.5 * inside * (1.0 - smoothstep(0.5 * uBorder - aa, 0.5 * uBorder + aa, abs(top)))));
    }
    c = over(c, layer(uEdge, inside * smoothstep(-uBorder - aa, -uBorder + aa, d)));   // the border: a band inside the edge
    o = c * uAlpha;
}
)";

} // namespace detail

class Ui
{
public:
    UiTheme theme;
    bool controllers = false;              // also point with the controllers (aim ray, trigger); hands only by default

    bool Init(App& app);                   // the shader and the quad: once, after app.Init
    void Begin(App& app, const Frame& f);  // once per frame, after BeginFrame: pointers and timers
    void Open(UiPanel& p);                 // starts a panel: the widgets below go into it
    void Close();                          // ends the open panel (its pointer dot goes on top)
    void Row(int count);                   // the next `count` widgets share one line
    void Space(float metres);              // a gap below the last widget (scaled like the theme)
    void Label(const char* text, UiSize size = UiSize::Body, UiTone tone = UiTone::Text);
    bool Button(const char* label, UiStyle style = UiStyle::Secondary, bool enabled = true); // true on the click frame
    bool Toggle(const char* label, bool& on);                                               // true when it changed
    bool Slider(const char* label, float& value, float min, float max);                     // true when it changed
    void Draw(const Eye& eye);             // every panel of this frame, into one eye: after the scene
    void End();                            // the frame is done; its panels are this frame's pointer targets
    void Destroy();

private:
    struct Spot { bool ok = false; float lx = 0.f, ly = 0.f, sd = 0.f, psd = 0.f, dist = 0.f; };  // a pointer on a panel plane
    struct Contact { bool over = false, click = false, held = false; float lx = 0.f, ly = 0.f; };
    struct Ptr                             // one hand (or controller) this frame
    {
        bool on = false;                   // live: tracked, the app has focus, its menu is shut
        bool tipOk = false, lastOk = false;
        bool rayOk = false, select = false, held = false;   // aim ray; pinch started / held
        bool poke = false;                 // this frame the pointer is the fingertip (else the ray)
        XrVector3f tip{}, last{}, origin{}, dir{};
        const UiPanel* owner = nullptr;    // the panel it points at
        int capture = -1;                  // the widget (call order) it holds, -1 none
        const UiPanel* capPanel = nullptr;
        bool capPoke = false;              // the held widget was pressed with the fingertip
    };
    struct Snap { const UiPanel* p; XrPosef pose; float w, h, scale, grow, alpha, appear; bool in; };
    struct Cmd { uint8_t kind; uint16_t panel; float x, y, a, b, radius, border, glow, header; XrColor4f c1, c2, c3, c4; uint32_t off, len; };
    struct Widget { float hover = 0.f, press = 0.f, flash = 0.f, pos = 0.f; };

    Spot Locate(const Ptr& q, bool poke, const XrPosef& pose) const;
    void Aim(Ptr& q);
    Contact Probe(int h, int me, float x, float y, float w, float hgt, bool enabled);
    Widget& State(int me);
    void Next(float h, float& x, float& y, float& w);
    void Box(float cx, float cy, float hw, float hh, float radius, float border, float glow, XrColor4f fill, XrColor4f edge,
             XrColor4f glowCol, float header, XrColor4f headCol);
    void Words(const char* s, float x, float y, float height, XrColor4f c);
    void Release(int h, int me);
    void Hold(int h, int me);

    App* app_ = nullptr;
    GLuint prog_ = 0, vao_ = 0, vbo_ = 0;
    GLint uVP_ = -1, uModel_ = -1, uCenter_ = -1, uQuad_ = -1, uHalf_ = -1, uRadius_ = -1, uBorder_ = -1, uGlow_ = -1,
          uHeader_ = -1, uFill_ = -1, uEdge_ = -1, uGlowCol_ = -1, uHeadCol_ = -1, uAlpha_ = -1;
    Ptr ptr_[2];
    std::vector<Snap> cur_, last_;         // this frame's panels; the last frame's (pointer targets)
    std::vector<Cmd> cmds_;
    std::vector<char> text_;               // the strings of this frame, NUL-terminated
    std::vector<Widget> widgets_;
    detail::UiPal pal_{};
    XrVector3f head_{0.f, 0.f, 0.f};
    float dt_ = 0.f, gap_ = 0.f, innerL_ = 0.f, innerW_ = 0.f, y_ = 0.f, rowTop_ = 0.f, rowH_ = 0.f;
    int ord_ = 0, panel_ = -1, rowLeft_ = 0, rowCount_ = 0;
    uint64_t frame_ = 0;
    UiPanel* openP_ = nullptr;
};

inline bool Ui::Init(App& app)
{
    app_ = &app;
    cmds_.reserve(256);
    text_.reserve(4096);
    cur_.reserve(8);
    last_.reserve(8);
    widgets_.resize(64);
    prog_ = glCreateProgram();
    GLuint vs = detail::UiCompile(GL_VERTEX_SHADER, detail::kUiVs), fs = detail::UiCompile(GL_FRAGMENT_SHADER, detail::kUiFs);
    glAttachShader(prog_, vs);
    glAttachShader(prog_, fs);
    glLinkProgram(prog_);
    glDeleteShader(vs);
    glDeleteShader(fs);
    GLint ok = 0;
    glGetProgramiv(prog_, GL_LINK_STATUS, &ok);
    if (!ok) { char log[512]; glGetProgramInfoLog(prog_, sizeof log, nullptr, log); Log("ui: link: %s", log); return false; }
    uVP_ = glGetUniformLocation(prog_, "uViewProj");
    uModel_ = glGetUniformLocation(prog_, "uModel");
    uCenter_ = glGetUniformLocation(prog_, "uCenter");
    uQuad_ = glGetUniformLocation(prog_, "uQuad");
    uHalf_ = glGetUniformLocation(prog_, "uHalf");
    uRadius_ = glGetUniformLocation(prog_, "uRadius");
    uBorder_ = glGetUniformLocation(prog_, "uBorder");
    uGlow_ = glGetUniformLocation(prog_, "uGlow");
    uHeader_ = glGetUniformLocation(prog_, "uHeader");
    uFill_ = glGetUniformLocation(prog_, "uFill");
    uEdge_ = glGetUniformLocation(prog_, "uEdge");
    uGlowCol_ = glGetUniformLocation(prog_, "uGlowCol");
    uHeadCol_ = glGetUniformLocation(prog_, "uHeadCol");
    uAlpha_ = glGetUniformLocation(prog_, "uAlpha");
    const GLfloat corners[12] = {-0.5f, -0.5f, 0.5f, -0.5f, 0.5f, 0.5f, -0.5f, -0.5f, 0.5f, 0.5f, -0.5f, 0.5f};
    glGenVertexArrays(1, &vao_);
    glBindVertexArray(vao_);
    glGenBuffers(1, &vbo_);
    glBindBuffer(GL_ARRAY_BUFFER, vbo_);
    glBufferData(GL_ARRAY_BUFFER, sizeof corners, corners, GL_STATIC_DRAW);
    glEnableVertexAttribArray(0);
    glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 0, nullptr);
    glBindVertexArray(0);
    glBindBuffer(GL_ARRAY_BUFFER, 0);
    return true;
}

inline void Ui::Destroy()
{
    if (vbo_) glDeleteBuffers(1, &vbo_);
    if (vao_) glDeleteVertexArrays(1, &vao_);
    if (prog_) glDeleteProgram(prog_);
    vbo_ = vao_ = prog_ = 0;
    app_ = nullptr;
    openP_ = nullptr;
    cmds_.clear();
    text_.clear();
    cur_.clear();
    last_.clear();
}

// the pointer of hand `poke` (fingertip or ray) on a panel plane with pose `pose`
inline Ui::Spot Ui::Locate(const Ptr& q, bool poke, const XrPosef& pose) const
{
    Spot r;
    const XrVector3f n = Rotate(pose.orientation, {0.f, 0.f, 1.f});
    const XrQuaternionf inv{-pose.orientation.x, -pose.orientation.y, -pose.orientation.z, pose.orientation.w};
    if (poke)
    {
        const XrVector3f v = detail::UiSub(q.tip, pose.position), l = Rotate(inv, v);
        r.ok = true;
        r.lx = l.x;
        r.ly = l.y;
        r.sd = detail::UiDot(v, n);                 // + in front of the panel (the user's side)
        r.dist = fabsf(r.sd);
        r.psd = q.lastOk ? detail::UiDot(detail::UiSub(q.last, pose.position), n) : -1.f;
        return r;
    }
    if (!q.rayOk) return r;
    const float den = detail::UiDot(q.dir, n);
    if (den > -1e-4f) return r;             // parallel, or from behind the panel
    const float t = detail::UiDot(detail::UiSub(pose.position, q.origin), n) / den;
    if (t <= 0.f || t > theme.rayRange) return r;
    const XrVector3f l = Rotate(inv, detail::UiSub(detail::UiAdd(q.origin, detail::UiScale(q.dir, t)), pose.position));
    r.ok = true;
    r.lx = l.x;
    r.ly = l.y;
    r.dist = t;
    return r;
}

// who a hand points at: a held widget keeps its panel; else the nearest panel under the fingertip (poke wins); else the
// nearest panel the aim ray hits from the front. Only last frame's panels count (one frame of lag, no ordering problems).
inline void Ui::Aim(Ptr& q)
{
    q.owner = nullptr;
    if (!q.on) return;
    if (q.capture >= 0)
    {
        for (const Snap& s : last_)
            if (s.p == q.capPanel) { q.owner = s.p; q.poke = q.capPoke; return; }
        q.capture = -1;                     // the panel is gone
        q.capPanel = nullptr;
    }
    float best = 1e9f;
    for (const Snap& s : last_)
    {
        if (!s.in || s.appear < 0.9f || !q.tipOk) continue;
        const Spot k = Locate(q, true, s.pose);
        if (k.sd < -0.03f || k.sd > theme.reach || !detail::UiInBox(k.lx, k.ly, s.w, s.h, 0.02f)) continue;
        if (fabsf(k.sd) < best) { best = fabsf(k.sd); q.owner = s.p; q.poke = true; }
    }
    if (q.owner) return;
    best = 1e9f;
    for (const Snap& s : last_)
    {
        if (!s.in || s.appear < 0.9f || !q.rayOk) continue;
        const Spot k = Locate(q, false, s.pose);
        if (!k.ok || !detail::UiInBox(k.lx, k.ly, s.w, s.h, 0.f)) continue;
        if (k.dist < best) { best = k.dist; q.owner = s.p; q.poke = false; }
    }
}

inline void Ui::Begin(App& app, const Frame& f)
{
    app_ = &app;
    dt_ = fminf(fmaxf(f.dt, 0.f), 0.1f);
    frame_++;
    cmds_.clear();
    text_.clear();
    cur_.clear();
    ord_ = 0;
    panel_ = -1;
    openP_ = nullptr;
    rowLeft_ = 0;
    pal_ = detail::UiMakePal(theme);
    const XrVector3f a = f.views[0].pose.position, b = f.views[1].pose.position;
    head_ = {(a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f, (a.z + b.z) * 0.5f};
    const bool live = f.focused && !app.MenuOpen();   // the app menu takes the hands while it is open
    for (int h = 0; h < 2; h++)
    {
        Ptr& q = ptr_[h];
        q.lastOk = q.tipOk;
        q.last = q.tip;
        q.tipOk = q.rayOk = q.select = q.held = false;
        const Hand& hd = app.hands.hand[h];
        if (hd.tracked)
        {
            const XrHandJointLocationEXT& j = hd.joints[XR_HAND_JOINT_INDEX_TIP_EXT];
            if (j.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT) { q.tip = j.pose.position; q.tipOk = true; }
            if (hd.aimValid) { q.origin = hd.aim.position; q.dir = Forward(hd.aim); q.rayOk = true; }
            q.select = hd.pinchStarted && !hd.systemGesture;
            q.held = hd.pinching && !hd.systemGesture;
        }
        if (controllers && !q.rayOk && app.controllers.ctl[h].active)
        {
            const Controller& c = app.controllers.ctl[h];
            q.origin = c.aim.position;
            q.dir = Forward(c.aim);
            q.rayOk = true;
            q.select = c.triggerPressed;
            q.held = c.trigger > 0.5f;
        }
        q.on = live && (q.tipOk || q.rayOk);
        if (!q.on) { q.capture = -1; q.capPanel = nullptr; }
        Aim(q);
    }
}

inline void Ui::Open(UiPanel& p)
{
    if (openP_ || cur_.size() >= 65535) return;
    if (p.lastFrame + 1 != frame_) p.appear = 0.f;    // not open last frame: it appears again
    p.lastFrame = frame_;
    p.appear = fminf(1.f, p.appear + (theme.appearTime > 0.f ? dt_ / theme.appearTime : 1.f));
    const float t = p.appear, e = 1.f - (1.f - t) * (1.f - t) * (1.f - t);   // ease-out
    Snap s{};
    s.p = &p;
    s.pose = p.pose;
    s.w = p.width;
    s.h = p.height;
    s.scale = p.scale > 0.f ? p.scale : 1.f;
    s.grow = 0.92f + 0.08f * e;
    s.alpha = e;
    s.appear = t;
    s.in = p.input;
    cur_.push_back(s);
    panel_ = (int)cur_.size() - 1;
    openP_ = &p;
    const float k = s.scale, hw = p.width * 0.5f, hh = p.height * 0.5f;
    const float bar = p.title ? theme.headerHeight * k : 0.f, pad = theme.padding * k;
    gap_ = fmaxf(theme.gap * k, theme.touchGap);
    innerL_ = -hw + pad;
    innerW_ = p.width - 2.f * pad;
    y_ = hh - bar - pad;
    XrColor4f edge = pal_.frame, glowC = pal_.accent, headC = pal_.accentSoft;
    edge.a = 0.85f;
    glowC.a = theme.glowAlpha;
    headC.a = 0.22f;
    Box(0.f, 0.f, hw, hh, theme.radius * k, theme.border * k, p.glow ? theme.glow * k : 0.f, pal_.glass, edge, glowC, bar, headC);
    if (p.title)
    {
        const float th = theme.titleText * k;
        Words(p.title, innerL_, hh - (bar - th) * 0.5f, th, pal_.textAccent);
    }
}

inline void Ui::Row(int count)
{
    if (!openP_ || count < 1) return;
    rowLeft_ = rowCount_ = count;
    rowTop_ = y_;
    rowH_ = 0.f;
}

inline void Ui::Space(float metres)
{
    if (openP_ && rowLeft_ == 0) y_ -= metres * cur_[panel_].scale;
}

// the box of the next widget (panel metres): x left, y top, w width; a row gives each widget one slot
inline void Ui::Next(float h, float& x, float& y, float& w)
{
    if (rowLeft_ > 0)
    {
        const int i = rowCount_ - rowLeft_;
        const float slot = (innerW_ - gap_ * (float)(rowCount_ - 1)) / (float)rowCount_;
        x = innerL_ + (float)i * (slot + gap_);
        y = rowTop_;
        w = slot;
        rowH_ = fmaxf(rowH_, h);
        if (--rowLeft_ == 0) y_ = rowTop_ - rowH_ - gap_;
        return;
    }
    x = innerL_;
    y = y_;
    w = innerW_;
    y_ -= h + gap_;
}

inline Ui::Widget& Ui::State(int me)
{
    if ((size_t)me >= widgets_.size()) widgets_.resize((size_t)me + 64);
    return widgets_[(size_t)me];
}

// one hand over one widget (rect x, y, w, hgt in panel metres): over = the pointer is on it, click = a press starts on
// it this frame, held = the press goes on. A widget held by another one gets nothing from this hand.
inline Ui::Contact Ui::Probe(int h, int me, float x, float y, float w, float hgt, bool enabled)
{
    Contact c;
    const Ptr& q = ptr_[h];
    if (!enabled || !q.on || q.owner != openP_ || (q.capture >= 0 && q.capture != me)) return c;
    const Spot s = Locate(q, q.poke, cur_[panel_].pose);
    if (!s.ok) return c;
    c.lx = s.lx;
    c.ly = s.ly;
    const float slop = q.poke ? 0.006f : 0.f;
    c.over = s.lx >= x - slop && s.lx <= x + w + slop && s.ly <= y + slop && s.ly >= y - hgt - slop;
    if (q.poke)
    {
        c.over = c.over && s.sd >= -0.03f && s.sd <= theme.reach;
        c.click = c.over && s.psd > 0.f && s.sd <= 0.f;     // the tip crossed the panel inside the widget
        c.held = s.sd <= 0.01f && s.sd >= -0.06f;           // still at the panel or behind it
    }
    else
    {
        c.click = c.over && q.select;                       // the pinch started on the widget
        c.held = q.held;
    }
    return c;
}

inline void Ui::Hold(int h, int me)
{
    ptr_[h].capture = me;
    ptr_[h].capPanel = openP_;
    ptr_[h].capPoke = ptr_[h].poke;
}

inline void Ui::Release(int h, int me)
{
    if (ptr_[h].capture == me) { ptr_[h].capture = -1; ptr_[h].capPanel = nullptr; }
}

inline void Ui::Box(float cx, float cy, float hw, float hh, float radius, float border, float glow, XrColor4f fill, XrColor4f edge,
                    XrColor4f glowCol, float header, XrColor4f headCol)
{
    Cmd c{};
    c.kind = 0;
    c.panel = (uint16_t)panel_;
    c.x = cx;
    c.y = cy;
    c.a = hw;
    c.b = hh;
    c.radius = radius;
    c.border = border;
    c.glow = glow;
    c.header = header;
    c.c1 = fill;
    c.c2 = edge;
    c.c3 = glowCol;
    c.c4 = headCol;
    cmds_.push_back(c);
}

inline void Ui::Words(const char* s, float x, float y, float height, XrColor4f col)
{
    Cmd c{};
    c.kind = 1;
    c.panel = (uint16_t)panel_;
    c.x = x;
    c.y = y;
    c.a = height;
    c.c1 = col;
    const size_t n = strlen(s);
    c.off = (uint32_t)text_.size();
    c.len = (uint32_t)n;
    text_.insert(text_.end(), s, s + n);
    text_.push_back('\0');
    cmds_.push_back(c);
}

inline void Ui::Label(const char* text, UiSize size, UiTone tone)
{
    if (!openP_ || !text) return;
    const float k = cur_[panel_].scale;
    const float t = (size == UiSize::Title ? theme.titleText : size == UiSize::Small ? theme.smallText : theme.bodyText) * k;
    int lines = 1;
    for (const char* p = text; *p; p++) if (*p == '\n') lines++;
    float x, y, w;
    Next(t * (1.1f * (float)lines - 0.1f), x, y, w);
    XrColor4f col = pal_.text;
    switch (tone)
    {
    case UiTone::Dim: col = pal_.dim; break;
    case UiTone::Accent: col = pal_.textAccent; break;
    case UiTone::Ok: col = pal_.ok; break;
    case UiTone::Warn: col = pal_.warn; break;
    case UiTone::Danger: col = pal_.danger; break;
    default: break;
    }
    Words(text, x, y, t, col);
}

inline bool Ui::Button(const char* label, UiStyle style, bool enabled)
{
    const int me = ord_++;
    if (!openP_ || !label) return false;
    Widget& st = State(me);
    const float k = cur_[panel_].scale, h = theme.buttonHeight * k, pad = theme.padding * k;
    float x, y, w;
    Next(h, x, y, w);
    bool over = false, click = false;
    for (int i = 0; i < 2; i++)
    {
        const Contact c = Probe(i, me, x, y, w, h, enabled);
        over = over || c.over;
        if (c.click) { click = true; Hold(i, me); }
        if (!c.held) Release(i, me);
    }
    const bool pressed = ptr_[0].capture == me || ptr_[1].capture == me;
    st.hover += ((over ? 1.f : 0.f) - st.hover) * (1.f - expf(-dt_ * 14.f));
    st.press += ((pressed && enabled ? 1.f : 0.f) - st.press) * (1.f - expf(-dt_ * 30.f));
    st.flash = click ? 1.f : fmaxf(0.f, st.flash - dt_ / fmaxf(theme.flashTime, 1e-3f));
    // the face sinks while it is pressed (the top stays, the bottom moves up under it)
    const float sink = st.press * 0.12f * h, fh = h - sink;
    const float cx = x + w * 0.5f, cy = y - sink * 0.5f - fh * 0.5f;
    XrColor4f fill = pal_.glass, edge{}, ink = pal_.text;
    if (style == UiStyle::Primary) { fill = pal_.accent; fill.a = 0.92f; ink = pal_.onAccent; }
    else if (style == UiStyle::Danger) { fill = pal_.danger; fill.a = 0.92f; ink = pal_.onAccent; }
    else { edge = pal_.frame; edge.a = 0.8f; }
    fill = detail::UiMix(fill, {1.f, 1.f, 1.f, fill.a}, 0.12f * st.hover);          // hover lifts the fill
    fill = detail::UiMix(fill, {0.f, 0.f, 0.f, fill.a}, 0.18f * st.press);          // a press darkens it
    fill = detail::UiMix(fill, {pal_.text.r, pal_.text.g, pal_.text.b, fill.a}, 0.35f * st.flash);   // a click flashes it
    XrColor4f accentA = pal_.accent;
    accentA.a = 0.95f;
    edge = detail::UiMix(edge, accentA, st.hover);
    const float glowK = fminf(1.f, 0.5f * st.hover + st.flash);
    XrColor4f glowC = pal_.accent;
    glowC.a = 0.5f * glowK;
    if (!enabled) { fill.a *= 0.4f; edge.a *= 0.4f; ink = pal_.dim; ink.a = 0.6f; }
    Box(cx, cy, w * 0.5f, fh * 0.5f, fminf(theme.radius * k, fh * 0.5f), theme.border * k, theme.glow * k * glowK, fill, edge, glowC, 0.f, XrColor4f{});
    const float n = (float)strlen(label), t = detail::UiFit(theme.bodyText * k, n, w - 2.f * pad);
    Words(label, cx - n * 0.25f * t, cy + t * 0.5f, t, ink);
    return click && enabled;
}

inline bool Ui::Toggle(const char* label, bool& on)
{
    const int me = ord_++;
    if (!openP_ || !label) return false;
    Widget& st = State(me);
    const float k = cur_[panel_].scale, h = theme.buttonHeight * k, pad = theme.padding * k;
    float x, y, w;
    Next(h, x, y, w);
    bool over = false, click = false;
    for (int i = 0; i < 2; i++)
    {
        const Contact c = Probe(i, me, x, y, w, h, true);
        over = over || c.over;
        if (c.click) { click = true; Hold(i, me); }
        if (!c.held) Release(i, me);
    }
    if (click) on = !on;
    const bool pressed = ptr_[0].capture == me || ptr_[1].capture == me;
    st.hover += ((over ? 1.f : 0.f) - st.hover) * (1.f - expf(-dt_ * 14.f));
    st.press += ((pressed ? 1.f : 0.f) - st.press) * (1.f - expf(-dt_ * 30.f));
    st.pos += ((on ? 1.f : 0.f) - st.pos) * (1.f - expf(-dt_ * 16.f));
    const float cy = y - h * 0.5f, th = h * 0.55f, tw = th * 1.8f, tx = x + w - tw;
    const float t = detail::UiFit(theme.bodyText * k, (float)strlen(label), w - tw - pad);
    Words(label, x, cy + t * 0.5f, t, pal_.text);
    XrColor4f track = detail::UiMix(pal_.glass, {pal_.accent.r, pal_.accent.g, pal_.accent.b, 0.9f}, st.pos);
    XrColor4f edge = detail::UiMix({pal_.frame.r, pal_.frame.g, pal_.frame.b, 0.8f}, {pal_.accent.r, pal_.accent.g, pal_.accent.b, 1.f},
                           fmaxf(st.hover, st.pos));
    Box(tx + tw * 0.5f, cy, tw * 0.5f, th * 0.5f, th * 0.5f, theme.border * k, 0.f, track, edge, XrColor4f{}, 0.f, XrColor4f{});
    const float kd = th * 0.78f, x0 = tx + th * 0.5f, x1 = tx + tw - th * 0.5f, kx = x0 + (x1 - x0) * st.pos;
    const XrColor4f knob = detail::UiMix(pal_.frame, pal_.text, st.pos);
    Box(kx, cy, kd * 0.5f, kd * 0.5f, kd * 0.5f, 0.f, 0.f, knob, XrColor4f{}, XrColor4f{}, 0.f, XrColor4f{});
    return click;
}

inline bool Ui::Slider(const char* label, float& value, float min, float max)
{
    const int me = ord_++;
    if (!openP_ || !label) return false;
    Widget& st = State(me);
    const float k = cur_[panel_].scale, t = theme.bodyText * k, knob = theme.buttonHeight * k * 0.5f, pad = theme.padding * k;
    const float lineGap = gap_ * 0.6f, r = knob * 0.5f, rowH = t + lineGap + knob;
    float x, y, w;
    Next(rowH, x, y, w);
    const float x0 = x + r, x1 = x + w - r, ty = y - t - lineGap - r;   // the track's centre line
    bool over = false, changed = false;
    for (int i = 0; i < 2; i++)
    {
        const Contact c = Probe(i, me, x, y, w, rowH, true);
        over = over || c.over;
        if (c.click) Hold(i, me);
        if (ptr_[i].capture == me)
        {
            if (c.held)
            {
                const float f = fminf(fmaxf((c.lx - x0) / fmaxf(x1 - x0, 1e-4f), 0.f), 1.f);
                const float v = min + f * (max - min);
                if (v != value) { value = v; changed = true; }
            }
            else Release(i, me);
        }
    }
    const bool pressed = ptr_[0].capture == me || ptr_[1].capture == me;
    st.hover += ((over ? 1.f : 0.f) - st.hover) * (1.f - expf(-dt_ * 14.f));
    st.press += ((pressed ? 1.f : 0.f) - st.press) * (1.f - expf(-dt_ * 30.f));
    const float frac = max > min ? fminf(fmaxf((value - min) / (max - min), 0.f), 1.f) : 0.f;
    const float kx = x0 + frac * (x1 - x0);
    char buf[32];
    if (max - min >= 10.f) snprintf(buf, sizeof buf, "%.0f", value);
    else snprintf(buf, sizeof buf, "%.2f", value);
    const float vw = (float)strlen(buf) * 0.5f * t;
    Words(label, x, y, detail::UiFit(t, (float)strlen(label), w - vw - pad), pal_.text);
    Words(buf, x + w - vw, y, t, pal_.textAccent);
    const float bar = knob * 0.28f;
    Box((x0 + x1) * 0.5f, ty, (x1 - x0) * 0.5f, bar * 0.5f, bar * 0.5f, 0.f, 0.f, {pal_.frame.r, pal_.frame.g, pal_.frame.b, 0.5f},
        XrColor4f{}, XrColor4f{}, 0.f, XrColor4f{});
    if (kx - x0 > 1e-4f)
        Box((x0 + kx) * 0.5f, ty, (kx - x0) * 0.5f, bar * 0.5f, bar * 0.5f, 0.f, 0.f, {pal_.accent.r, pal_.accent.g, pal_.accent.b, 0.9f},
            XrColor4f{}, XrColor4f{}, 0.f, XrColor4f{});
    const float glowK = fminf(1.f, 0.5f * st.hover + 0.6f * st.press);
    XrColor4f glowC = pal_.accent;
    glowC.a = 0.5f * glowK;
    Box(kx, ty, r, r, r, theme.border * k, theme.glow * k * glowK, pal_.text, XrColor4f{}, glowC, 0.f, XrColor4f{});
    return changed;
}

inline void Ui::Close()
{
    if (!openP_) return;
    const Snap& s = cur_[panel_];
    for (int i = 0; i < 2; i++)
    {
        const Ptr& q = ptr_[i];
        if (!q.on || q.owner != openP_) continue;
        const Spot p = Locate(q, q.poke, s.pose);
        if (!p.ok || !detail::UiInBox(p.lx, p.ly, s.w, s.h, 0.01f)) continue;
        // the dot keeps its angle: its radius grows with the distance of the point it sits on
        const XrVector3f world = detail::UiAdd(s.pose.position, Rotate(s.pose.orientation, {p.lx, p.ly, 0.f}));
        const bool held = q.capture >= 0;
        const float r = fmaxf(theme.cursor * detail::UiLen(detail::UiSub(world, head_)), 0.002f) * (held ? 0.7f : 1.f);
        XrColor4f dot = held ? pal_.text : pal_.accent;
        dot.a = 0.95f;
        XrColor4f glowC = pal_.accent;
        glowC.a = 0.5f;
        Box(p.lx, p.ly, r, r, r, 0.f, r * 1.2f, dot, XrColor4f{}, glowC, 0.f, XrColor4f{});
    }
    openP_ = nullptr;
    panel_ = -1;
    rowLeft_ = 0;
}

inline void Ui::End()
{
    if (openP_) Close();
    last_.assign(cur_.begin(), cur_.end());
}

inline void Ui::Draw(const Eye& eye)
{
    if (!app_ || cmds_.empty()) return;
    GLint prog = 0, vao = 0;
    glGetIntegerv(GL_CURRENT_PROGRAM, &prog);
    glGetIntegerv(GL_VERTEX_ARRAY_BINDING, &vao);
    const GLboolean blend = glIsEnabled(GL_BLEND), depth = glIsEnabled(GL_DEPTH_TEST), cull = glIsEnabled(GL_CULL_FACE);
    GLboolean mask = GL_TRUE;
    glGetBooleanv(GL_DEPTH_WRITEMASK, &mask);
    int shown = -1;
    float alpha = 1.f;
    Mat4 model;
    for (const Cmd& c : cmds_)
    {
        const Snap& s = cur_[c.panel];
        if ((int)c.panel != shown)
        {
            shown = (int)c.panel;
            model = Mat4::FromPose(s.pose) * Mat4::Scale(s.grow, s.grow, 1.f);
            alpha = s.alpha;
        }
        if (c.kind == 1)
        {
            XrColor4f col = c.c1;
            col.a *= alpha;
            app_->text.Draw(eye.viewProj, model, text_.data() + c.off, c.x, c.y, c.a, col);
            continue;
        }
        glUseProgram(prog_);
        glBindVertexArray(vao_);
        glEnable(GL_BLEND);
        glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
        glDisable(GL_DEPTH_TEST);
        glDisable(GL_CULL_FACE);
        glDepthMask(GL_FALSE);
        glUniformMatrix4fv(uVP_, 1, GL_FALSE, eye.viewProj.m);
        glUniformMatrix4fv(uModel_, 1, GL_FALSE, model.m);
        glUniform2f(uCenter_, c.x, c.y);
        glUniform2f(uHalf_, c.a, c.b);
        glUniform2f(uQuad_, 2.f * (c.a + c.glow), 2.f * (c.b + c.glow));
        glUniform1f(uRadius_, c.radius);
        glUniform1f(uBorder_, c.border);
        glUniform1f(uGlow_, c.glow);
        glUniform1f(uHeader_, c.header);
        glUniform4f(uFill_, c.c1.r, c.c1.g, c.c1.b, c.c1.a);
        glUniform4f(uEdge_, c.c2.r, c.c2.g, c.c2.b, c.c2.a);
        glUniform4f(uGlowCol_, c.c3.r, c.c3.g, c.c3.b, c.c3.a);
        glUniform4f(uHeadCol_, c.c4.r, c.c4.g, c.c4.b, c.c4.a);
        glUniform1f(uAlpha_, alpha);
        glDrawArrays(GL_TRIANGLES, 0, 6);
    }
    if (blend) glEnable(GL_BLEND); else glDisable(GL_BLEND);
    if (depth) glEnable(GL_DEPTH_TEST); else glDisable(GL_DEPTH_TEST);
    if (cull) glEnable(GL_CULL_FACE); else glDisable(GL_CULL_FACE);
    glDepthMask(mask);
    glUseProgram((GLuint)prog);
    glBindVertexArray((GLuint)vao);
}

} // namespace mcx
