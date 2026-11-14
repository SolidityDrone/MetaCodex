// mcx_physics.h: a small rigid-body kit for native Quest apps built in MetaCodex. Header only, no GL, no exceptions.
//   phys::World    fixed step (1/90 s) with an accumulator, sub-steps, gravity, render interpolation, sleep
//   bodies         dynamic; kinematic (the app moves it: it pushes dynamic bodies and never enters a static one);
//                  static. Shapes: sphere, oriented box, infinite plane (static)
//   contacts       sphere-sphere, sphere-box, sphere-plane, box-plane, box-box (separating axes + clipping)
//   solver         sequential impulses, warm starting, restitution, Coulomb friction, Baumgarte, damping, islands
//   queries        Raycast, Pick (the nearest grabbable body near a point)
//   phys::Grabber  pinch or aim-ray grab of a body with mcx::Hand; a throw gives the hand's mean velocity of 100 ms
// Units: metres, kilograms, seconds, radians. Y up, right-handed. The world frame is the app's LOCAL space (where the
// hands and the eyes are). The floor is the stage's y = 0, expressed in LOCAL (quest-native-physics).
//
//   phys::World world;                                      // 1/90 s fixed step, gravity -9.81 m/s^2
//   world.Add(phys::BodyDesc::Plane({0.f, 1.f, 0.f}, floorY));
//   phys::BodyId cube = world.Add(phys::BodyDesc::Box({0.f, 1.f, -1.f}, {0.05f, 0.05f, 0.05f}));
//   world.Step(f.dt);                                       // in each frame: 0..maxSteps fixed steps
//   XrPosef pose = world.RenderPose(cube);                  // interpolated for drawing
#pragma once
#include <mcx_xr.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

namespace mcx {
namespace phys {

// ---------------------------------------------------------------- maths
struct Vec3
{
    float x = 0.f, y = 0.f, z = 0.f;
    Vec3() = default;
    constexpr Vec3(float a, float b, float c) : x(a), y(b), z(c) {}
    Vec3 operator+(const Vec3& o) const { return {x + o.x, y + o.y, z + o.z}; }
    Vec3 operator-(const Vec3& o) const { return {x - o.x, y - o.y, z - o.z}; }
    Vec3 operator-() const { return {-x, -y, -z}; }
    Vec3 operator*(float s) const { return {x * s, y * s, z * s}; }
    Vec3 operator/(float s) const { return {x / s, y / s, z / s}; }
    Vec3& operator+=(const Vec3& o) { x += o.x; y += o.y; z += o.z; return *this; }
    Vec3& operator-=(const Vec3& o) { x -= o.x; y -= o.y; z -= o.z; return *this; }
    Vec3& operator*=(float s) { x *= s; y *= s; z *= s; return *this; }
};
inline Vec3 operator*(float s, const Vec3& v) { return v * s; }
inline float Dot(const Vec3& a, const Vec3& b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
inline Vec3 Cross(const Vec3& a, const Vec3& b)
{
    return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
}
inline float Length(const Vec3& v) { return std::sqrt(Dot(v, v)); }
inline Vec3 Normalized(const Vec3& v, const Vec3& fallback = Vec3(0.f, 1.f, 0.f))
{
    float l = Length(v);
    return l > 1e-12f ? v / l : fallback;
}
inline bool Finite(const Vec3& v) { return std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z); }
inline float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

// unit quaternion, (x, y, z) vector part and w scalar part (OpenXR order)
struct Quat
{
    float x = 0.f, y = 0.f, z = 0.f, w = 1.f;
    Quat() = default;
    constexpr Quat(float a, float b, float c, float d) : x(a), y(b), z(c), w(d) {}
};
inline Quat operator*(const Quat& a, const Quat& b) // a * b: turn by b, then by a
{
    return {a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z};
}
inline Quat Conj(const Quat& q) { return {-q.x, -q.y, -q.z, q.w}; }
inline Quat Normalized(const Quat& q)
{
    float l = std::sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
    return l > 1e-12f ? Quat(q.x / l, q.y / l, q.z / l, q.w / l) : Quat();
}
inline Vec3 QuatRotate(const Quat& q, const Vec3& v)
{
    Vec3 u(q.x, q.y, q.z);
    Vec3 t = 2.f * Cross(u, v);
    return v + t * q.w + Cross(u, t);
}
// the rotation of |r| radians about the axis r
inline Quat FromRotVec(const Vec3& r)
{
    float a = Length(r);
    if (a < 1e-8f) return Normalized(Quat(0.5f * r.x, 0.5f * r.y, 0.5f * r.z, 1.f));
    float s = std::sin(0.5f * a) / a;
    return Quat(s * r.x, s * r.y, s * r.z, std::cos(0.5f * a));
}
// the rotation vector (axis * angle) of a rotation, the short way round
inline Vec3 ToRotVec(const Quat& q)
{
    float sgn = q.w < 0.f ? -1.f : 1.f;
    Vec3 v(sgn * q.x, sgn * q.y, sgn * q.z);
    float s = Length(v);
    if (s < 1e-8f) return v * 2.f;
    return v * (2.f * std::atan2(s, sgn * q.w) / s);
}
// q turned by w (rad/s) for h seconds
inline Quat Integrate(const Quat& q, const Vec3& w, float h) { return Normalized(FromRotVec(w * h) * q); }
inline Quat NLerp(const Quat& a, const Quat& b, float t)
{
    float s = (a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w) < 0.f ? -1.f : 1.f;
    return Normalized(Quat(a.x + (s * b.x - a.x) * t, a.y + (s * b.y - a.y) * t, a.z + (s * b.z - a.z) * t,
                           a.w + (s * b.w - a.w) * t));
}
inline Vec3 FromXr(const XrVector3f& v) { return Vec3(v.x, v.y, v.z); }
inline Quat FromXr(const XrQuaternionf& q) { return Quat(q.x, q.y, q.z, q.w); }
inline XrVector3f ToXr(const Vec3& v) { return XrVector3f{v.x, v.y, v.z}; }
inline XrQuaternionf ToXr(const Quat& q) { return XrQuaternionf{q.x, q.y, q.z, q.w}; }
inline XrPosef ToXrPose(const Vec3& p, const Quat& q)
{
    XrPosef r;
    r.orientation = ToXr(q);
    r.position = ToXr(p);
    return r;
}

// ---------------------------------------------------------------- bodies and shapes
enum class BodyType : uint8_t { Static, Kinematic, Dynamic };
enum class ShapeType : uint8_t { Sphere, Box, Plane };

// a body handle: stays valid while the body lives; a removed body's handle is no longer Alive()
struct BodyId
{
    static constexpr uint32_t kNone = 0xFFFFFFFFu;
    uint32_t index = kNone; // slot in the world
    uint32_t gen = 0;       // slot generation
    bool Valid() const { return index != kNone; }
    bool operator==(const BodyId& o) const { return index == o.index && gen == o.gen; }
    bool operator!=(const BodyId& o) const { return !(*this == o); }
};

struct ShapeInfo
{
    ShapeType type = ShapeType::Sphere;
    float radius = 0.f;           // sphere
    Vec3 halfExtents;             // box: half of each side
    Vec3 normal;                  // plane: unit normal, the free side (body frame)
    float offset = 0.f;           // plane: the surface is dot(R * normal, p - position) == offset
    bool grabbable = false;       // the flag from BodyDesc (World::Grabbable also checks the body is dynamic)
};

struct BodyDesc
{
    BodyType type = BodyType::Dynamic;
    ShapeType shape = ShapeType::Sphere;
    Vec3 position;
    Quat rotation;
    float radius = 0.05f;                          // sphere radius (m)
    Vec3 halfExtents = Vec3(0.05f, 0.05f, 0.05f);  // box: half of each side (m)
    Vec3 normal = Vec3(0.f, 1.f, 0.f);             // plane: the free side, in the body's frame (position, rotation)
    float offset = 0.f;                            // plane: dot(normal, p) == offset
    float density = 600.f;                         // kg/m^3 for a dynamic body (used when mass is 0)
    float mass = 0.f;                              // kg; > 0 overrides density
    float friction = 0.6f;
    float restitution = 0.1f;
    float linearDamping = 0.05f;                   // 1/s
    float angularDamping = 0.1f;                   // 1/s
    float gravityScale = 1.f;
    bool grabbable = true;                         // a hand may pick it (dynamic bodies only)
    uint32_t user = 0;                             // free for the app
    static BodyDesc Sphere(const Vec3& p, float r, BodyType t = BodyType::Dynamic)
    {
        BodyDesc d;
        d.type = t;
        d.shape = ShapeType::Sphere;
        d.position = p;
        d.radius = r;
        return d;
    }
    static BodyDesc Box(const Vec3& p, const Vec3& half, BodyType t = BodyType::Dynamic, const Quat& q = Quat())
    {
        BodyDesc d;
        d.type = t;
        d.shape = ShapeType::Box;
        d.position = p;
        d.rotation = q;
        d.halfExtents = half;
        return d;
    }
    static BodyDesc Plane(const Vec3& n, float offset)
    {
        BodyDesc d;
        d.type = BodyType::Static;
        d.shape = ShapeType::Plane;
        d.normal = Normalized(n);
        d.offset = offset;
        d.grabbable = false;
        return d;
    }
};

struct RayHit
{
    bool hit = false;
    BodyId body;
    float distance = 0.f;
    Vec3 point, normal;
};

// ---------------------------------------------------------------- contact geometry (no state)
namespace detail {

struct Manifold
{
    int count = 0;
    Vec3 normal;                 // from the first body to the second
    float depth[4] = {0.f, 0.f, 0.f, 0.f};
    Vec3 point[4];
};

inline void Push(Manifold& m, const Vec3& n, const Vec3& p, float depth)
{
    m.normal = n;
    if (m.count < 4)
    {
        m.point[m.count] = p;
        m.depth[m.count] = depth;
        m.count++;
        return;
    }
    int k = 0; // keep the 4 deepest points
    for (int i = 1; i < 4; i++)
        if (m.depth[i] < m.depth[k]) k = i;
    if (depth > m.depth[k])
    {
        m.point[k] = p;
        m.depth[k] = depth;
    }
}

inline void BoxAxes(const Quat& q, Vec3 ax[3])
{
    ax[0] = QuatRotate(q, Vec3(1.f, 0.f, 0.f));
    ax[1] = QuatRotate(q, Vec3(0.f, 1.f, 0.f));
    ax[2] = QuatRotate(q, Vec3(0.f, 0.f, 1.f));
}

// margin: contacts are reported this far (m) before the surfaces touch (speculative contacts)
inline bool SphereSphere(const Vec3& ca, float ra, const Vec3& cb, float rb, float margin, Manifold& m)
{
    Vec3 d = cb - ca;
    float dist = Length(d);
    float sum = ra + rb;
    if (dist > sum + margin) return false;
    Vec3 n = dist > 1e-6f ? d / dist : Vec3(0.f, 1.f, 0.f);
    float depth = sum - dist;
    Push(m, n, ca + n * (ra - 0.5f * depth), depth);
    return true;
}

// sphere against box: the normal points from the box to the sphere
inline bool SphereBox(const Vec3& c, float r, const Vec3& bc, const Quat& bq, const Vec3& h, float margin, Manifold& m)
{
    Vec3 local = QuatRotate(Conj(bq), c - bc);
    Vec3 q(Clamp(local.x, -h.x, h.x), Clamp(local.y, -h.y, h.y), Clamp(local.z, -h.z, h.z));
    Vec3 d = local - q;
    float dist2 = Dot(d, d);
    Vec3 nl, pl;
    float depth;
    if (dist2 > 1e-12f)
    {
        float dist = std::sqrt(dist2);
        if (dist > r + margin) return false;
        nl = d / dist;
        pl = q;
        depth = r - dist;
    }
    else // the centre is inside the box: leave through the nearest face
    {
        float fx = h.x - std::fabs(local.x), fy = h.y - std::fabs(local.y), fz = h.z - std::fabs(local.z);
        if (fx <= fy && fx <= fz)
        {
            float s = local.x < 0.f ? -1.f : 1.f;
            nl = Vec3(s, 0.f, 0.f);
            pl = Vec3(s * h.x, local.y, local.z);
            depth = r + fx;
        }
        else if (fy <= fz)
        {
            float s = local.y < 0.f ? -1.f : 1.f;
            nl = Vec3(0.f, s, 0.f);
            pl = Vec3(local.x, s * h.y, local.z);
            depth = r + fy;
        }
        else
        {
            float s = local.z < 0.f ? -1.f : 1.f;
            nl = Vec3(0.f, 0.f, s);
            pl = Vec3(local.x, local.y, s * h.z);
            depth = r + fz;
        }
    }
    Vec3 n = QuatRotate(bq, nl);
    Push(m, n, bc + QuatRotate(bq, pl) - n * (0.5f * depth), depth);
    return true;
}

// sphere against an infinite plane: the normal points from the plane to the sphere
inline bool SpherePlane(const Vec3& c, float r, const Vec3& pn, float pd, float margin, Manifold& m)
{
    float s = Dot(pn, c) - pd; // height of the centre above the plane
    if (s > r + margin) return false;
    Push(m, pn, c - pn * (0.5f * (r + s)), r - s);
    return true;
}

// box corners against an infinite plane: the normal points from the plane to the box
inline bool BoxPlane(const Vec3& bc, const Quat& bq, const Vec3& h, const Vec3& pn, float pd, float margin, Manifold& m)
{
    Vec3 ax[3];
    BoxAxes(bq, ax);
    bool any = false;
    for (int k = 0; k < 8; k++)
    {
        Vec3 corner = bc + ax[0] * ((k & 1) ? h.x : -h.x) + ax[1] * ((k & 2) ? h.y : -h.y) +
                      ax[2] * ((k & 4) ? h.z : -h.z);
        float s = Dot(pn, corner) - pd;
        if (s > margin) continue;
        float depth = -s;
        Push(m, pn, corner + pn * (0.5f * depth), depth);
        any = true;
    }
    return any;
}

// keeps the part of a convex polygon with dot(p, nrm) <= off; returns the new vertex count
inline int ClipPolygon(const Vec3* in, int n, const Vec3& nrm, float off, Vec3* out)
{
    int m = 0;
    for (int i = 0; i < n; i++)
    {
        const Vec3& a = in[i];
        const Vec3& b = in[(i + 1) % n];
        float da = Dot(nrm, a) - off, db = Dot(nrm, b) - off;
        if (da <= 0.f && m < 31) out[m++] = a;
        if (((da < 0.f && db > 0.f) || (da > 0.f && db < 0.f)) && m < 31) out[m++] = a + (b - a) * (da / (da - db));
    }
    return m;
}

// box against box, separating axes (3 + 3 faces, 9 edges). A face contact clips the incident face against the
// reference face (up to 4 points); an edge contact is one point at the closest points of the two edges.
inline bool BoxBox(const Vec3& ca, const Quat& qa, const Vec3& ha, const Vec3& cb, const Quat& qb, const Vec3& hb,
                   float margin, Manifold& m)
{
    Vec3 A[3], B[3];
    BoxAxes(qa, A);
    BoxAxes(qb, B);
    const float hA[3] = {ha.x, ha.y, ha.z}, hB[3] = {hb.x, hb.y, hb.z};
    const Vec3 d = cb - ca;
    float faceOver = 1e30f, edgeOver = 1e30f;
    int faceAxis = -1, edgeA = -1, edgeB = -1;
    Vec3 faceN, edgeN;
    for (int ax = 0; ax < 15; ax++)
    {
        Vec3 L;
        if (ax < 3) L = A[ax];
        else if (ax < 6) L = B[ax - 3];
        else
        {
            L = Cross(A[(ax - 6) / 3], B[(ax - 6) % 3]);
            float l = Length(L);
            if (l < 1e-4f) continue; // parallel edges: the face axes decide
            L = L / l;
        }
        float ra = 0.f, rb = 0.f;
        for (int i = 0; i < 3; i++)
        {
            ra += hA[i] * std::fabs(Dot(L, A[i]));
            rb += hB[i] * std::fabs(Dot(L, B[i]));
        }
        float dist = Dot(d, L);
        float over = ra + rb - std::fabs(dist); // penetration along L (negative: a gap)
        if (over < -margin) return false;       // a separating axis
        Vec3 n = dist < 0.f ? -L : L;           // from A to B
        if (ax < 6)
        {
            if (over < faceOver)
            {
                faceOver = over;
                faceAxis = ax;
                faceN = n;
            }
        }
        else if (over < edgeOver)
        {
            edgeOver = over;
            edgeA = (ax - 6) / 3;
            edgeB = (ax - 6) % 3;
            edgeN = n;
        }
    }
    if (faceAxis < 0) return false;
    // faces win unless an edge axis is clearly better (stacks must not rock on an edge)
    bool useEdge = edgeA >= 0 && edgeOver < 0.9f * faceOver - 0.002f;
    if (!useEdge)
    {
        const bool refA = faceAxis < 3;
        const Vec3* R = refA ? A : B;
        const float* hR = refA ? hA : hB;
        const Vec3* I = refA ? B : A;
        const float* hI = refA ? hB : hA;
        const Vec3 cR = refA ? ca : cb, cI = refA ? cb : ca;
        const int r = faceAxis % 3;
        const Vec3 refN = refA ? faceN : -faceN; // outward normal of the reference face
        // the incident face: the face of I that points most against refN
        int ii = 0;
        float bestDot = -1.f;
        for (int k = 0; k < 3; k++)
        {
            float dk = std::fabs(Dot(refN, I[k]));
            if (dk > bestDot)
            {
                bestDot = dk;
                ii = k;
            }
        }
        const Vec3 iN = Dot(refN, I[ii]) > 0.f ? -I[ii] : I[ii];
        const Vec3 fi = cI + iN * hI[ii];
        const int j = (ii + 1) % 3, k2 = (ii + 2) % 3;
        Vec3 poly[32], tmp[32];
        poly[0] = fi + I[j] * hI[j] + I[k2] * hI[k2];
        poly[1] = fi - I[j] * hI[j] + I[k2] * hI[k2];
        poly[2] = fi - I[j] * hI[j] - I[k2] * hI[k2];
        poly[3] = fi + I[j] * hI[j] - I[k2] * hI[k2];
        int n = 4;
        for (int s = 1; s <= 2; s++) // clip against the two side planes of the reference face
        {
            const int u = (r + s) % 3;
            const float cu = Dot(cR, R[u]);
            int c1 = ClipPolygon(poly, n, R[u], cu + hR[u], tmp);
            for (int q = 0; q < c1; q++) poly[q] = tmp[q];
            n = ClipPolygon(poly, c1, -R[u], hR[u] - cu, tmp);
            for (int q = 0; q < n; q++) poly[q] = tmp[q];
        }
        const Vec3 fc = cR + refN * hR[r]; // centre of the reference face
        const Vec3 nAB = refA ? refN : -refN;
        m.count = 0;
        for (int q = 0; q < n; q++)
        {
            float sep = Dot(poly[q] - fc, refN); // > 0: outside the reference face
            if (sep > margin) continue;
            float depth = -sep;
            Push(m, nAB, poly[q] + refN * (0.5f * depth), depth);
        }
        return m.count > 0;
    }
    // edge against edge: the support point of each edge that faces the other box, then the closest points
    const Vec3 L = edgeN;
    Vec3 pa = ca, pb = cb;
    for (int k = 0; k < 3; k++)
    {
        if (k != edgeA) pa += A[k] * (Dot(L, A[k]) < 0.f ? -hA[k] : hA[k]);
        if (k != edgeB) pb += B[k] * (Dot(L, B[k]) > 0.f ? -hB[k] : hB[k]);
    }
    const Vec3 u = A[edgeA], v = B[edgeB], r0 = pa - pb;
    const float b = Dot(u, v), c = Dot(u, r0), f = Dot(v, r0);
    const float denom = 1.f - b * b;
    float s = std::fabs(denom) > 1e-6f ? (b * f - c) / denom : 0.f;
    s = Clamp(s, -hA[edgeA], hA[edgeA]);
    float t = Clamp(f + s * b, -hB[edgeB], hB[edgeB]);
    const Vec3 onA = pa + u * s, onB = pb + v * t; // the closest points of the two edges
    m.count = 0;
    Push(m, L, (onA + onB) * 0.5f, edgeOver);
    return true;
}

// the contact between two shapes at the given poses; the normal points from a to b
inline bool Collide(const ShapeInfo& a, const Vec3& pa, const Quat& qa, const ShapeInfo& b, const Vec3& pb,
                    const Quat& qb, float margin, Manifold& m)
{
    m.count = 0;
    if (a.type == ShapeType::Plane && b.type == ShapeType::Plane) return false;
    if (a.type == ShapeType::Plane)
    {
        Vec3 pn = QuatRotate(qa, a.normal);
        float pd = Dot(pn, pa) + a.offset;
        return b.type == ShapeType::Sphere ? SpherePlane(pb, b.radius, pn, pd, margin, m)
                                           : BoxPlane(pb, qb, b.halfExtents, pn, pd, margin, m);
    }
    if (b.type == ShapeType::Plane)
    {
        Vec3 pn = QuatRotate(qb, b.normal);
        float pd = Dot(pn, pb) + b.offset;
        bool hit = a.type == ShapeType::Sphere ? SpherePlane(pa, a.radius, pn, pd, margin, m)
                                               : BoxPlane(pa, qa, a.halfExtents, pn, pd, margin, m);
        if (hit) m.normal = -m.normal;
        return hit;
    }
    if (a.type == ShapeType::Sphere && b.type == ShapeType::Sphere)
        return SphereSphere(pa, a.radius, pb, b.radius, margin, m);
    if (a.type == ShapeType::Sphere)
    {
        bool hit = SphereBox(pa, a.radius, pb, qb, b.halfExtents, margin, m);
        if (hit) m.normal = -m.normal;
        return hit;
    }
    if (b.type == ShapeType::Sphere) return SphereBox(pb, b.radius, pa, qa, a.halfExtents, margin, m);
    return BoxBox(pa, qa, a.halfExtents, pb, qb, b.halfExtents, margin, m);
}

} // namespace detail

// ---------------------------------------------------------------- the world
class World
{
public:
    struct Config
    {
        float fixedDt = 1.f / 90.f;        // s per fixed step
        int maxSteps = 4;                  // catch-up cap: at most this many fixed steps per Step() call
        float maxFrameDt = 0.1f;           // s: a longer frame counts as this long (the rest is dropped)
        int subSteps = 3;                  // collision + solver passes per fixed step (more: stiffer stacks)
        int iterations = 16;               // sequential impulse iterations per pass (8 leaves stacks jittering)
        int maxBodies = 256;               // fixed pool: Add() fails (invalid BodyId) when it is full
        Vec3 gravity{0.f, -9.81f, 0.f};    // m/s^2
        float slop = 0.002f;               // m: penetration that is allowed without correction
        float baumgarte = 0.2f;            // share of the penetration removed per pass
        float maxCorrectSpeed = 1.f;       // m/s: cap of the correction speed
        float speculative = 0.004f;        // m: contacts are solved this far before touching
        float restitutionThreshold = 0.5f; // m/s: slower impacts do not bounce
        float maxLinearSpeed = 40.f;       // m/s
        float maxAngularSpeed = 60.f;      // rad/s
        bool allowSleep = true;
        float sleepLinear = 0.02f;         // m/s: below this (and sleepAngular) a body is at rest
        float sleepAngular = 0.05f;        // rad/s
        float sleepTime = 0.5f;            // s at rest before a whole island sleeps
    };
    Config cfg;

    World() { Init(); }
    explicit World(const Config& c) : cfg(c) { Init(); }

    // ---- bodies
    BodyId Add(const BodyDesc& d);
    void Remove(BodyId id);
    void Clear() { Init(); }
    bool Alive(BodyId id) const
    {
        return id.index < bodies_.size() && bodies_[id.index].alive && bodies_[id.index].gen == id.gen;
    }
    int Count() const { return alive_; }
    int AwakeCount() const;
    int ContactCount() const { return int(contacts_.size()); }

    // ---- the fixed step: runs 0..maxSteps fixed steps for dt, keeps the rest for the next call
    int Step(float dt);
    float Alpha() const { return Clamp(acc_ / std::max(cfg.fixedDt, 1e-4f), 0.f, 0.999f); }

    // ---- state (GetPose and Velocity: the last fixed step; RenderPose: interpolated with Alpha)
    bool GetPose(BodyId id, Vec3& pos, Quat& rot) const;
    XrPosef RenderPose(BodyId id) const;
    Vec3 Velocity(BodyId id) const;
    Vec3 AngularVelocity(BodyId id) const;
    void SetPose(BodyId id, const Vec3& pos, const Quat& rot);                 // teleport: no interpolation
    void SetVelocity(BodyId id, const Vec3& linear, const Vec3& angular = Vec3());
    void ApplyImpulse(BodyId id, const Vec3& impulse, const Vec3& point);     // world-space point
    BodyType Type(BodyId id) const;
    void SetType(BodyId id, BodyType t);
    void SetKinematicTarget(BodyId id, const Vec3& pos, const Quat& rot);     // makes it kinematic
    void ClearKinematicTarget(BodyId id);
    bool Sleeping(BodyId id) const;
    void Wake(BodyId id);
    ShapeInfo Shape(BodyId id) const;
    bool Grabbable(BodyId id) const;
    uint32_t User(BodyId id) const;

    // ---- queries
    bool Raycast(const Vec3& origin, const Vec3& dir, float maxDist, RayHit& hit, BodyId ignore = BodyId()) const;
    BodyId Pick(const Vec3& point, float radius) const; // nearest grabbable dynamic body within radius of its surface

private:
    struct Body
    {
        ShapeInfo geom;
        BodyType type = BodyType::Static;
        bool alive = false, sleeping = false, hasTarget = false;
        uint32_t gen = 1, user = 0;
        Vec3 pos, prevPos, vel, angVel, targetPos;
        Quat rot, prevRot, targetRot;
        float mass = 0.f, dynInvMass = 0.f, invMass = 0.f; // invMass: 0 unless dynamic
        Vec3 dynInvInertia, invInertia;                    // body frame; 0 unless dynamic
        float friction = 0.6f, restitution = 0.1f, linDamp = 0.f, angDamp = 0.f, gravScale = 1.f;
        float sleepTime = 0.f;
    };
    struct Contact
    {
        uint32_t a = 0, b = 0, ga = 0, gb = 0; // body indices (a < b) and generations
        Vec3 n, p, t1, t2, ra, rb;
        float depth = 0.f, bias = 0.f, friction = 0.f;
        float nMass = 0.f, t1Mass = 0.f, t2Mass = 0.f;
        float lambdaN = 0.f, lambdaT1 = 0.f, lambdaT2 = 0.f;
    };
    struct Aabb
    {
        Vec3 lo, hi;
    };

    void Init();
    void WakeAll();
    Body* Get(BodyId id) { return Alive(id) ? &bodies_[id.index] : nullptr; }
    const Body* Get(BodyId id) const { return Alive(id) ? &bodies_[id.index] : nullptr; }
    bool Awake(const Body& b) const;
    static uint64_t Key(uint32_t a, uint32_t b) { return (uint64_t(a) << 32) | b; }
    static Vec3 ApplyInvInertia(const Body& b, const Vec3& v);
    static float DistanceTo(const Body& b, const Vec3& p);
    static bool RayBody(const Body& b, const Vec3& o, const Vec3& d, float maxT, float& t, Vec3& n);
    Vec3 RelVel(const Body& a, const Body& b, const Contact& c) const { return (b.vel + Cross(b.angVel, c.rb)) - (a.vel + Cross(a.angVel, c.ra)); }
    float EffMass(const Body& a, const Body& b, const Vec3& ra, const Vec3& rb, const Vec3& d) const;
    void ApplyContactImpulse(Contact& c, const Vec3& P);
    void FixedStep(float h);
    void SubStep(float h);
    void PlanKinematic(uint32_t i, float h);
    void BuildContacts(float h);
    void Pair(uint32_t i, uint32_t j, float h);
    void MatchWarm(Contact& c) const;
    void PrepareContact(Contact& c, float h);
    void SolveContact(Contact& c);
    void UpdateSleep(float h);
    uint32_t Find(uint32_t x)
    {
        while (parent_[x] != x) { parent_[x] = parent_[parent_[x]]; x = parent_[x]; }
        return x;
    }

    std::vector<Body> bodies_;
    std::vector<uint32_t> free_, order_, planes_, parent_;
    std::vector<Aabb> aabb_;
    std::vector<float> minRest_;
    std::vector<Contact> contacts_, prev_;
    int alive_ = 0;
    float acc_ = 0.f;
};

inline void World::Init()
{
    const uint32_t n = uint32_t(std::max(1, cfg.maxBodies));
    bodies_.assign(n, Body());
    free_.clear();
    free_.reserve(n);
    for (uint32_t i = n; i-- > 0;) free_.push_back(i);
    order_.clear();
    order_.reserve(n);
    planes_.clear();
    planes_.reserve(n);
    aabb_.assign(n, Aabb());
    parent_.assign(n, 0u);
    minRest_.assign(n, 0.f);
    contacts_.clear();
    contacts_.reserve(4u * n);
    prev_.clear();
    prev_.reserve(4u * n);
    alive_ = 0;
    acc_ = 0.f;
}

inline BodyId World::Add(const BodyDesc& d)
{
    if (free_.empty()) return BodyId();
    const uint32_t i = free_.back();
    free_.pop_back();
    Body b;
    b.gen = bodies_[i].gen;
    b.alive = true;
    b.type = d.type;
    b.geom.type = d.shape;
    b.pos = b.prevPos = d.position;
    b.rot = b.prevRot = Normalized(d.rotation);
    b.friction = d.friction;
    b.restitution = d.restitution;
    b.linDamp = d.linearDamping;
    b.angDamp = d.angularDamping;
    b.gravScale = d.gravityScale;
    b.user = d.user;
    b.geom.grabbable = d.grabbable && d.shape != ShapeType::Plane;
    if (d.shape == ShapeType::Plane)
    {
        b.type = BodyType::Static;
        b.geom.normal = Normalized(d.normal);
        b.geom.offset = d.offset;
    }
    else
    {
        float volume, ix, iy, iz;
        if (d.shape == ShapeType::Sphere)
        {
            b.geom.radius = std::max(d.radius, 0.002f);
            const float r = b.geom.radius;
            volume = 4.18879f * r * r * r;
            b.mass = d.mass > 0.f ? d.mass : d.density * volume;
            b.mass = Clamp(b.mass, 0.001f, 100000.f);
            ix = iy = iz = 0.4f * b.mass * r * r; // solid sphere
        }
        else
        {
            const Vec3 h(std::max(d.halfExtents.x, 0.002f), std::max(d.halfExtents.y, 0.002f),
                         std::max(d.halfExtents.z, 0.002f));
            b.geom.halfExtents = h;
            volume = 8.f * h.x * h.y * h.z;
            b.mass = d.mass > 0.f ? d.mass : d.density * volume;
            b.mass = Clamp(b.mass, 0.001f, 100000.f);
            ix = b.mass * (h.y * h.y + h.z * h.z) / 3.f; // solid box
            iy = b.mass * (h.x * h.x + h.z * h.z) / 3.f;
            iz = b.mass * (h.x * h.x + h.y * h.y) / 3.f;
        }
        b.dynInvMass = 1.f / b.mass;
        b.dynInvInertia = Vec3(1.f / ix, 1.f / iy, 1.f / iz);
        b.invMass = b.type == BodyType::Dynamic ? b.dynInvMass : 0.f;
        b.invInertia = b.type == BodyType::Dynamic ? b.dynInvInertia : Vec3();
    }
    bodies_[i] = b;
    alive_++;
    BodyId id;
    id.index = i;
    id.gen = b.gen;
    return id;
}

inline void World::Remove(BodyId id)
{
    if (!Alive(id)) return;
    Body& b = bodies_[id.index];
    b.alive = false;
    b.gen++; // every handle to this slot is stale now
    free_.push_back(id.index);
    alive_--;
    WakeAll(); // a sleeping body may rest on the one that left
}

inline int World::AwakeCount() const
{
    int n = 0;
    for (const Body& b : bodies_)
        if (b.alive && b.type == BodyType::Dynamic && !b.sleeping) n++;
    return n;
}

inline int World::Step(float dt)
{
    if (!(dt > 0.f)) return 0;
    const float h = std::max(cfg.fixedDt, 1e-4f);
    acc_ += std::min(dt, cfg.maxFrameDt);
    int steps = 0;
    while (acc_ >= h && steps < cfg.maxSteps)
    {
        for (Body& b : bodies_)
        {
            if (!b.alive) continue;
            b.prevPos = b.pos; // the state before this step, for RenderPose
            b.prevRot = b.rot;
        }
        FixedStep(h);
        acc_ -= h;
        steps++;
    }
    if (acc_ >= h) acc_ = std::fmod(acc_, h); // past the cap: drop the backlog
    return steps;
}

inline bool World::GetPose(BodyId id, Vec3& pos, Quat& rot) const
{
    const Body* b = Get(id);
    if (!b) return false;
    pos = b->pos;
    rot = b->rot;
    return true;
}

inline XrPosef World::RenderPose(BodyId id) const
{
    const Body* b = Get(id);
    if (!b) return ToXrPose(Vec3(), Quat());
    float a = Alpha();
    return ToXrPose(b->prevPos + (b->pos - b->prevPos) * a, NLerp(b->prevRot, b->rot, a));
}

inline Vec3 World::Velocity(BodyId id) const
{
    const Body* b = Get(id);
    return b ? b->vel : Vec3();
}

inline Vec3 World::AngularVelocity(BodyId id) const
{
    const Body* b = Get(id);
    return b ? b->angVel : Vec3();
}

inline void World::SetPose(BodyId id, const Vec3& pos, const Quat& rot)
{
    Body* b = Get(id);
    if (!b) return;
    const Quat nr = Normalized(rot);
    const bool moved = pos.x != b->pos.x || pos.y != b->pos.y || pos.z != b->pos.z || nr.x != b->rot.x ||
                       nr.y != b->rot.y || nr.z != b->rot.z || nr.w != b->rot.w;
    b->pos = b->prevPos = pos;
    b->rot = b->prevRot = nr;
    if (b->type == BodyType::Dynamic)
    {
        b->sleeping = false;
        b->sleepTime = 0.f;
    }
    else if (moved)
        WakeAll(); // a static or kinematic body moved: sleepers may rest on it
}

inline void World::SetVelocity(BodyId id, const Vec3& linear, const Vec3& angular)
{
    Body* b = Get(id);
    if (!b) return;
    b->vel = linear;
    b->angVel = angular;
    if (b->type == BodyType::Dynamic)
    {
        b->sleeping = false;
        b->sleepTime = 0.f;
    }
}

inline void World::ApplyImpulse(BodyId id, const Vec3& impulse, const Vec3& point)
{
    Body* b = Get(id);
    if (!b || b->type != BodyType::Dynamic) return;
    b->vel += impulse * b->invMass;
    b->angVel += ApplyInvInertia(*b, Cross(point - b->pos, impulse));
    b->sleeping = false;
    b->sleepTime = 0.f;
}

inline BodyType World::Type(BodyId id) const
{
    const Body* b = Get(id);
    return b ? b->type : BodyType::Static;
}

inline void World::SetType(BodyId id, BodyType t)
{
    Body* b = Get(id);
    if (!b || b->geom.type == ShapeType::Plane || b->type == t) return;
    b->type = t;
    b->invMass = t == BodyType::Dynamic ? b->dynInvMass : 0.f;
    b->invInertia = t == BodyType::Dynamic ? b->dynInvInertia : Vec3();
    if (t != BodyType::Kinematic) b->hasTarget = false;
    if (t == BodyType::Static)
    {
        b->vel = Vec3();
        b->angVel = Vec3();
    }
    b->sleeping = false;
    b->sleepTime = 0.f;
    WakeAll();
}

inline void World::SetKinematicTarget(BodyId id, const Vec3& pos, const Quat& rot)
{
    Body* b = Get(id);
    if (!b) return;
    if (b->type != BodyType::Kinematic) SetType(id, BodyType::Kinematic);
    b->hasTarget = true;
    b->targetPos = pos;
    b->targetRot = Normalized(rot);
}

inline void World::ClearKinematicTarget(BodyId id)
{
    Body* b = Get(id);
    if (b) b->hasTarget = false;
}

inline bool World::Sleeping(BodyId id) const
{
    const Body* b = Get(id);
    return b && b->sleeping;
}

inline void World::Wake(BodyId id)
{
    Body* b = Get(id);
    if (b && b->type == BodyType::Dynamic)
    {
        b->sleeping = false;
        b->sleepTime = 0.f;
    }
}

inline ShapeInfo World::Shape(BodyId id) const
{
    const Body* b = Get(id);
    return b ? b->geom : ShapeInfo();
}

inline bool World::Grabbable(BodyId id) const
{
    const Body* b = Get(id);
    return b && b->type == BodyType::Dynamic && b->geom.grabbable;
}

inline uint32_t World::User(BodyId id) const
{
    const Body* b = Get(id);
    return b ? b->user : 0u;
}

inline void World::WakeAll()
{
    for (Body& b : bodies_)
        if (b.alive && b.sleeping)
        {
            b.sleeping = false;
            b.sleepTime = 0.f;
        }
}

inline bool World::Awake(const Body& b) const
{
    if (b.type == BodyType::Dynamic) return !b.sleeping;
    if (b.type == BodyType::Kinematic)
        return b.hasTarget || Dot(b.vel, b.vel) > cfg.sleepLinear * cfg.sleepLinear ||
               Dot(b.angVel, b.angVel) > cfg.sleepAngular * cfg.sleepAngular;
    return false;
}

inline Vec3 World::ApplyInvInertia(const Body& b, const Vec3& v)
{
    if (b.invMass == 0.f) return Vec3();
    Vec3 l = QuatRotate(Conj(b.rot), v);
    l.x *= b.invInertia.x;
    l.y *= b.invInertia.y;
    l.z *= b.invInertia.z;
    return QuatRotate(b.rot, l);
}

inline float World::EffMass(const Body& a, const Body& b, const Vec3& ra, const Vec3& rb, const Vec3& d) const
{
    Vec3 ca = Cross(ra, d), cb = Cross(rb, d);
    float k = a.invMass + b.invMass + Dot(ca, ApplyInvInertia(a, ca)) + Dot(cb, ApplyInvInertia(b, cb));
    return k > 1e-12f ? 1.f / k : 0.f;
}

// P: the impulse on b (a receives -P at the same point)
inline void World::ApplyContactImpulse(Contact& c, const Vec3& P)
{
    Body& a = bodies_[c.a];
    Body& b = bodies_[c.b];
    a.vel -= P * a.invMass;
    a.angVel -= ApplyInvInertia(a, Cross(c.ra, P));
    b.vel += P * b.invMass;
    b.angVel += ApplyInvInertia(b, Cross(c.rb, P));
}

inline float World::DistanceTo(const Body& b, const Vec3& p)
{
    if (b.geom.type == ShapeType::Sphere) return std::max(0.f, Length(p - b.pos) - b.geom.radius);
    Vec3 l = QuatRotate(Conj(b.rot), p - b.pos);
    const Vec3& h = b.geom.halfExtents;
    Vec3 q(Clamp(l.x, -h.x, h.x), Clamp(l.y, -h.y, h.y), Clamp(l.z, -h.z, h.z));
    return Length(l - q); // 0 inside the box
}

// the ray's hit distance t (0 <= t <= maxT) and the surface normal; origin inside a body: no hit
inline bool World::RayBody(const Body& b, const Vec3& o, const Vec3& d, float maxT, float& t, Vec3& n)
{
    if (b.geom.type == ShapeType::Plane)
    {
        Vec3 pn = QuatRotate(b.rot, b.geom.normal);
        float denom = Dot(pn, d);
        float s = Dot(pn, o) - (Dot(pn, b.pos) + b.geom.offset);
        if (denom > -1e-6f || s < 0.f) return false; // parallel, or the origin is on the back side
        t = -s / denom;
        n = pn;
        return t <= maxT;
    }
    if (b.geom.type == ShapeType::Sphere)
    {
        Vec3 oc = o - b.pos;
        float bq = Dot(oc, d);
        float c = Dot(oc, oc) - b.geom.radius * b.geom.radius;
        if (c < 0.f) return false;
        float disc = bq * bq - c;
        if (disc < 0.f) return false;
        float ts = -bq - std::sqrt(disc);
        if (ts < 0.f || ts > maxT) return false;
        t = ts;
        n = Normalized((o + d * t) - b.pos);
        return true;
    }
    Vec3 ol = QuatRotate(Conj(b.rot), o - b.pos), dl = QuatRotate(Conj(b.rot), d);
    const float oc[3] = {ol.x, ol.y, ol.z}, dc[3] = {dl.x, dl.y, dl.z};
    const float hc[3] = {b.geom.halfExtents.x, b.geom.halfExtents.y, b.geom.halfExtents.z};
    float t0 = 0.f, t1 = maxT;
    int axis = -1;
    float sign = 0.f;
    for (int i = 0; i < 3; i++) // slab test
    {
        if (std::fabs(dc[i]) < 1e-9f)
        {
            if (std::fabs(oc[i]) > hc[i]) return false;
            continue;
        }
        float inv = 1.f / dc[i];
        float ta = (-hc[i] - oc[i]) * inv, tb = (hc[i] - oc[i]) * inv;
        float enter = std::min(ta, tb), exit = std::max(ta, tb);
        if (enter > t0)
        {
            t0 = enter;
            axis = i;
            sign = inv > 0.f ? -1.f : 1.f;
        }
        if (exit < t1) t1 = exit;
        if (t0 > t1) return false;
    }
    if (axis < 0) return false; // the origin is inside the box
    t = t0;
    Vec3 nl;
    if (axis == 0) nl.x = sign;
    else if (axis == 1) nl.y = sign;
    else nl.z = sign;
    n = QuatRotate(b.rot, nl);
    return true;
}

inline bool World::Raycast(const Vec3& origin, const Vec3& dir, float maxDist, RayHit& hit, BodyId ignore) const
{
    hit = RayHit();
    const Vec3 d = Normalized(dir, Vec3(0.f, 0.f, -1.f));
    float best = maxDist;
    for (uint32_t i = 0; i < bodies_.size(); i++)
    {
        const Body& b = bodies_[i];
        if (!b.alive || (ignore.Valid() && ignore.index == i && ignore.gen == b.gen)) continue;
        float t = 0.f;
        Vec3 n;
        if (!RayBody(b, origin, d, best, t, n)) continue;
        best = t;
        hit.hit = true;
        hit.body.index = i;
        hit.body.gen = b.gen;
        hit.distance = t;
        hit.point = origin + d * t;
        hit.normal = n;
    }
    return hit.hit;
}

inline BodyId World::Pick(const Vec3& p, float radius) const
{
    BodyId best;
    float bestD = radius;
    for (uint32_t i = 0; i < bodies_.size(); i++)
    {
        const Body& b = bodies_[i];
        if (!b.alive || b.type != BodyType::Dynamic || !b.geom.grabbable) continue;
        float dist = DistanceTo(b, p);
        if (dist <= bestD)
        {
            bestD = dist;
            best.index = i;
            best.gen = b.gen;
        }
    }
    return best;
}

inline void World::FixedStep(float h)
{
    const int n = std::max(1, cfg.subSteps);
    for (int s = 0; s < n; s++) SubStep(h / float(n));
}

// a kinematic body reaches its target (or moves with its velocity) in this pass, but never into a static body
inline void World::PlanKinematic(uint32_t i, float h)
{
    Body& k = bodies_[i];
    Vec3 tp;
    Quat tq;
    if (k.hasTarget)
    {
        tp = k.targetPos;
        tq = k.targetRot;
    }
    else
    {
        tp = k.pos + k.vel * h;
        tq = Integrate(k.rot, k.angVel, h);
    }
    for (int pass = 0; pass < 2; pass++)
    {
        Vec3 push;
        bool any = false;
        for (uint32_t j = 0; j < bodies_.size(); j++)
        {
            const Body& s = bodies_[j];
            if (!s.alive || s.type != BodyType::Static) continue;
            detail::Manifold m;
            if (!detail::Collide(k.geom, tp, tq, s.geom, s.pos, s.rot, 0.f, m)) continue;
            float dmax = 0.f;
            for (int q = 0; q < m.count; q++) dmax = std::max(dmax, m.depth[q]);
            if (dmax <= 0.f) continue;
            push += -m.normal * dmax; // the normal points from the body to the static one
            any = true;
        }
        if (!any) break;
        tp += push;
    }
    k.vel = (tp - k.pos) / h;
    if (Length(k.vel) > cfg.maxLinearSpeed) k.vel = Normalized(k.vel) * cfg.maxLinearSpeed;
    k.angVel = ToRotVec(tq * Conj(k.rot)) / h;
}

inline void World::Pair(uint32_t i, uint32_t j, float h)
{
    Body& a = bodies_[i];
    Body& b = bodies_[j];
    if (a.type == BodyType::Static && b.type == BodyType::Static) return;
    if (a.type != BodyType::Dynamic && b.type != BodyType::Dynamic) return; // kinematic vs static: PlanKinematic
    if (!Awake(a) && !Awake(b)) return;
    detail::Manifold m;
    // a fast pair reports its contact early enough: the margin grows with the distance it closes in one pass
    const float reach = cfg.speculative + 1.2f * h * Length(b.vel - a.vel);
    if (!detail::Collide(a.geom, a.pos, a.rot, b.geom, b.pos, b.rot, reach, m)) return;
    // a sleeping body that touches something awake wakes up (sleeping bodies get no contacts)
    if (a.sleeping) { a.sleeping = false; a.sleepTime = 0.f; }
    if (b.sleeping) { b.sleeping = false; b.sleepTime = 0.f; }
    for (int k = 0; k < m.count; k++)
    {
        Contact c;
        c.a = i;
        c.b = j;
        c.ga = a.gen;
        c.gb = b.gen;
        c.n = m.normal;
        c.p = m.point[k];
        c.depth = m.depth[k];
        MatchWarm(c);
        contacts_.push_back(c);
    }
}

// the impulses of the same contact in the last pass (same bodies, a point within 3 cm)
inline void World::MatchWarm(Contact& c) const
{
    const uint64_t key = Key(c.a, c.b);
    auto it = std::lower_bound(prev_.begin(), prev_.end(), key,
                               [](const Contact& x, uint64_t k) { return Key(x.a, x.b) < k; });
    float best = 0.03f * 0.03f;
    for (; it != prev_.end() && Key(it->a, it->b) == key; ++it)
    {
        if (it->ga != c.ga || it->gb != c.gb) continue;
        Vec3 d = it->p - c.p;
        float d2 = Dot(d, d);
        if (d2 < best)
        {
            best = d2;
            c.lambdaN = it->lambdaN;
            c.lambdaT1 = it->lambdaT1;
            c.lambdaT2 = it->lambdaT2;
        }
    }
}

inline void World::BuildContacts(float h)
{
    contacts_.clear();
    order_.clear();
    planes_.clear();
    const float margin = cfg.speculative;
    for (uint32_t i = 0; i < bodies_.size(); i++)
    {
        const Body& b = bodies_[i];
        if (!b.alive) continue;
        if (b.geom.type == ShapeType::Plane)
        {
            planes_.push_back(i);
            continue;
        }
        Vec3 e;
        if (b.geom.type == ShapeType::Sphere)
            e = Vec3(b.geom.radius, b.geom.radius, b.geom.radius);
        else
        {
            Vec3 ax[3];
            detail::BoxAxes(b.rot, ax);
            const Vec3& hh = b.geom.halfExtents;
            e = Vec3(std::fabs(ax[0].x) * hh.x + std::fabs(ax[1].x) * hh.y + std::fabs(ax[2].x) * hh.z,
                     std::fabs(ax[0].y) * hh.x + std::fabs(ax[1].y) * hh.y + std::fabs(ax[2].y) * hh.z,
                     std::fabs(ax[0].z) * hh.x + std::fabs(ax[1].z) * hh.y + std::fabs(ax[2].z) * hh.z);
        }
        // grow by the margin and by how far the body moves in this pass (no tunnelling through thin boxes)
        float grow = margin + 2.f * h * (Length(b.vel) + Length(b.angVel) * Length(e));
        aabb_[i].lo = b.pos - e - Vec3(grow, grow, grow);
        aabb_[i].hi = b.pos + e + Vec3(grow, grow, grow);
        order_.push_back(i);
    }
    // sweep and prune along x (insertion sort: the order changes little between passes)
    for (size_t i = 1; i < order_.size(); i++)
    {
        uint32_t v = order_[i];
        size_t j = i;
        while (j > 0 && aabb_[order_[j - 1]].lo.x > aabb_[v].lo.x)
        {
            order_[j] = order_[j - 1];
            j--;
        }
        order_[j] = v;
    }
    for (size_t i = 0; i < order_.size(); i++)
    {
        const uint32_t ia = order_[i];
        const Aabb& A = aabb_[ia];
        for (size_t j = i + 1; j < order_.size(); j++)
        {
            const uint32_t ib = order_[j];
            const Aabb& B = aabb_[ib];
            if (B.lo.x > A.hi.x) break;
            if (B.lo.y > A.hi.y || A.lo.y > B.hi.y || B.lo.z > A.hi.z || A.lo.z > B.hi.z) continue;
            Pair(std::min(ia, ib), std::max(ia, ib), h);
        }
    }
    for (uint32_t ip : planes_)
        for (uint32_t ib : order_) Pair(std::min(ip, ib), std::max(ip, ib), h);
}

inline void World::PrepareContact(Contact& c, float h)
{
    Body& a = bodies_[c.a];
    Body& b = bodies_[c.b];
    c.ra = c.p - a.pos;
    c.rb = c.p - b.pos;
    const Vec3 helper = std::fabs(c.n.y) < 0.9f ? Vec3(0.f, 1.f, 0.f) : Vec3(1.f, 0.f, 0.f);
    c.t1 = Normalized(Cross(c.n, helper));
    c.t2 = Cross(c.n, c.t1);
    c.nMass = EffMass(a, b, c.ra, c.rb, c.n);
    c.t1Mass = EffMass(a, b, c.ra, c.rb, c.t1);
    c.t2Mass = EffMass(a, b, c.ra, c.rb, c.t2);
    c.friction = std::sqrt(a.friction * b.friction);
    const float e = std::max(a.restitution, b.restitution);
    // the bias: push out what overlaps, let a gap close in this pass only, bounce fast impacts
    float bias = 0.f;
    if (c.depth > cfg.slop) bias = std::min(cfg.baumgarte * (c.depth - cfg.slop) / h, cfg.maxCorrectSpeed);
    else if (c.depth < 0.f) bias = c.depth / h;
    const float vn0 = Dot(RelVel(a, b, c), c.n);
    if (vn0 < -cfg.restitutionThreshold) bias = std::max(bias, -e * vn0);
    c.bias = bias;
}

inline void World::SolveContact(Contact& c)
{
    Body& a = bodies_[c.a];
    Body& b = bodies_[c.b];
    float vn = Dot(RelVel(a, b, c), c.n);
    float dl = (c.bias - vn) * c.nMass;
    float old = c.lambdaN;
    c.lambdaN = std::max(old + dl, 0.f);
    dl = c.lambdaN - old;
    if (dl != 0.f) ApplyContactImpulse(c, c.n * dl);
    const float maxF = c.friction * c.lambdaN;
    float vt = Dot(RelVel(a, b, c), c.t1);
    old = c.lambdaT1;
    c.lambdaT1 = Clamp(old - vt * c.t1Mass, -maxF, maxF);
    dl = c.lambdaT1 - old;
    if (dl != 0.f) ApplyContactImpulse(c, c.t1 * dl);
    vt = Dot(RelVel(a, b, c), c.t2);
    old = c.lambdaT2;
    c.lambdaT2 = Clamp(old - vt * c.t2Mass, -maxF, maxF);
    dl = c.lambdaT2 - old;
    if (dl != 0.f) ApplyContactImpulse(c, c.t2 * dl);
}

inline void World::UpdateSleep(float h)
{
    const float lin2 = cfg.sleepLinear * cfg.sleepLinear, ang2 = cfg.sleepAngular * cfg.sleepAngular;
    for (uint32_t i = 0; i < bodies_.size(); i++)
    {
        Body& b = bodies_[i];
        parent_[i] = i;
        minRest_[i] = 1e30f;
        if (!b.alive || b.type != BodyType::Dynamic || b.sleeping) continue;
        bool still = Dot(b.vel, b.vel) < lin2 && Dot(b.angVel, b.angVel) < ang2;
        b.sleepTime = still ? b.sleepTime + h : 0.f;
    }
    // islands: dynamic bodies that touch each other (kinematic and static bodies do not join islands)
    for (const Contact& c : contacts_)
    {
        if (bodies_[c.a].type != BodyType::Dynamic || bodies_[c.b].type != BodyType::Dynamic) continue;
        uint32_t ra = Find(c.a), rb = Find(c.b);
        if (ra != rb) parent_[rb] = ra;
    }
    for (uint32_t i = 0; i < bodies_.size(); i++)
    {
        const Body& b = bodies_[i];
        if (!b.alive || b.type != BodyType::Dynamic || b.sleeping) continue;
        uint32_t r = Find(i);
        minRest_[r] = std::min(minRest_[r], b.sleepTime);
    }
    if (!cfg.allowSleep) return;
    for (uint32_t i = 0; i < bodies_.size(); i++)
    {
        Body& b = bodies_[i];
        if (!b.alive || b.type != BodyType::Dynamic || b.sleeping) continue;
        if (minRest_[Find(i)] >= cfg.sleepTime)
        {
            b.sleeping = true;
            b.vel = Vec3();
            b.angVel = Vec3();
        }
    }
}

inline void World::SubStep(float h)
{
    // 1. kinematic bodies: where they go in this pass
    for (uint32_t i = 0; i < bodies_.size(); i++)
        if (bodies_[i].alive && bodies_[i].type == BodyType::Kinematic) PlanKinematic(i, h);
    // 2. gravity and damping
    for (Body& b : bodies_)
    {
        if (!b.alive || b.type != BodyType::Dynamic || b.sleeping) continue;
        b.vel += cfg.gravity * (b.gravScale * h);
        b.vel *= 1.f / (1.f + h * b.linDamp);
        b.angVel *= 1.f / (1.f + h * b.angDamp);
    }
    // 3. contacts at the current poses, warm-started from the last pass
    BuildContacts(h);
    for (Contact& c : contacts_) PrepareContact(c, h);
    for (Contact& c : contacts_) ApplyContactImpulse(c, c.n * c.lambdaN + c.t1 * c.lambdaT1 + c.t2 * c.lambdaT2);
    // 4. sequential impulses, alternately forward and backward (one fixed order pushes a stack sideways)
    const int nC = int(contacts_.size());
    for (int it = 0; it < std::max(1, cfg.iterations); it++)
    {
        if (it % 2 == 0)
            for (int k = 0; k < nC; k++) SolveContact(contacts_[k]);
        else
            for (int k = nC - 1; k >= 0; k--) SolveContact(contacts_[k]);
    }
    // 5. speed limits and positions
    for (Body& b : bodies_)
    {
        if (!b.alive || b.type == BodyType::Static) continue;
        if (b.type == BodyType::Dynamic)
        {
            if (b.sleeping) continue;
            float ls = Length(b.vel), ws = Length(b.angVel);
            if (ls > cfg.maxLinearSpeed) b.vel = b.vel * (cfg.maxLinearSpeed / ls);
            if (ws > cfg.maxAngularSpeed) b.angVel = b.angVel * (cfg.maxAngularSpeed / ws);
        }
        b.pos += b.vel * h;
        b.rot = Integrate(b.rot, b.angVel, h);
        if (!Finite(b.pos) || !Finite(b.vel) || !Finite(b.angVel))
        {
            // a numeric blow-up: go back to the start of the fixed step, at rest
            b.pos = b.prevPos;
            b.rot = b.prevRot;
            b.vel = Vec3();
            b.angVel = Vec3();
            b.sleeping = false;
        }
    }
    // 6. rest: sleep whole islands that have been still long enough
    UpdateSleep(h);
    // 7. keep this pass's impulses for the next pass (sorted by body pair for MatchWarm)
    prev_.swap(contacts_);
    std::sort(prev_.begin(), prev_.end(), [](const Contact& x, const Contact& y) { return Key(x.a, x.b) < Key(y.a, y.b); });
}

// ---------------------------------------------------------------- the hand: grab, hold, throw
// The held body is kinematic: it follows the hand each pass, pushes dynamic bodies, and stops at static ones.
// On release it becomes dynamic with the hand's mean velocity and spin over throwWindow seconds.
class Grabber
{
public:
    struct Config
    {
        float radius = 0.04f;        // m: the pinch point (or the aim point) must be this close to a body
        float maxAimDistance = 4.f;  // m: reach of the aim ray
        float throwWindow = 0.1f;    // s: the release velocity is the mean hand velocity over this time
        float maxThrowSpeed = 8.f;   // m/s: a faster release is clamped (a fling is not a throw)
        float maxThrowSpin = 15.f;   // rad/s
        float pulseTime = 0.25f;     // s: the pulse falls from 1 to 0 after a grab
    };
    Config cfg;
    BodyId hovered;      // the body under the pinch or the ray: highlight it
    BodyId held;         // the body in the hand
    float pulse = 0.f;   // 1 at the grab, falls to 0 over pulseTime: scale the body by 1 + 0.08 * pulse
    Vec3 throwVelocity;  // the last release: the body's linear velocity (m/s) ...
    Vec3 throwSpin;      // ... and its angular velocity (rad/s)

    bool Holding() const { return held.Valid(); }
    // once per frame, between BeginFrame and EndFrame, with f.seconds as now
    void UpdateHand(World& w, const mcx::Hand& hand, double now);
    void UpdateAim(World& w, const mcx::Hand& hand, double now);
    // the same with any point and orientation (a tracker, a controller)
    void UpdateAt(World& w, const Vec3& point, const Quat& rot, bool tracked, bool pinchStarted, bool pinching,
                  double now);
    void Drop(World& w, double now);  // let go now, with the throw velocity
    void Reset(World& w);             // let go now, at rest

private:
    struct Sample
    {
        double t = 0.0;
        Vec3 p;
        Quat q;
    };
    static constexpr int kSamples = 32;
    Sample samples_[kSamples];
    int count_ = 0, head_ = 0;
    Vec3 relPos_;        // the body centre in the hand's frame, at the grab
    Quat relRot_;        // the body orientation in the hand's frame, at the grab
    double grabTime_ = -1e9;
    float aimDist_ = 0.f;
    void Push(double now, const Vec3& p, const Quat& q);
    void Grab(World& w, BodyId id, const Vec3& p, const Quat& q, double now);
    void Follow(World& w, const Vec3& p, const Quat& q, bool tracked, bool pinching, double now);
    void Release(World& w);
};

inline void Grabber::Push(double now, const Vec3& p, const Quat& q)
{
    Sample& s = samples_[head_];
    s.t = now;
    s.p = p;
    s.q = q;
    head_ = (head_ + 1) % kSamples;
    if (count_ < kSamples) count_++;
}

inline void Grabber::Grab(World& w, BodyId id, const Vec3& p, const Quat& q, double now)
{
    Vec3 bp;
    Quat bq;
    if (!w.GetPose(id, bp, bq)) return;
    const Quat inv = Conj(q);
    relPos_ = QuatRotate(inv, bp - p);
    relRot_ = Normalized(inv * bq);
    held = id;
    hovered = id;
    grabTime_ = now;
    count_ = 0;
    head_ = 0;
    w.Wake(id);
    w.SetType(id, BodyType::Kinematic);
    w.SetVelocity(id, Vec3(), Vec3());
    w.SetKinematicTarget(id, bp, bq); // the target is the current pose: no jump at the grab
}

inline void Grabber::Release(World& w)
{
    if (!Holding()) return;
    Vec3 v, spin, lever;
    if (count_ >= 1)
    {
        const Sample& newest = samples_[(head_ + kSamples - 1) % kSamples];
        lever = QuatRotate(newest.q, relPos_); // the body centre, seen from the hand's point
        if (count_ >= 2)
            for (int k = 0; k < count_ - 1; k++) // the oldest sample inside the window
            {
                const Sample& s = samples_[((head_ - count_ + k) % kSamples + kSamples) % kSamples];
                double dt = newest.t - s.t;
                if (dt > cfg.throwWindow) continue;
                if (dt >= 0.02)
                {
                    v = (newest.p - s.p) / float(dt);
                    spin = ToRotVec(newest.q * Conj(s.q)) / float(dt);
                }
                break;
            }
    }
    float sp = Length(v);
    if (sp > cfg.maxThrowSpeed) v = v * (cfg.maxThrowSpeed / sp);
    float ss = Length(spin);
    if (ss > cfg.maxThrowSpin) spin = spin * (cfg.maxThrowSpin / ss);
    const Vec3 linear = v + Cross(spin, lever); // the centre moves with the hand's spin too
    w.ClearKinematicTarget(held);
    w.SetType(held, BodyType::Dynamic);
    w.SetVelocity(held, linear, spin);
    w.Wake(held);
    throwVelocity = linear;
    throwSpin = spin;
    held = BodyId();
    hovered = BodyId();
    count_ = 0;
    head_ = 0;
}

inline void Grabber::Follow(World& w, const Vec3& p, const Quat& q, bool tracked, bool pinching, double now)
{
    if (Holding())
    {
        if (!w.Alive(held))
        {
            held = BodyId();
            count_ = 0;
        }
        else if (!tracked || !pinching)
            Release(w);
        else
        {
            Push(now, p, q);
            w.SetKinematicTarget(held, p + QuatRotate(q, relPos_), Normalized(q * relRot_));
            hovered = held;
        }
    }
    pulse = Clamp(1.f - float(now - grabTime_) / std::max(cfg.pulseTime, 1e-3f), 0.f, 1.f);
}

inline void Grabber::UpdateAt(World& w, const Vec3& point, const Quat& rot, bool tracked, bool pinchStarted,
                              bool pinching, double now)
{
    if (!Holding())
    {
        hovered = tracked ? w.Pick(point, cfg.radius) : BodyId();
        if (tracked && pinchStarted && hovered.Valid()) Grab(w, hovered, point, rot, now);
    }
    Follow(w, point, rot, tracked, pinching, now);
}

inline void Grabber::UpdateHand(World& w, const mcx::Hand& hand, double now)
{
    const Vec3 thumb = FromXr(hand.Joint(XR_HAND_JOINT_THUMB_TIP_EXT).position);
    const Vec3 index = FromXr(hand.Joint(XR_HAND_JOINT_INDEX_TIP_EXT).position);
    const Quat palm = FromXr(hand.Joint(XR_HAND_JOINT_PALM_EXT).orientation);
    UpdateAt(w, (thumb + index) * 0.5f, palm, hand.tracked, hand.pinchStarted, hand.pinching, now);
}

inline void Grabber::UpdateAim(World& w, const mcx::Hand& hand, double now)
{
    const bool ok = hand.tracked && hand.aimValid;
    const Vec3 o = FromXr(hand.aim.position);
    const Quat q = FromXr(hand.aim.orientation);
    const Vec3 dir = QuatRotate(q, Vec3(0.f, 0.f, -1.f));
    if (!Holding())
    {
        hovered = BodyId();
        RayHit hit;
        if (ok && w.Raycast(o, dir, cfg.maxAimDistance, hit) && w.Grabbable(hit.body)) hovered = hit.body;
        if (ok && hand.pinchStarted && hovered.Valid())
        {
            aimDist_ = hit.distance;
            Grab(w, hovered, hit.point, q, now);
        }
    }
    Follow(w, o + dir * aimDist_, q, ok, hand.pinching, now);
}

inline void Grabber::Drop(World& w, double)
{
    Release(w);
}

inline void Grabber::Reset(World& w)
{
    if (!Holding()) return;
    w.ClearKinematicTarget(held);
    w.SetType(held, BodyType::Dynamic);
    w.SetVelocity(held, Vec3(), Vec3());
    w.Wake(held);
    held = BodyId();
    hovered = BodyId();
    count_ = 0;
    head_ = 0;
}

} // namespace phys
} // namespace mcx
