---
name: quest-native-physics
description: Physics for native Quest apps made in MetaCodex (mcx_physics.h) - objects that fall, bounce, roll, stack or topple, grabbing and throwing objects with the hands (pinch or aim ray), and collisions with the room (the floor from the stage, tables and walls from the scene). Use when the user asks for physics, gravity, a ball, something dropped, thrown, stacked, knocked over or bouncing, grabbing and throwing with the hands, or objects that must rest on or hit the floor, a table or the walls. Also decides when plain animation is enough.
---

# Physics in native apps (mcx_physics.h)

Read first: quest-native-apps (the flow), quest-native-hands-first (mandatory: the hands are the input), and
quest-native-quality (the passes and the self-review). Budgets: quest-native-performance. Every name: `references/api.md`.
Numbers below were measured on the PC with the host tests, not on the headset.

## 1. Physics or animation?

| The user should see | Use |
|---|---|
| A motion you can write as a formula: a spin, a bob, a sliding panel | Animation (a function of time). No kit. |
| Objects that fall, bounce, roll, stack or topple, and that the user can knock over | Physics. |
| Objects the user picks up and throws | Physics and `phys::Grabber`. |

Use physics only where the result cannot be scripted. Keep the body count low (section 5).

## 2. The fixed-step loop

- `phys::World` steps at 1/90 s (`world.cfg.fixedDt`). Each frame, `Step(f.dt)` runs 0 to 4 steps (`world.cfg.maxSteps`).
- Draw with `RenderPose(id)`. It blends the last two steps by `Alpha()`. `GetPose` gives the last step only:
  drawn as it is, the motion judders.
- A frame longer than 0.1 s counts as 0.1 s (`world.cfg.maxFrameDt`). The scene slows down instead of jumping.
- In mixed reality draw no floor: the real floor shows through passthrough. Under a body that rests on the floor,
  draw an `mcx::ContactShadow` (mcx_gfx.h) so it looks grounded.

The code below is the whole loop: the scene, the grab and the drawing.

```cpp
#include <mcx_gfx.h>
#include <mcx_physics.h>
#include <vector>

namespace phys = mcx::phys;

// One scene: a floor, a few bodies, one hand grab. Lives for the whole session.
struct Scene
{
    phys::World world;                 // 1/90 s fixed step
    phys::Grabber grab;                // pinch to grab, release to throw
    std::vector<phys::BodyId> bodies;  // the dynamic bodies, for drawing
    mcx::Lit lit;
    mcx::Mesh box, sphere;             // unit size; each body scales its copy
};

// Height of the floor in LOCAL space: the stage origin, seen from LOCAL.
float FloorInLocal(const mcx::App& app, XrTime time)
{
    XrSpaceLocation loc{XR_TYPE_SPACE_LOCATION};
    if (app.stage == XR_NULL_HANDLE || XR_FAILED(xrLocateSpace(app.stage, app.local, time, &loc)) ||
        !(loc.locationFlags & XR_SPACE_LOCATION_POSITION_VALID_BIT))
        return -1.5f; // no boundary: a guess, check it on the headset
    return loc.pose.position.y;
}

// Once, from the loop on the first frame: it needs the frame time.
bool InitScene(Scene& s, float floorY)
{
    s.world.Add(phys::BodyDesc::Plane({0.f, 1.f, 0.f}, floorY));
    for (int i = 0; i < 4; i++) // a tower of 4 boxes, 40 cm ahead, touching
        s.bodies.push_back(s.world.Add(phys::BodyDesc::Box({0.f, floorY + 0.05f + 0.1f * i, -0.4f}, {0.05f, 0.05f, 0.05f})));
    phys::BodyDesc ball = phys::BodyDesc::Sphere({0.3f, floorY + 0.6f, -0.4f}, 0.05f); // its centre starts 60 cm up
    ball.linearDamping = ball.angularDamping = 1.f; // the kit has no rolling resistance: a ball needs damping
    s.bodies.push_back(s.world.Add(ball));
    s.box.Upload(mcx::MakeBox({1.f, 1.f, 1.f}));
    s.sphere.Upload(mcx::MakeSphere(1.f, 16, 8));
    return s.lit.Init();
}

// Once per frame, between BeginFrame and EndFrame.
void UpdateScene(Scene& s, mcx::App& app, const mcx::Frame& f)
{
    s.world.Step(f.dt);                                       // 0 to 4 fixed steps of 1/90 s
    s.grab.UpdateHand(s.world, app.hands.hand[1], f.seconds); // right hand: pinch to grab, release to throw
}

// Per eye, between BeginEye and EndEye: each body at its interpolated pose.
void DrawScene(Scene& s, const mcx::Eye& eye)
{
    s.lit.Begin(eye);
    for (phys::BodyId id : s.bodies)
    {
        const phys::ShapeInfo shape = s.world.Shape(id);
        const mcx::Mat4 pose = mcx::Mat4::FromPose(s.world.RenderPose(id));
        const bool held = id == s.grab.held;
        const float grow = held ? 1.f + 0.08f * s.grab.pulse : 1.f; // the pulse at the grab
        const uint32_t tint = (held || id == s.grab.hovered) ? 0x35D0FFu : 0x9DBDD2u; // accent when the pinch is near
        if (shape.type == phys::ShapeType::Sphere)
        {
            const float r = grow * shape.radius;
            s.lit.Draw(s.sphere, pose * mcx::Mat4::Scale(r, r, r), mcx::Material::Matte(tint));
        }
        else
        {
            const phys::Vec3 h = shape.halfExtents * grow;
            s.lit.Draw(s.box, pose * mcx::Mat4::Scale(2.f * h.x, 2.f * h.y, 2.f * h.z), mcx::Material::Matte(tint));
        }
    }
    s.lit.End();
}

// The app loop, as in the nightvision example, with the scene in it.
void RunScene(mcx::App& app, Scene& s)
{
    bool started = false;
    while (app.Pump())
    {
        mcx::Frame f;
        if (!app.BeginFrame(f)) continue;
        if (!started) { InitScene(s, FloorInLocal(app, f.time)); started = true; }
        UpdateScene(s, app, f);
        for (int e = 0; e < 2; e++)
        {
            mcx::Eye eye;
            app.BeginEye(e, eye);
            glClearColor(0.f, 0.f, 0.f, app.clearAlpha()); // alpha 0 where the room should show
            glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
            DrawScene(s, eye);
            app.EndEye(e);
        }
        app.EndFrame();
    }
    app.Shutdown();
}
```

## 3. Grab and throw with the hands

- Call `grab.UpdateHand(world, app.hands.hand[1], f.seconds)` once per frame (the loop uses the right hand).
  The pinch point is the midpoint of the thumb and index tips. A new pinch grabs the body within 4 cm of it.
- Feedback (hands have no haptics): tint `grab.hovered` (the loop uses the accent `0x35D0FF`). Scale `grab.held` by
  `1 + 0.08 * grab.pulse`. `pulse` goes from 1 at the grab to 0 in 0.25 s.
- A throw uses the hand's mean velocity over the last 0.1 s, at most 8 m/s (`throwVelocity`). Measured: a hand
  moving at (1.2, 1.0, 0) m/s released the ball at that velocity (error 0.00 %).
- `UpdateAim` grabs what the aim ray hits (use it with `hand.aimValid`). `Drop` releases with the throw velocity.
  `Reset` releases at rest.
- Do not move a held body yourself. While held it is kinematic: it pushes the bodies it touches and stops at static ones.

## 4. The room as colliders

- The world lives in LOCAL space, where the hands are. The floor is the stage origin: `FloorInLocal` (in the loop)
  reads its height. With no stage (no boundary) it returns -1.5 m, a guess. Check it on the headset.
- Tables and walls need `cfg.scene = true` and USE_SCENE (quest-native-xr). When `app.HasPermission(mcx::kPermScene)`
  is true, call `app.spatial.QueryScene()` once. When `sceneDone` is true, call `AddRoom` once, after `BeginFrame`
  (the poses are located then). It adds static boxes for the TABLE volumes and the WALL_FACE rectangles (4 cm thick).
- Check the placement first: drop a ball on a table, and it must stop on the top. The frame of the scene boxes is
  an assumption (the corner of the 3D box, the XY plane of a 2D rectangle). Check it, and tell the user what you saw.

`AddRoom` uses `phys` from the loop code above.

```cpp
#include <string>

// Static boxes for the room: the TABLE volumes and the WALL_FACE rectangles of app.spatial.scene.
void AddRoom(const mcx::App& app, phys::World& world, std::vector<phys::BodyId>& out)
{
    for (const mcx::SceneObject& o : app.spatial.scene)
    {
        if (!o.located) continue;
        const phys::Quat rot = phys::FromXr(o.pose.orientation);
        phys::Vec3 centre, half;
        if (o.hasVolume && o.labels.find("TABLE") != std::string::npos)
        {
            // the 3D box: its corner is the offset (assumed: check it on the headset)
            const XrOffset3DfFB& off = o.volume.offset;
            const XrExtent3DfFB& ext = o.volume.extent;
            centre = phys::Vec3(off.x + 0.5f * ext.width, off.y + 0.5f * ext.height, off.z + 0.5f * ext.depth);
            half = phys::Vec3(0.5f * ext.width, 0.5f * ext.height, 0.5f * ext.depth);
        }
        else if (o.hasPlane && o.labels.find("WALL_FACE") != std::string::npos)
        {
            // the rectangle in the wall's XY plane, as a 4 cm slab centred on it
            const XrRect2Df& r = o.plane;
            centre = phys::Vec3(r.offset.x + 0.5f * r.extent.width, r.offset.y + 0.5f * r.extent.height, 0.f);
            half = phys::Vec3(0.5f * r.extent.width, 0.5f * r.extent.height, 0.02f);
        }
        else
            continue;
        const phys::Vec3 at = phys::FromXr(o.pose.position) + phys::QuatRotate(rot, centre);
        out.push_back(world.Add(phys::BodyDesc::Box(at, half, phys::BodyType::Static, rot)));
    }
}
```

## 5. Budgets

- Physics: 2 ms of CPU per frame (quest-native-performance). A step does not allocate (measured with 100 bodies).
- The fixed step is 1/90 s, so the kit runs 90 steps a second at any frame rate. A 72 Hz frame runs 1 or 2 steps
  (1.25 on average). At 0.28 ms per step on the PC, that is about 25 ms of CPU a second, 2.5 % of one core.
- Measured on the PC (clang -O2): 100 awake boxes 0.28 ms per step; 250 spheres 0.21 to 0.28 ms; 100 sleeping
  bodies 0.02 ms per step. The headset CPU is slower than this PC: measure it with `FrameStats`
  (quest-native-performance, section 6).
- Cap: 256 bodies (`world.cfg.maxBodies`). `Add` returns an invalid id when the world is full. Remove what you no longer need.

## 6. Stability rules (measured on the PC)

| Rule | Measured |
|---|---|
| Sizes from 5 cm to 2 m | A 2 m slab (4 cm thick) stays flat. A 5 cm ball rests on it at the right height. |
| At most 30 times the mass of the body underneath (1:30) | 1:30: dip 1.2 mm, asleep after 0.6 s. 1:100: dip 1 cm, not asleep after 10 s. |
| Keep 3 sub-steps (the default) | A 10-box tower with 1 sub-step collapses. With 3 it sags 18 mm (about 2 mm per contact: the kit's slop). |
| Move dynamic bodies with forces, velocities or kinematic targets | `SetPose` teleports: a body can start inside another and is pushed out at up to 1 m/s. |
| Fast small bodies | A 3 cm ball does not pass a 1 cm wall at 5, 20 or 40 m/s. At 40 m/s its centre never comes closer than 22 cm to the wall. |

Balls roll on for too long, because the kit has no rolling resistance. A 5 cm ball at 1 m/s on an open floor,
with the default damping (0.05 and 0.1 per second): 0.28 m/s after 20 s, not asleep after 30 s. With
`linearDamping` and `angularDamping` at 1.0: it rolls 1.0 m in all and sleeps after 6.5 s. Set 1.0 on balls that
must stop. With that damping a ball rolls about as many metres as its speed in m/s.

## 7. What the kit cannot do

- Rolling resistance: balls roll on (section 6). Damping is the only brake.
- Shapes: sphere, oriented box and infinite plane. No meshes, capsules, cylinders or convex hulls.
- No joints (hinges, springs, ropes), no triggers, no collision events, no collision layers. Check distances and
  poses yourself.
- No continuous collision: fast bodies rely on speculative contacts (section 6).
- Box on box: good for stacks and towers (measured). An edge-on-edge contact is one point, so a box balanced on an
  edge is approximate. Mass ratios above 1:30 jitter.
- Kinematic bodies push dynamic bodies and never enter static ones. They are not solved against each other.

## 8. Test physics before the headset

1. Write a host test first. Build the scene, step 10 s, and assert that nothing goes below the floor and that
   everything sleeps. Build it with `clang++ -std=c++17 -O2`, a stub `mcx_xr.h` that holds the `mcx::Hand` struct,
   and the OpenXR headers. The kit's tests run in under a second.
2. Feel it fast in the Runner (quest-native-dev-loop). A module app gets the hands through `mcx::Hands`, not `mcx::App`.
3. Then quest-native-quality: a start state, a reset, grounded objects and no debug text.
