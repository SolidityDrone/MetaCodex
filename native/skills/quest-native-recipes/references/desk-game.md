# A game on the user's desk: breakout on the real table

A whole app that compiles (the skills gate joins the blocks of this file and compiles them with the NDK). It shows
the path for "a game on my desk": the Neon arcade look (quest-native-art-direction), the board on the real TABLE
from the room (recipe 3), the paddle under the hand (quest-native-hands-first), bricks drawn with instancing, a
ball with a trail, a halo and a contact shadow, sparks on each hit, the bricks growing in, a start panel, a HUD and
a result panel (quest-native-ui-design). Labels: **ours** (compiled, not yet seen on the headset).

Manifest: the nightvision example's entries for passthrough, hands and USE_SCENE (`cfg.scene = true`); remove the
controller and anchor entries (quest-native-hands-first). Split it into the files of quest-native-quality
section 6 when you adapt it; the constants go into `config.h`.

What the user sees: the room, a panel "Desk breakout" with Start. Pinch Start: a dark board appears on the table
0.45 m in front, its neon walls and four rows of glowing bricks grow in. The paddle follows the index fingertip of
the right hand (or the left). Pinch to serve. Each hit gives a burst of sparks. Three balls; then the score and
"Play again". No table in the room: the board stands at a seated desk height, and the panel says why.

```cpp
#include <mcx_gfx.h>
#include <mcx_ui.h>
#include <algorithm>
#include <cmath>
#include <cstdio>

// config.h: metres, seconds, sRGB colours of the Neon arcade look
constexpr float kAhead = 0.45f;                     // the board's centre, this far ahead of the head
constexpr float kBoardW = 0.50f, kBoardD = 0.36f;  // the field: width (x) and depth (z)
constexpr float kTop = 0.008f;                      // the board's top above the table
constexpr float kBall = 0.012f;                     // the ball's radius
constexpr float kSpeed = 0.32f;                     // the ball's speed, metres per second
constexpr float kPaddleW = 0.09f, kPaddleZ = 0.14f; // the paddle's width; its line, towards the user
constexpr float kBrickW = 0.052f, kBrickH = 0.018f, kBrickD = 0.024f, kGap = 0.006f;
constexpr int kCols = 8, kRows = 4, kLives = 3;
static const unsigned int kRowColour[kRows] = {0xFF4FD8, 0xD94BC9, 0x35D0FF, 0x2AA6D6}; // two neon families

struct Brick
{
    float x = 0.f;
    float z = 0.f;
    int row = 0;
    bool alive = false;
};

enum class Mode { Start, Play, Result };

// the located TABLE nearest to `spot` (horizontally), within 1.2 m; nullptr without one
const mcx::SceneObject* NearestTable(const mcx::App& app, XrVector3f spot)
{
    const mcx::SceneObject* best = nullptr;
    float bestD = 1.2f;
    for (const mcx::SceneObject& o : app.spatial.scene)
    {
        if (!o.located || o.labels.find("TABLE") == std::string::npos) continue;
        const float dx = o.pose.position.x - spot.x, dz = o.pose.position.z - spot.z;
        const float d = std::sqrt(dx * dx + dz * dz);
        if (d < bestD)
        {
            bestD = d;
            best = &o;
        }
    }
    return best;
}
```

The game: board space has its origin on the table top at the board's centre, +X to the user's right, -Z away
from the user, +Y up. The ball moves in sub-steps of half its radius, so it never jumps through a brick.

```cpp
struct DeskBreakout
{
    mcx::Lit lit;
    mcx::ContactShadow shadow;
    mcx::Mesh slab, wallSide, wallFar, brick, paddle, ball;
    mcx::Instances rows[kRows];        // one per brick colour: one draw call each
    mcx::Material slabMat, wallMat, paddleMat, ballMat, rowMat[kRows];
    mcx::Sprites glow;                 // the ball's halo and trail, the sparks: cleared each frame
    mcx::Particles sparks;
    mcx::Trail trail;
    mcx::Ui ui;
    mcx::UiPanel panel, hud;
    XrVector3f accent{0.f, 0.f, 0.f}, rowLight[kRows] = {};
    XrVector3f origin{0.f, 0.f, 0.f};  // the board's centre on the table top (world)
    XrQuaternionf turn{0.f, 0.f, 0.f, 1.f};
    XrVector3f pos{0.f, 0.f, 0.f};     // the ball (board space)
    XrVector3f vel{0.f, 0.f, 0.f};
    Brick bricks[kRows * kCols];
    Mode mode = Mode::Start;
    float paddleX = 0.f;
    float paddleTarget = 0.f;
    int score = 0;
    int lives = kLives;
    int left = 0;
    bool asked = false;
    bool placed = false;
    bool served = false;
    bool panelPlaced = false;
    double startedAt = 0.0;
    char title[32] = "Desk breakout";
    char line[64] = "Steer with your hand. Pinch to serve.";
    char hudText[48] = "";

    bool Init(mcx::App& app)
    {
        lit.light.keyColor = {0.85f, 0.88f, 0.95f}; // Neon arcade: a cool key, low ambient
        lit.light.sky = {0.20f, 0.18f, 0.28f};
        lit.light.ground = {0.06f, 0.05f, 0.08f};
        accent = mcx::GfxSrgbRgb(0xFF4FD8);
        slabMat = mcx::Material::Matte(0x1C1830);
        slabMat.roughness = 0.4f;
        slabMat.rim = 0.5f;
        wallMat = mcx::Material::Glow(0x35D0FF, 0.6f); // cyan only on the thin walls
        paddleMat = mcx::Material::Glow(0xFF4FD8, 0.9f);
        ballMat = mcx::Material::Glow(0xFFE6F8, 1.f);
        for (int r = 0; r < kRows; r++)
        {
            rowMat[r] = mcx::Material::Glow(kRowColour[r], 0.7f);
            rowLight[r] = mcx::GfxSrgbRgb(kRowColour[r]);
        }
        ui.theme.accent = 0xFF4FD8; // the look's UI accent; text and glass stay
        ui.theme.accentSoft = 0xB8379C;
        ui.theme.textAccent = 0xFFB3F0;
        sparks.gravity = {0.f, -0.8f, 0.f};
        if (!lit.Init() || !shadow.Init() || !ui.Init(app) || !glow.Init(256) || !sparks.Init(192) || !trail.Init(20, 0.01f))
            return false;
        if (!slab.Upload(mcx::MakeRoundedBox({kBoardW + 0.04f, kTop, kBoardD + 0.04f}, 0.004f, 2)) ||
            !wallSide.Upload(mcx::MakeBox({0.008f, 0.016f, kBoardD})) ||
            !wallFar.Upload(mcx::MakeBox({kBoardW + 0.016f, 0.016f, 0.008f})) ||
            !brick.Upload(mcx::MakeRoundedBox({kBrickW, kBrickH, kBrickD}, 0.004f, 2)) ||
            !paddle.Upload(mcx::MakeRoundedBox({kPaddleW, 0.014f, 0.016f}, 0.006f, 3)) ||
            !ball.Upload(mcx::MakeSphere(kBall, 24, 12)))
            return false;
        for (mcx::Instances& r : rows)
            if (!r.Init(brick, kCols)) return false;
        return true;
    }

    XrVector3f ToWorld(XrVector3f l) const { return mcx::Vec3Add(origin, mcx::Rotate(turn, l)); }
    XrVector3f ToBoard(XrVector3f w) const { return mcx::Rotate(mcx::QuatConjugate(turn), mcx::Vec3Sub(w, origin)); }
    mcx::Mat4 At(XrVector3f l, float s) const { return mcx::Trs(ToWorld(l), turn, {s, s, s}); }

    // at the first Start: the board in front of the user, on the table when the room has one
    void Place(const mcx::App& app, const mcx::Frame& f)
    {
        const XrVector3f head = mcx::Vec3Lerp(f.views[0].pose.position, f.views[1].pose.position, 0.5f);
        XrVector3f fwd = mcx::Forward(f.views[0].pose);
        fwd.y = 0.f;
        if (mcx::Vec3Dot(fwd, fwd) < 1e-4f) fwd = {0.f, 0.f, -1.f};
        fwd = mcx::Vec3Normalize(fwd);
        origin = {head.x + fwd.x * kAhead, head.y - 0.5f, head.z + fwd.z * kAhead}; // no table: a seated desk
        const mcx::SceneObject* table = NearestTable(app, origin);
        if (table) origin.y = table->pose.position.y;
        else mcx::Log("breakout: no TABLE in the room; the board stands at %.2f m", origin.y);
        turn = mcx::QuatLookAt(origin, mcx::Vec3Add(origin, fwd));
        placed = true;
    }

    void NewGame(double now)
    {
        for (int r = 0; r < kRows; r++)
            for (int c = 0; c < kCols; c++)
            {
                Brick& b = bricks[r * kCols + c];
                b.x = ((float)c - 0.5f * (float)(kCols - 1)) * (kBrickW + kGap);
                b.z = -0.5f * kBoardD + 0.04f + (float)r * (kBrickD + kGap);
                b.row = r;
                b.alive = true;
            }
        score = 0;
        lives = kLives;
        left = kRows * kCols;
        served = false;
        paddleX = paddleTarget = 0.f;
        trail.Reset();
        startedAt = now;
        mode = Mode::Play;
    }

    void Finish(const char* why)
    {
        std::snprintf(title, sizeof title, "Score %d", score);
        std::snprintf(line, sizeof line, "%s", why);
        mode = Mode::Result;
        served = false;
    }

    // one sub-step of the ball: walls, paddle, at most one brick
    void Step(float dt)
    {
        pos.x += vel.x * dt;
        pos.z += vel.z * dt;
        const float side = 0.5f * kBoardW - kBall, back = -0.5f * kBoardD + kBall;
        if (pos.x < -side) { pos.x = -side; vel.x = std::fabs(vel.x); }
        if (pos.x > side) { pos.x = side; vel.x = -std::fabs(vel.x); }
        if (pos.z < back) { pos.z = back; vel.z = std::fabs(vel.z); }
        if (vel.z > 0.f && std::fabs(pos.z - kPaddleZ) < kBall && std::fabs(pos.x - paddleX) < 0.5f * kPaddleW + kBall)
        {
            const float a = std::max(-1.f, std::min(1.f, (pos.x - paddleX) / (0.5f * kPaddleW))); // up to 57 degrees
            vel = {kSpeed * std::sin(a), 0.f, -kSpeed * std::cos(a)};
            pos.z = kPaddleZ - kBall;
        }
        for (Brick& b : bricks)
        {
            if (!b.alive) continue;
            const float dx = pos.x - b.x, dz = pos.z - b.z;
            const float px = 0.5f * kBrickW + kBall - std::fabs(dx), pz = 0.5f * kBrickD + kBall - std::fabs(dz);
            if (px <= 0.f || pz <= 0.f) continue;
            b.alive = false;
            left--;
            score += 10 * (kRows - b.row);
            if (px < pz) vel.x = dx < 0.f ? -std::fabs(vel.x) : std::fabs(vel.x);
            else vel.z = dz < 0.f ? -std::fabs(vel.z) : std::fabs(vel.z);
            sparks.Burst(ToWorld({b.x, kTop + 0.5f * kBrickH, b.z}), 24, 0.7f, rowLight[b.row], 0.016f, 0.55f);
            break;
        }
    }

    void Play(mcx::App& app, const mcx::Frame& f)
    {
        const mcx::Hand& right = app.hands.hand[1];
        const mcx::Hand& leftHand = app.hands.hand[0];
        const mcx::Hand* h = right.tracked ? &right : (leftHand.tracked ? &leftHand : nullptr);
        if (h) // the paddle follows the index fingertip; an untracked hand leaves it where it is
        {
            const float limit = 0.5f * (kBoardW - kPaddleW);
            paddleTarget = std::max(-limit, std::min(limit, ToBoard(h->Joint(XR_HAND_JOINT_INDEX_TIP_EXT).position).x));
        }
        paddleX = mcx::Damp(paddleX, paddleTarget, 25.f, f.dt);
        if (!served)
        {
            pos = {paddleX, 0.f, kPaddleZ - 2.f * kBall};
            if (right.pinchStarted || leftHand.pinchStarted)
            {
                served = true;
                vel = {0.35f * kSpeed, 0.f, -0.94f * kSpeed};
            }
            return;
        }
        const int steps = 1 + (int)(kSpeed * f.dt / (0.5f * kBall));
        for (int s = 0; s < steps; s++) Step(f.dt / (float)steps);
        if (pos.z > 0.5f * kBoardD) // missed
        {
            served = false;
            trail.Reset();
            if (--lives <= 0) Finish("Out of balls. Pinch Play again.");
        }
        if (left == 0) Finish("Board cleared!");
    }

    // once per frame, after BeginFrame
    void Update(mcx::App& app, const mcx::Frame& f)
    {
        if (!asked && app.spatial.sceneAvailable && app.HasPermission(mcx::kPermScene)) asked = app.spatial.QueryScene();
        ui.Begin(app, f);
        if (mode != Mode::Play)
        {
            if (!panelPlaced) { panel.PlaceInFront(f, 1.0f, 0.25f); panelPlaced = true; }
            panel.title = title;
            panel.width = 0.44f;
            panel.height = 0.27f;
            ui.Open(panel);
            ui.Label(line);
            ui.Space(0.012f);
            const bool go = ui.Button(mode == Mode::Start ? "Start" : "Play again", mcx::UiStyle::Primary);
            ui.Close();
            if (go)
            {
                if (!placed) Place(app, f);
                NewGame(f.seconds);
                panelPlaced = false;
            }
        }
        else if (f.focused) // false while a system menu is open: the game waits
            Play(app, f);
        if (placed && mode == Mode::Play) // the HUD: above the far edge, facing the user, never a pointer target
        {
            hud.input = false;
            hud.pose.position = ToWorld({0.f, 0.10f, -0.5f * kBoardD - 0.05f});
            hud.pose.orientation = turn;
            hud.scale = 0.7f; // about 0.7 m from the eyes: text keeps its angle
            hud.width = 0.30f * hud.scale;
            hud.height = 0.075f * hud.scale;
            std::snprintf(hudText, sizeof hudText, "Score %d   Balls %d", score, lives);
            ui.Open(hud);
            ui.Label(hudText);
            ui.Close();
        }
        // the bricks (grow in one by one) and the glow set, once per frame
        for (mcx::Instances& r : rows) r.Clear();
        for (int i = 0; placed && i < kRows * kCols; i++)
        {
            const Brick& b = bricks[i];
            const float s = mcx::EaseOutBack(mcx::Progress(f.seconds, startedAt + 0.2 + 0.02 * i, 0.35f), 1.4f);
            if (b.alive && s > 0.02f) rows[b.row].Add(At({b.x, kTop + 0.5f * kBrickH, b.z}, s));
        }
        sparks.Update(f.dt);
        glow.Clear();
        sparks.AddTo(glow);
        if (mode == Mode::Play)
        {
            const XrVector3f b = ToWorld({pos.x, kTop + kBall, pos.z});
            trail.Push(b);
            trail.AddTo(glow, 2.4f * kBall, mcx::Vec3Scale(accent, 0.45f));
            glow.Add(b, 6.f * kBall, mcx::Vec3Scale(accent, 0.5f));
        }
    }

    // one eye: the room, the board, the shadows, the glow, the UI
    void Draw(const mcx::App& app, const mcx::Frame& f, const mcx::Eye& eye)
    {
        glClearColor(0.f, 0.f, 0.f, app.clearAlpha());
        glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        if (placed)
        {
            const float grow = mcx::EaseOut(mcx::Progress(f.seconds, startedAt, 0.35f));
            const XrVector3f ballAt{pos.x, kTop + kBall, pos.z};
            lit.Begin(eye);
            lit.Draw(slab, At({0.f, 0.5f * kTop, 0.f}, grow), slabMat);
            lit.Draw(wallSide, At({-0.5f * kBoardW - 0.004f, kTop + 0.008f, 0.f}, grow), wallMat);
            lit.Draw(wallSide, At({0.5f * kBoardW + 0.004f, kTop + 0.008f, 0.f}, grow), wallMat);
            lit.Draw(wallFar, At({0.f, kTop + 0.008f, -0.5f * kBoardD - 0.004f}, grow), wallMat);
            for (int r = 0; r < kRows; r++) lit.DrawInstanced(rows[r], rowMat[r]);
            if (mode == Mode::Play)
            {
                lit.Draw(paddle, At({paddleX, kTop + 0.007f, kPaddleZ + kBall + 0.008f}, grow), paddleMat);
                lit.Draw(ball, At(ballAt, 1.f), ballMat);
            }
            lit.End();
            shadow.Draw(eye, origin, {kBoardW, kTop, kBoardD}, origin.y, 0.3f * grow); // grounds the board
            if (mode == Mode::Play)
                shadow.Draw(eye, ToWorld(ballAt), {2.f * kBall, 2.f * kBall, 2.f * kBall}, origin.y + kTop, 0.55f);
        }
        glow.Draw(eye);
        ui.Draw(eye);
    }

    void End() { ui.End(); }
    void Destroy()
    {
        ui.Destroy();
        glow.Destroy();
        for (mcx::Instances& r : rows) r.Destroy();
        slab.Destroy();
        wallSide.Destroy();
        wallFar.Destroy();
        brick.Destroy();
        paddle.Destroy();
        ball.Destroy();
        shadow.Destroy();
        lit.Destroy();
    }
};

void android_main(android_app* android)
{
    mcx::Config cfg;
    cfg.name = "Desk breakout";
    cfg.passthrough = true; // the room and the real table stay visible
    cfg.scene = true;       // the TABLE (USE_SCENE in the manifest)
    cfg.hands = true;
    cfg.controllers = false;
    cfg.foveation = 2;
    cfg.cpuLevel = 2;
    cfg.gpuLevel = 2;
    mcx::App app;
    if (!app.Init(android, cfg)) { while (app.Pump()) {} return; }
    DeskBreakout game;
    if (!game.Init(app)) { app.Shutdown(); return; }
    while (app.Pump())
    {
        mcx::Frame f;
        if (!app.BeginFrame(f)) continue;
        game.Update(app, f);
        for (int e = 0; e < 2; e++)
        {
            mcx::Eye eye;
            app.BeginEye(e, eye);
            game.Draw(app, f, eye);
            app.EndEye(e);
        }
        game.End();
        app.EndFrame();
    }
    game.Destroy();
    app.Shutdown();
}
```

Budget (ours, counted): about 12 draw calls for the board (4 brick rows, walls, paddle, ball, 2 shadows), 1 for the
glow, the UI; under 10,000 triangles. Hands: the paddle follows the index fingertip of the right hand (the left when
the right is not seen); serve: pinch with either hand; Start and Play again: pinch or touch the button.

What to tell the user after `mcx-app launch`: "ready: tap LAUNCH in MetaCodex. Pinch Start; the board appears on
your table; steer with your right hand and pinch to serve."
