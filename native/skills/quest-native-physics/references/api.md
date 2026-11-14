# mcx_physics.h API, name by name

Header `mcx_physics.h` (includes `mcx_xr.h`; no GL). Namespace `mcx::phys`. Units: metres, kilograms, seconds, radians.
Y up, right-handed. The world is in the app's LOCAL space.

## Maths

- `Vec3`: `x`, `y`, `z`; `Vec3(x, y, z)`; `+` and `-` with a `Vec3`; `*` and `/` by a float; unary `-`; `+=`, `-=`, `*=`.
- `operator*(float, Vec3)`: scale from the left.
- `Dot(a, b)`, `Cross(a, b)`, `Length(v)`: the usual vector maths.
- `Normalized(v, fallback)`: unit vector; `fallback` (default +Y) for a zero vector.
- `Finite(v)`: true when all three components are finite.
- `Clamp(v, lo, hi)`: `v` limited to `[lo, hi]`.
- `Quat`: `x`, `y`, `z`, `w` (OpenXR order); identity by default; `Quat(x, y, z, w)`.
- `operator*(Quat, Quat)`: `a * b` turns by `b`, then by `a`.
- `Conj(q)`: conjugate (the inverse of a unit quaternion).
- `Normalized(q)`: unit quaternion; identity for a zero one.
- `QuatRotate(q, v)`: rotates `v` by `q`.
- `FromRotVec(r)`: the rotation of `|r|` radians about `r`.
- `ToRotVec(q)`: the rotation vector of `q`, the short way round.
- `Integrate(q, w, h)`: `q` turned by angular velocity `w` (rad/s) for `h` seconds.
- `NLerp(a, b, t)`: normalised blend of two orientations.
- `FromXr(XrVector3f)`, `FromXr(XrQuaternionf)`: OpenXR types to `Vec3` and `Quat`.
- `ToXr(Vec3)`, `ToXr(Quat)`: the reverse.
- `ToXrPose(position, rotation)`: an `XrPosef`.

## Bodies and shapes

- `BodyType`: enum class: `Static`, `Kinematic` (the app moves it), `Dynamic` (the solver moves it).
- `ShapeType`: enum class: `Sphere`, `Box`, `Plane`.
- `BodyId`: a handle: `index`, `gen`, `Valid()`, `==`, `!=`, `kNone`. Valid while its body lives.
- `ShapeInfo`: `type`; `radius` (sphere); `halfExtents` (box, half of each side); `normal` and `offset` (plane,
  `dot(normal, p) == offset` in the body's frame); `grabbable`.
- `BodyDesc`: the recipe for `World::Add`. Fields and defaults: `type` Dynamic; `shape` Sphere; `position`; `rotation`
  identity; `radius` 0.05 m; `halfExtents` 0.05 m; `normal` +Y; `offset` 0; `density` 600 kg/m^3; `mass` 0 (more than
  0 overrides `density`); `friction` 0.6; `restitution` 0.1; `linearDamping` 0.05 /s; `angularDamping` 0.1 /s;
  `gravityScale` 1; `grabbable` true; `user` 0 (free for the app).
- `BodyDesc::Sphere(position, radius, type)`: a sphere (default Dynamic).
- `BodyDesc::Box(position, halfExtents, type, rotation)`: an oriented box (default Dynamic, identity rotation).
- `BodyDesc::Plane(normal, offset)`: an infinite static plane; the free side is `normal`.
- `RayHit`: `hit`, `body`, `distance`, `point`, `normal`: the result of `World::Raycast`.

## World

- `World::Config` (`World::cfg`): `fixedDt` 1/90 s; `maxSteps` 4; `maxFrameDt` 0.1 s; `subSteps` 3; `iterations` 16;
  `maxBodies` 256; `gravity` (0, -9.81, 0) m/s^2; `slop` 2 mm; `baumgarte` 0.2; `maxCorrectSpeed` 1 m/s;
  `speculative` 4 mm; `restitutionThreshold` 0.5 m/s; `maxLinearSpeed` 40 m/s; `maxAngularSpeed` 60 rad/s;
  `allowSleep` true; `sleepLinear` 0.02 m/s; `sleepAngular` 0.05 rad/s; `sleepTime` 0.5 s.
- `World()`, `World(const Config&)`: an empty world.
- `World::Add(const BodyDesc&)`: a new body; an invalid `BodyId` when the world is full.
- `World::Remove(BodyId)`: removes a body; its handle goes stale.
- `World::Clear()`: removes every body; keeps `cfg`.
- `World::Alive(BodyId)`: true while the handle's body exists.
- `World::Count()`: live bodies.
- `World::AwakeCount()`: dynamic bodies that are not asleep.
- `World::ContactCount()`: contacts found in the last step.
- `World::Step(float dt)`: adds `dt` (at most `maxFrameDt`), runs up to `maxSteps` fixed steps; returns the steps run.
- `World::Alpha()`: the part of a step left over, 0 to 0.999; used by `RenderPose`.
- `World::GetPose(BodyId, Vec3& pos, Quat& rot)`: the pose after the last fixed step; false for a dead handle.
- `World::RenderPose(BodyId)`: the pose to draw, interpolated with `Alpha()`, as `XrPosef`.
- `World::Velocity(BodyId)`: linear velocity (m/s); zero for a dead handle.
- `World::AngularVelocity(BodyId)`: angular velocity (rad/s); zero for a dead handle.
- `World::SetPose(BodyId, pos, rot)`: teleports (no interpolation, velocity kept); wakes sleepers when a static or kinematic body moves.
- `World::SetVelocity(BodyId, linear, angular)`: sets both velocities; wakes a dynamic body. `angular` defaults to zero.
- `World::ApplyImpulse(BodyId, impulse, point)`: a world-space impulse at a world-space point; dynamic bodies only.
- `World::Type(BodyId)`: the body's `BodyType`.
- `World::SetType(BodyId, BodyType)`: changes the type; to Static it also stops the body.
- `World::SetKinematicTarget(BodyId, pos, rot)`: makes the body kinematic; it moves to the target in the next pass, never into a static body.
- `World::ClearKinematicTarget(BodyId)`: the kinematic body keeps its last velocity instead of a target.
- `World::Sleeping(BodyId)`: true when a dynamic body is asleep.
- `World::Wake(BodyId)`: wakes a dynamic body.
- `World::Shape(BodyId)`: the body's `ShapeInfo`.
- `World::Grabbable(BodyId)`: true for a dynamic body with `grabbable` set.
- `World::User(BodyId)`: the `user` value from `BodyDesc`.
- `World::Raycast(origin, dir, maxDist, RayHit& hit, ignore)`: the nearest body the ray hits (planes too); false when none. A ray that starts inside a body hits nothing.
- `World::Pick(point, radius)`: the nearest grabbable dynamic body within `radius` of its surface; invalid when none.

## Grabber (one held body, pinch or aim)

- `Grabber::Config` (`Grabber::cfg`): `radius` 0.04 m (pinch reach); `maxAimDistance` 4 m; `throwWindow` 0.1 s;
  `maxThrowSpeed` 8 m/s; `maxThrowSpin` 15 rad/s; `pulseTime` 0.25 s.
- `Grabber::hovered`: the body under the pinch or the aim ray (highlight it); invalid when none.
- `Grabber::held`: the body in the hand; invalid when none.
- `Grabber::pulse`: 1 at the grab, falling to 0 over `pulseTime`.
- `Grabber::throwVelocity`, `Grabber::throwSpin`: the last release, in m/s and rad/s.
- `Grabber::Holding()`: true while a body is held.
- `Grabber::UpdateHand(World&, const mcx::Hand&, double now)`: pinch grab and follow; pinch point = midpoint of thumb and index tips; the palm gives the orientation. Once per frame.
- `Grabber::UpdateAim(World&, const mcx::Hand&, double now)`: grabs with the aim ray (needs `hand.aimValid`).
- `Grabber::UpdateAt(World&, point, rot, tracked, pinchStarted, pinching, now)`: the same with any point and orientation (a tracker or a controller).
- `Grabber::Drop(World&, double now)`: releases now, with the throw velocity.
- `Grabber::Reset(World&)`: releases now, at rest.

## Internals (not for apps)

- `detail::Manifold`, `detail::Collide`, `detail::SphereSphere`, `detail::SphereBox`, `detail::SpherePlane`,
  `detail::BoxPlane`, `detail::BoxBox`, `detail::ClipPolygon`, `detail::Push`, `detail::BoxAxes`: the contact
  geometry that `World::Step` uses. Do not call them from an app.
- The solver and the broad phase are private `World` members (`SubStep`, `BuildContacts`, `SolveContact`,
  `UpdateSleep`, and so on).
