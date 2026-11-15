// mcx_gfx.h: the rendering kit of native MetaCodex apps, header-only, next to mcx_xr.h. Shader helpers, the maths
// the layer lacks, procedural meshes (normals, uvs, counter-clockwise winding), a lit renderer (hemisphere ambient,
// one key light, soft specular, Fresnel rim, emissive, optional fog) with an opaque and a premultiplied translucent
// path, instanced drawing, contact shadows that ground an object on the floor, motion helpers (easing, damping,
// pulses), and additive glow: Sprites (halos, stars, sparks; one draw call), Trail and Particles that feed them.
//
//   mcx::Lit lit;  mcx::ContactShadow shadow;  mcx::Mesh box;
//   lit.Init();  shadow.Init();  box.Upload(mcx::MakeBox({0.2f, 0.2f, 0.2f}));            // once, at init
//   // per eye, between app.BeginEye and app.EndEye:
//   lit.Begin(eye);
//   lit.Draw(box, mcx::Trs({0.f, 1.f, -1.f}, mcx::QuatYawPitch(0.f, 0.f), {1.f, 1.f, 1.f}), mcx::Material::Matte(0x9DBDD2));
//   lit.End();
//   shadow.Draw(eye, {0.f, 1.f, -1.f}, {0.2f, 0.2f, 0.2f}, floorY);                        // after the opaque objects
//   box.Destroy();  lit.Destroy();  shadow.Destroy();                                       // at exit
//
// Conventions (skill quest-native-rendering): metres, Y up, right-handed, angles in radians. Vertex layout: px py pz
// nx ny nz u v (32 bytes; attribute locations 0, 1, 2); indices uint32. Shaders write LINEAR colour (the eye buffers
// are sRGB). A translucent colour is premultiplied: the layer is blended over the passthrough underneath.
// GL state: Begin() sets depth test on (LEQUAL), back-face culling on (CCW front), blending off, depth writes on.
// End() puts the App defaults back: depth test, culling and blending off, depth writes on. DrawTranslucent turns
// blending and depth writes on and off again. ContactShadow::Draw saves the program, depth test, depth writes, depth
// function and blending, and puts them back, so it may run between Begin and End. Nothing allocates in a draw call.
#pragma once
#include <mcx_xr.h>
#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace mcx {

// ================================================================ vectors and quaternions (the layer has Mat4 only)
inline XrVector3f Vec3Add(XrVector3f a, XrVector3f b) { return {a.x + b.x, a.y + b.y, a.z + b.z}; }
inline XrVector3f Vec3Sub(XrVector3f a, XrVector3f b) { return {a.x - b.x, a.y - b.y, a.z - b.z}; }
inline XrVector3f Vec3Scale(XrVector3f a, float s) { return {a.x * s, a.y * s, a.z * s}; }
inline float Vec3Dot(XrVector3f a, XrVector3f b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
inline XrVector3f Vec3Cross(XrVector3f a, XrVector3f b)
{
    return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
}
inline XrVector3f Vec3Normalize(XrVector3f a) // a zero vector gives +Y
{
    float l2 = Vec3Dot(a, a);
    return l2 > 1e-20f ? Vec3Scale(a, 1.f / std::sqrt(l2)) : XrVector3f{0.f, 1.f, 0.f};
}
inline XrVector3f Vec3Lerp(XrVector3f a, XrVector3f b, float t) { return Vec3Add(a, Vec3Scale(Vec3Sub(b, a), t)); }

// a * b: rotates by b, then by a
inline XrQuaternionf QuatMul(XrQuaternionf a, XrQuaternionf b)
{
    return {a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z};
}
inline XrQuaternionf QuatConjugate(XrQuaternionf q) { return {-q.x, -q.y, -q.z, q.w}; }
inline XrQuaternionf QuatNormalize(XrQuaternionf q)
{
    float l2 = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
    if (l2 < 1e-20f) return {0.f, 0.f, 0.f, 1.f};
    float k = 1.f / std::sqrt(l2);
    return {q.x * k, q.y * k, q.z * k, q.w * k};
}
// a counter-clockwise turn by `radians` about `axis` (right-hand rule)
inline XrQuaternionf QuatAxisAngle(XrVector3f axis, float radians)
{
    XrVector3f a = Vec3Normalize(axis);
    float s = std::sin(0.5f * radians);
    return {a.x * s, a.y * s, a.z * s, std::cos(0.5f * radians)};
}
// yaw turns left about +Y; pitch tilts up about +X (applied first). Forward (-Z) becomes
// (-cos(pitch) sin(yaw), sin(pitch), -cos(pitch) cos(yaw)).
inline XrQuaternionf QuatYawPitch(float yaw, float pitch)
{
    return QuatMul(QuatAxisAngle({0.f, 1.f, 0.f}, yaw), QuatAxisAngle({1.f, 0.f, 0.f}, pitch));
}
// the short way between two orientations
inline XrQuaternionf QuatSlerp(XrQuaternionf a, XrQuaternionf b, float t)
{
    float d = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
    if (d < 0.f)
    {
        b = {-b.x, -b.y, -b.z, -b.w};
        d = -d;
    }
    float k0 = 1.f - t, k1 = t;
    if (d < 0.9995f)
    {
        float th = std::acos(std::min(d, 1.f)), s = std::sin(th);
        k0 = std::sin((1.f - t) * th) / s;
        k1 = std::sin(t * th) / s;
    }
    return QuatNormalize({a.x * k0 + b.x * k1, a.y * k0 + b.y * k1, a.z * k0 + b.z * k1, a.w * k0 + b.w * k1});
}
// the orientation whose forward (-Z) points from `from` to `to`; `up` is kept as close as possible
inline XrQuaternionf QuatLookAt(XrVector3f from, XrVector3f to, XrVector3f up = {0.f, 1.f, 0.f})
{
    XrVector3f fwd = Vec3Sub(to, from);
    if (Vec3Dot(fwd, fwd) < 1e-20f) return {0.f, 0.f, 0.f, 1.f};
    XrVector3f z = Vec3Scale(Vec3Normalize(fwd), -1.f); // the object's +Z points back
    XrVector3f x = Vec3Cross(up, z);
    if (Vec3Dot(x, x) < 1e-8f) x = Vec3Cross({0.f, 0.f, 1.f}, z); // up is parallel to the view
    if (Vec3Dot(x, x) < 1e-8f) x = Vec3Cross({1.f, 0.f, 0.f}, z);
    x = Vec3Normalize(x);
    XrVector3f y = Vec3Cross(z, x);
    // the rotation whose columns are x, y, z, as a quaternion (Shepperd's method)
    float tr = x.x + y.y + z.z;
    XrQuaternionf q;
    if (tr > 0.f)
    {
        float s = std::sqrt(tr + 1.f) * 2.f;
        q = {(y.z - z.y) / s, (z.x - x.z) / s, (x.y - y.x) / s, 0.25f * s};
    }
    else if (x.x > y.y && x.x > z.z)
    {
        float s = std::sqrt(1.f + x.x - y.y - z.z) * 2.f;
        q = {0.25f * s, (y.x + x.y) / s, (z.x + x.z) / s, (y.z - z.y) / s};
    }
    else if (y.y > z.z)
    {
        float s = std::sqrt(1.f + y.y - x.x - z.z) * 2.f;
        q = {(y.x + x.y) / s, 0.25f * s, (z.y + y.z) / s, (z.x - x.z) / s};
    }
    else
    {
        float s = std::sqrt(1.f + z.z - x.x - y.y) * 2.f;
        q = {(z.x + x.z) / s, (z.y + y.z) / s, 0.25f * s, (x.y - y.x) / s};
    }
    return QuatNormalize(q);
}

// ================================================================ matrices: what Mat4 lacks
inline XrVector3f MulDir(const Mat4& m, XrVector3f v) // the upper 3x3, no translation
{
    return {m.m[0] * v.x + m.m[4] * v.y + m.m[8] * v.z, m.m[1] * v.x + m.m[5] * v.y + m.m[9] * v.z,
            m.m[2] * v.x + m.m[6] * v.y + m.m[10] * v.z};
}
inline XrVector3f MulPoint(const Mat4& m, XrVector3f p)
{
    XrVector3f r = MulDir(m, p);
    return {r.x + m.m[12], r.y + m.m[13], r.z + m.m[14]};
}
// position, rotation and scale in one model matrix: scale first, then rotate, then move
inline Mat4 Trs(XrVector3f position, XrQuaternionf rotation, XrVector3f scale)
{
    return Mat4::FromPose(XrPosef{rotation, position}) * Mat4::Scale(scale.x, scale.y, scale.z);
}
// a 3x3 matrix, stored as its three columns
struct GfxMat3
{
    XrVector3f col[3];
    XrVector3f Mul(XrVector3f v) const
    {
        return Vec3Add(Vec3Add(Vec3Scale(col[0], v.x), Vec3Scale(col[1], v.y)), Vec3Scale(col[2], v.z));
    }
};
// the normal matrix of a model: the inverse transpose of its upper 3x3, so normals stay perpendicular to surfaces
// under non-uniform scale. Columns are (b x c), (c x a), (a x b) divided by the determinant.
inline GfxMat3 NormalMatrix(const Mat4& m)
{
    XrVector3f a{m.m[0], m.m[1], m.m[2]}, b{m.m[4], m.m[5], m.m[6]}, c{m.m[8], m.m[9], m.m[10]};
    float det = Vec3Dot(a, Vec3Cross(b, c));
    if (std::fabs(det) < 1e-12f) return {{{1.f, 0.f, 0.f}, {0.f, 1.f, 0.f}, {0.f, 0.f, 1.f}}};
    float k = 1.f / det;
    return {{Vec3Scale(Vec3Cross(b, c), k), Vec3Scale(Vec3Cross(c, a), k), Vec3Scale(Vec3Cross(a, b), k)}};
}

// ================================================================ colour: sRGB hex to linear (shaders work in linear)
inline float GfxSrgbToLinear(float c) { return c <= 0.04045f ? c / 12.92f : std::pow((c + 0.055f) / 1.055f, 2.4f); }
inline XrVector3f GfxSrgbRgb(uint32_t rgb)
{
    return {GfxSrgbToLinear(((rgb >> 16) & 255) / 255.f), GfxSrgbToLinear(((rgb >> 8) & 255) / 255.f),
            GfxSrgbToLinear((rgb & 255) / 255.f)};
}
inline XrColor4f GfxSrgb(uint32_t rgb, float alpha = 1.f)
{
    XrVector3f c = GfxSrgbRgb(rgb);
    return {c.x, c.y, c.z, alpha};
}

// ================================================================ shader helpers (failures go to mcx::Log)
inline GLuint CompileShader(GLenum type, const char* src, const char* name)
{
    GLuint s = glCreateShader(type);
    glShaderSource(s, 1, &src, nullptr);
    glCompileShader(s);
    GLint ok = 0;
    glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
    if (!ok)
    {
        char log[1024] = {};
        glGetShaderInfoLog(s, sizeof log, nullptr, log);
        Log("gfx: %s %s shader: %s", name, type == GL_VERTEX_SHADER ? "vertex" : "fragment", log);
        glDeleteShader(s);
        return 0;
    }
    return s;
}
// vertex and fragment source -> program; 0 on failure (the log says why). The shader objects are deleted.
inline GLuint CreateProgram(const char* name, const char* vsSrc, const char* fsSrc)
{
    GLuint vs = CompileShader(GL_VERTEX_SHADER, vsSrc, name);
    GLuint fs = CompileShader(GL_FRAGMENT_SHADER, fsSrc, name);
    GLuint p = 0;
    if (vs && fs)
    {
        p = glCreateProgram();
        glAttachShader(p, vs);
        glAttachShader(p, fs);
        glLinkProgram(p);
        GLint ok = 0;
        glGetProgramiv(p, GL_LINK_STATUS, &ok);
        if (!ok)
        {
            char log[1024] = {};
            glGetProgramInfoLog(p, sizeof log, nullptr, log);
            Log("gfx: %s link: %s", name, log);
            glDeleteProgram(p);
            p = 0;
        }
    }
    if (vs) glDeleteShader(vs);
    if (fs) glDeleteShader(fs);
    return p;
}

// ================================================================ meshes: CPU data (MeshData) and primitives
// the shared vertex layout: 8 floats, 32 bytes; attribute locations 0 position, 1 normal, 2 uv
struct MeshVertex
{
    XrVector3f pos;
    XrVector3f normal;
    XrVector2f uv;
};
static_assert(sizeof(MeshVertex) == 8 * sizeof(float), "the shared vertex layout is 8 floats");

struct MeshData
{
    std::vector<MeshVertex> vertices;
    std::vector<uint32_t> indices; // triangles, counter-clockwise seen from outside
    // adds `other` moved by `t`; normals follow the transform; a mirroring transform flips the winding back
    void Append(const MeshData& other, const Mat4& t)
    {
        GfxMat3 nm = NormalMatrix(t);
        XrVector3f a{t.m[0], t.m[1], t.m[2]}, b{t.m[4], t.m[5], t.m[6]}, c{t.m[8], t.m[9], t.m[10]};
        bool mirror = Vec3Dot(a, Vec3Cross(b, c)) < 0.f;
        uint32_t base = (uint32_t)vertices.size();
        for (const MeshVertex& v : other.vertices)
            vertices.push_back({MulPoint(t, v.pos), Vec3Normalize(nm.Mul(v.normal)), v.uv});
        for (size_t k = 0; k + 2 < other.indices.size(); k += 3)
        {
            uint32_t i0 = base + other.indices[k], i1 = base + other.indices[k + 1], i2 = base + other.indices[k + 2];
            indices.push_back(i0);
            indices.push_back(mirror ? i2 : i1);
            indices.push_back(mirror ? i1 : i2);
        }
    }
    // the box around the vertices (lo = min corner, hi = max corner)
    void Bounds(XrVector3f& lo, XrVector3f& hi) const
    {
        lo = hi = vertices.empty() ? XrVector3f{0.f, 0.f, 0.f} : vertices[0].pos;
        for (const MeshVertex& v : vertices)
        {
            lo = {std::min(lo.x, v.pos.x), std::min(lo.y, v.pos.y), std::min(lo.z, v.pos.z)};
            hi = {std::max(hi.x, v.pos.x), std::max(hi.y, v.pos.y), std::max(hi.z, v.pos.z)};
        }
    }
};

namespace gfx_detail {
inline constexpr float kPi = 3.14159265358979f;
inline constexpr float kTwoPi = 6.28318530717959f;

// a triangle, unless it has no area (two rows meeting at a pole or an apex): sin(angle) below 1e-6
inline void AddTri(MeshData& m, uint32_t a, uint32_t b, uint32_t c)
{
    XrVector3f e1 = Vec3Sub(m.vertices[b].pos, m.vertices[a].pos), e2 = Vec3Sub(m.vertices[c].pos, m.vertices[a].pos);
    XrVector3f cr = Vec3Cross(e1, e2);
    if (Vec3Dot(cr, cr) <= 1e-12f * Vec3Dot(e1, e1) * Vec3Dot(e2, e2)) return;
    m.indices.push_back(a);
    m.indices.push_back(b);
    m.indices.push_back(c);
}
// a parallelogram with corners c -/+ u -/+ v, counter-clockwise seen from the side that cross(u, v) points to
inline void Quad(MeshData& m, XrVector3f c, XrVector3f u, XrVector3f v)
{
    XrVector3f n = Vec3Normalize(Vec3Cross(u, v));
    uint32_t b = (uint32_t)m.vertices.size();
    m.vertices.push_back({Vec3Sub(Vec3Sub(c, u), v), n, {0.f, 0.f}});
    m.vertices.push_back({Vec3Sub(Vec3Add(c, u), v), n, {1.f, 0.f}});
    m.vertices.push_back({Vec3Add(Vec3Add(c, u), v), n, {1.f, 1.f}});
    m.vertices.push_back({Vec3Add(Vec3Sub(c, u), v), n, {0.f, 1.f}});
    AddTri(m, b, b + 1, b + 2);
    AddTri(m, b, b + 2, b + 3);
}
// (rows + 1) x (cols + 1) vertices from vertexAt(i, j); each cell becomes two triangles. The surface needs
// cross(d/di, d/dj) to point out of it, which makes the triangles counter-clockwise.
template <class F>
inline void Grid(MeshData& m, int rows, int cols, F vertexAt)
{
    uint32_t base = (uint32_t)m.vertices.size();
    for (int i = 0; i <= rows; i++)
        for (int j = 0; j <= cols; j++) m.vertices.push_back(vertexAt(i, j));
    auto id = [&](int i, int j) { return base + (uint32_t)(i * (cols + 1) + j); };
    for (int i = 0; i < rows; i++)
        for (int j = 0; j < cols; j++)
        {
            AddTri(m, id(i, j), id(i + 1, j), id(i + 1, j + 1));
            AddTri(m, id(i, j), id(i + 1, j + 1), id(i, j + 1));
        }
}
// a flat disc at height y, normal up or down: a fan around its centre
inline void Cap(MeshData& m, float y, bool up, float radius, int segments)
{
    XrVector3f n{0.f, up ? 1.f : -1.f, 0.f};
    uint32_t c = (uint32_t)m.vertices.size();
    m.vertices.push_back({{0.f, y, 0.f}, n, {0.5f, 0.5f}});
    for (int j = 0; j <= segments; j++)
    {
        float a = kTwoPi * (float)j / (float)segments, cs = std::cos(a), sn = std::sin(a);
        m.vertices.push_back({{radius * cs, y, radius * sn}, n, {0.5f + 0.5f * cs, 0.5f + 0.5f * sn}});
    }
    for (int j = 0; j < segments; j++)
    {
        uint32_t r0 = c + 1 + (uint32_t)j, r1 = r0 + 1;
        if (up) AddTri(m, c, r1, r0);
        else AddTri(m, c, r0, r1);
    }
}
// coordinates in [-1, 1] along one axis of a box with half size h and corner radius r. They are dense near the ends,
// where the rounding happens: samples per quarter circle on each end, the flat middle is one segment.
inline std::vector<float> RoundedCoords(float h, float r, int samples)
{
    float w = h > 0.f ? std::min(0.45f, r / h) : 0.f;
    std::vector<float> v;
    for (int i = 0; i <= samples; i++) v.push_back(-1.f + w * (1.f - std::cos(0.5f * kPi * (float)i / (float)samples)));
    for (int i = samples; i >= 0; i--) v.push_back(1.f - w * (1.f - std::cos(0.5f * kPi * (float)i / (float)samples)));
    return v;
}
// the shared layout on the array buffer that is bound now (locations 0, 1, 2)
inline void VertexAttribs()
{
    const GLsizei stride = (GLsizei)sizeof(MeshVertex);
    glEnableVertexAttribArray(0);
    glVertexAttribPointer(0, 3, GL_FLOAT, GL_FALSE, stride, reinterpret_cast<const void*>(offsetof(MeshVertex, pos)));
    glEnableVertexAttribArray(1);
    glVertexAttribPointer(1, 3, GL_FLOAT, GL_FALSE, stride, reinterpret_cast<const void*>(offsetof(MeshVertex, normal)));
    glEnableVertexAttribArray(2);
    glVertexAttribPointer(2, 2, GL_FLOAT, GL_FALSE, stride, reinterpret_cast<const void*>(offsetof(MeshVertex, uv)));
}
} // namespace gfx_detail

// an axis-aligned box, centred; `size` is the full size in metres. Flat normals, 6 uv squares.
inline MeshData MakeBox(XrVector3f size)
{
    MeshData m;
    XrVector3f h = Vec3Scale(size, 0.5f);
    gfx_detail::Quad(m, {h.x, 0.f, 0.f}, {0.f, h.y, 0.f}, {0.f, 0.f, h.z});   // +X
    gfx_detail::Quad(m, {-h.x, 0.f, 0.f}, {0.f, 0.f, h.z}, {0.f, h.y, 0.f});  // -X
    gfx_detail::Quad(m, {0.f, h.y, 0.f}, {0.f, 0.f, h.z}, {h.x, 0.f, 0.f});   // +Y
    gfx_detail::Quad(m, {0.f, -h.y, 0.f}, {h.x, 0.f, 0.f}, {0.f, 0.f, h.z}); // -Y
    gfx_detail::Quad(m, {0.f, 0.f, h.z}, {h.x, 0.f, 0.f}, {0.f, h.y, 0.f});   // +Z
    gfx_detail::Quad(m, {0.f, 0.f, -h.z}, {0.f, h.y, 0.f}, {h.x, 0.f, 0.f});  // -Z
    return m;
}
// a box with rounded edges and corners. `radius` is clamped to 0.45 x the smallest half size (the flat part stays);
// `samples` is the number of segments per quarter circle (2 is enough at small radii). Smooth normals on the rounding.
inline MeshData MakeRoundedBox(XrVector3f size, float radius, int samples)
{
    MeshData m;
    XrVector3f H = Vec3Scale(size, 0.5f);
    float r = std::min(std::max(radius, 0.f), 0.45f * std::min(H.x, std::min(H.y, H.z)));
    samples = std::max(1, samples);
    XrVector3f I{H.x - r, H.y - r, H.z - r}; // the core box the rounding wraps around
    // the six faces: outward normal n, then axes u, v with cross(u, v) = n
    static const float kFace[6][9] = {{1, 0, 0, 0, 1, 0, 0, 0, 1},  {-1, 0, 0, 0, 0, 1, 0, 1, 0},
                                      {0, 1, 0, 0, 0, 1, 1, 0, 0},  {0, -1, 0, 1, 0, 0, 0, 0, 1},
                                      {0, 0, 1, 1, 0, 0, 0, 1, 0},  {0, 0, -1, 0, 1, 0, 1, 0, 0}};
    for (const auto& q : kFace)
    {
        XrVector3f n{q[0], q[1], q[2]}, u{q[3], q[4], q[5]}, v{q[6], q[7], q[8]};
        std::vector<float> a = gfx_detail::RoundedCoords(std::fabs(Vec3Dot(u, H)), r, samples);
        std::vector<float> b = gfx_detail::RoundedCoords(std::fabs(Vec3Dot(v, H)), r, samples);
        gfx_detail::Grid(m, (int)a.size() - 1, (int)b.size() - 1, [&](int i, int j) {
            // a point on the cube surface (in metres), pulled onto the rounded box: the offset from the core box
            // gives the normal, the core box plus r along it gives the position
            XrVector3f d = Vec3Add(n, Vec3Add(Vec3Scale(u, a[i]), Vec3Scale(v, b[j])));
            XrVector3f s{d.x * H.x, d.y * H.y, d.z * H.z};
            XrVector3f c{std::min(std::max(s.x, -I.x), I.x), std::min(std::max(s.y, -I.y), I.y),
                         std::min(std::max(s.z, -I.z), I.z)};
            XrVector3f o = Vec3Sub(s, c);
            XrVector3f nn = Vec3Dot(o, o) > 1e-20f ? Vec3Normalize(o) : n;
            return MeshVertex{Vec3Add(c, Vec3Scale(nn, r)), nn, {0.5f * (a[i] + 1.f), 0.5f * (b[j] + 1.f)}};
        });
    }
    return m;
}
// a flat square on the XZ plane (normal +Y), centred, divX by divZ cells
inline MeshData MakePlane(float sizeX, float sizeZ, int divX, int divZ)
{
    MeshData m;
    divX = std::max(1, divX);
    divZ = std::max(1, divZ);
    gfx_detail::Grid(m, divZ, divX, [&](int i, int j) {
        float s = (float)i / (float)divZ, t = (float)j / (float)divX; // s along z, t along x
        return MeshVertex{{(t - 0.5f) * sizeX, 0.f, (s - 0.5f) * sizeZ}, {0.f, 1.f, 0.f}, {t, s}};
    });
    return m;
}
// a cylinder along Y, centred, with flat caps
inline MeshData MakeCylinder(float radius, float height, int segments)
{
    MeshData m;
    segments = std::max(3, segments);
    float h2 = 0.5f * height;
    gfx_detail::Grid(m, 1, segments, [&](int i, int j) {
        float a = gfx_detail::kTwoPi * (float)j / (float)segments, c = std::cos(a), s = std::sin(a);
        return MeshVertex{{radius * c, i ? h2 : -h2, radius * s}, {c, 0.f, s}, {(float)j / (float)segments, (float)i}};
    });
    gfx_detail::Cap(m, h2, true, radius, segments);
    gfx_detail::Cap(m, -h2, false, radius, segments);
    return m;
}
// a cone along Y: base disc at -height/2 (radius `radius`), apex at +height/2, with a flat base
inline MeshData MakeCone(float radius, float height, int segments)
{
    MeshData m;
    segments = std::max(3, segments);
    float h2 = 0.5f * height;
    gfx_detail::Grid(m, 1, segments, [&](int i, int j) {
        float a = gfx_detail::kTwoPi * (float)j / (float)segments, c = std::cos(a), s = std::sin(a);
        XrVector3f p = i ? XrVector3f{0.f, h2, 0.f} : XrVector3f{radius * c, -h2, radius * s};
        XrVector3f n = Vec3Normalize({c * height, radius, s * height}); // the slope, without dividing by height
        return MeshVertex{p, n, {(float)j / (float)segments, (float)i}};
    });
    gfx_detail::Cap(m, -h2, false, radius, segments);
    return m;
}
// a capsule along Y: radius, total height (2 x radius gives a sphere), segments around, rings per half
inline MeshData MakeCapsule(float radius, float height, int segments, int rings)
{
    MeshData m;
    segments = std::max(3, segments);
    rings = std::max(2, rings);
    float L = std::max(0.f, height - 2.f * radius); // the straight part between the two half spheres
    struct Ring
    {
        float phi, yc; // latitude, and the centre height of the half sphere it belongs to
    };
    std::vector<Ring> rs;
    for (int k = 0; k <= rings; k++) rs.push_back({-0.5f * gfx_detail::kPi + 0.5f * gfx_detail::kPi * (float)k / (float)rings, -0.5f * L});
    for (int k = L > 0.f ? 0 : 1; k <= rings; k++) rs.push_back({0.5f * gfx_detail::kPi * (float)k / (float)rings, 0.5f * L});
    gfx_detail::Grid(m, (int)rs.size() - 1, segments, [&](int i, int j) {
        float phi = rs[i].phi, cp = std::cos(phi), sp = std::sin(phi);
        if (std::fabs(std::fabs(phi) - 0.5f * gfx_detail::kPi) < 1e-6f) cp = 0.f; // exact poles: their vertices coincide
        float a = gfx_detail::kTwoPi * (float)j / (float)segments, c = std::cos(a), s = std::sin(a);
        XrVector3f n{cp * c, sp, cp * s};
        return MeshVertex{{radius * n.x, rs[i].yc + radius * n.y, radius * n.z}, n,
                          {(float)j / (float)segments, (phi + 0.5f * gfx_detail::kPi) / gfx_detail::kPi}};
    });
    return m;
}
inline MeshData MakeSphere(float radius, int segments, int rings)
{
    return MakeCapsule(radius, 2.f * radius, segments, rings);
}
// a ring lying flat (its axis is Y): `major` from the axis to the tube centre, `minor` the tube radius
inline MeshData MakeTorus(float major, float minor, int segments, int tubeSegments)
{
    MeshData m;
    segments = std::max(3, segments);
    tubeSegments = std::max(3, tubeSegments);
    gfx_detail::Grid(m, tubeSegments, segments, [&](int i, int j) {
        float v = gfx_detail::kTwoPi * (float)i / (float)tubeSegments, u = gfx_detail::kTwoPi * (float)j / (float)segments;
        float cv = std::cos(v), sv = std::sin(v), cu = std::cos(u), su = std::sin(u);
        return MeshVertex{{(major + minor * cv) * cu, minor * sv, (major + minor * cv) * su}, {cv * cu, sv, cv * su},
                          {(float)j / (float)segments, (float)i / (float)tubeSegments}};
    });
    return m;
}

// ================================================================ the GPU mesh
// one VAO with the shared layout (0 position, 1 normal, 2 uv) and an index buffer. Keep it alive while it is drawn.
class Mesh
{
public:
    Mesh() = default;
    Mesh(const Mesh&) = delete;
    Mesh& operator=(const Mesh&) = delete;
    bool Upload(const MeshData& data) // creates or replaces the buffers; false when the data is empty
    {
        Destroy();
        if (data.vertices.empty() || data.indices.empty()) return false;
        glGenVertexArrays(1, &vao_);
        glGenBuffers(1, &vbo_);
        glGenBuffers(1, &ibo_);
        glBindVertexArray(vao_);
        glBindBuffer(GL_ARRAY_BUFFER, vbo_);
        glBufferData(GL_ARRAY_BUFFER, (GLsizeiptr)(data.vertices.size() * sizeof(MeshVertex)), data.vertices.data(), GL_STATIC_DRAW);
        glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, ibo_);
        glBufferData(GL_ELEMENT_ARRAY_BUFFER, (GLsizeiptr)(data.indices.size() * sizeof(uint32_t)), data.indices.data(), GL_STATIC_DRAW);
        gfx_detail::VertexAttribs();
        glBindVertexArray(0);
        glBindBuffer(GL_ARRAY_BUFFER, 0);
        glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, 0);
        indexCount_ = (uint32_t)data.indices.size();
        return true;
    }
    void Draw() const // one draw call; a program must be bound (Lit::Begin does it)
    {
        if (!vao_) return;
        glBindVertexArray(vao_);
        glDrawElements(GL_TRIANGLES, (GLsizei)indexCount_, GL_UNSIGNED_INT, nullptr);
        glBindVertexArray(0);
    }
    void Destroy()
    {
        if (vao_) glDeleteVertexArrays(1, &vao_);
        if (vbo_) glDeleteBuffers(1, &vbo_);
        if (ibo_) glDeleteBuffers(1, &ibo_);
        vao_ = vbo_ = ibo_ = 0;
        indexCount_ = 0;
    }
    bool Ready() const { return vao_ != 0; }
    uint32_t IndexCount() const { return indexCount_; }
    GLuint Vbo() const { return vbo_; }
    GLuint Ibo() const { return ibo_; }

private:
    GLuint vao_ = 0, vbo_ = 0, ibo_ = 0;
    uint32_t indexCount_ = 0;
};

// ================================================================ materials and lighting
// a surface. base alpha is used by DrawTranslucent only; Draw makes the object solid.
struct Material
{
    XrColor4f base{0.8f, 0.8f, 0.8f, 1.f}; // linear rgba
    XrVector3f emissive{0.f, 0.f, 0.f};   // linear rgb, added after the light (ignores shadows)
    float roughness = 0.6f;               // 0 glossy (a sharp highlight) .. 1 matte (a soft highlight)
    float rim = 0.f;                      // Fresnel rim strength (0 off); the rim takes base + emissive colour
    bool unlit = false;                   // base + emissive only: flat markers and labels
    // presets, sRGB in
    static Material Matte(uint32_t srgb)
    {
        Material m;
        m.base = GfxSrgb(srgb);
        m.roughness = 0.85f;
        return m;
    }
    // a dark body with a coloured glow: emissive and rim in the colour, strength 0..1
    static Material Glow(uint32_t srgb, float strength = 1.f)
    {
        Material m;
        XrVector3f c = GfxSrgbRgb(srgb);
        m.base = {0.25f * c.x, 0.25f * c.y, 0.25f * c.z, 1.f};
        m.emissive = Vec3Scale(c, 0.8f * strength);
        m.rim = 0.6f * strength;
        m.roughness = 0.35f;
        return m;
    }
    // a tinted see-through surface: use with DrawTranslucent; alpha 0.1-0.4 reads as glass
    static Material Glass(uint32_t srgb, float alpha = 0.25f)
    {
        Material m;
        m.base = GfxSrgb(srgb, alpha);
        m.rim = 0.9f;
        m.roughness = 0.1f;
        return m;
    }
};

// the scene light: a key light, a hemisphere ambient, exposure and optional fog (defaults tuned for passthrough)
struct Lighting
{
    XrVector3f keyDir{-0.35f, 0.85f, 0.40f}; // towards the key light (normalised by Begin)
    XrVector3f keyColor{0.95f, 0.92f, 0.87f}; // linear, intensity included
    XrVector3f sky{0.28f, 0.31f, 0.36f};     // ambient from above, linear
    XrVector3f ground{0.10f, 0.09f, 0.085f}; // ambient from below, linear
    float exposure = 1.f;                    // multiplies lit and emissive colour
    float fogStart = 0.f, fogEnd = 0.f;      // metres; fogEnd <= fogStart: no fog
    XrVector3f fogColor{0.f, 0.f, 0.f};
    float fogOpacity = 0.f;                  // 0 fades objects into the room (passthrough); 1 fades into fogColor (VR)
};

// ================================================================ instances: one mesh, many model matrices, one call
// per instance: the model matrix (attributes 3..6) and its normal matrix (attributes 7..9), divisor 1
class Instances
{
public:
    Instances() = default;
    Instances(const Instances&) = delete;
    Instances& operator=(const Instances&) = delete;
    // the mesh must stay alive while the instances are drawn; `capacity` is fixed here (no growth later)
    bool Init(const Mesh& mesh, int capacity)
    {
        Destroy();
        if (!mesh.Ready() || capacity < 1) return false;
        capacity_ = capacity;
        data_.reserve((size_t)capacity * kFloats);
        glGenVertexArrays(1, &vao_);
        glGenBuffers(1, &vbo_);
        glBindVertexArray(vao_);
        glBindBuffer(GL_ARRAY_BUFFER, mesh.Vbo());
        gfx_detail::VertexAttribs();
        glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, mesh.Ibo());
        glBindBuffer(GL_ARRAY_BUFFER, vbo_);
        glBufferData(GL_ARRAY_BUFFER, (GLsizeiptr)((size_t)capacity * kFloats * sizeof(float)), nullptr, GL_DYNAMIC_DRAW);
        const GLsizei stride = (GLsizei)(kFloats * sizeof(float));
        for (GLuint k = 0; k < 4; k++) // model matrix columns
        {
            glEnableVertexAttribArray(3 + k);
            glVertexAttribPointer(3 + k, 4, GL_FLOAT, GL_FALSE, stride, reinterpret_cast<const void*>((size_t)k * 16));
            glVertexAttribDivisor(3 + k, 1);
        }
        for (GLuint k = 0; k < 3; k++) // normal matrix columns
        {
            glEnableVertexAttribArray(7 + k);
            glVertexAttribPointer(7 + k, 3, GL_FLOAT, GL_FALSE, stride, reinterpret_cast<const void*>(64 + (size_t)k * 12));
            glVertexAttribDivisor(7 + k, 1);
        }
        glBindVertexArray(0);
        glBindBuffer(GL_ARRAY_BUFFER, 0);
        glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, 0);
        indexCount_ = mesh.IndexCount();
        return true;
    }
    void Clear() { data_.clear(); } // count 0, keeps the memory
    bool Add(const Mat4& model)     // false when full
    {
        if (Count() >= capacity_) return false;
        data_.insert(data_.end(), model.m, model.m + 16);
        GfxMat3 n = NormalMatrix(model);
        for (const XrVector3f& c : n.col)
        {
            data_.push_back(c.x);
            data_.push_back(c.y);
            data_.push_back(c.z);
        }
        return true;
    }
    int Count() const { return (int)(data_.size() / kFloats); }
    void Upload() // sends the used part to the GPU; Lit::DrawInstanced calls it for you
    {
        if (!vbo_ || data_.empty()) return;
        glBindBuffer(GL_ARRAY_BUFFER, vbo_);
        glBufferSubData(GL_ARRAY_BUFFER, 0, (GLsizeiptr)(data_.size() * sizeof(float)), data_.data());
        glBindBuffer(GL_ARRAY_BUFFER, 0);
    }
    void Destroy()
    {
        if (vao_) glDeleteVertexArrays(1, &vao_);
        if (vbo_) glDeleteBuffers(1, &vbo_);
        vao_ = vbo_ = 0;
        indexCount_ = 0;
        capacity_ = 0;
        data_.clear();
    }
    GLuint Vao() const { return vao_; }
    uint32_t IndexCount() const { return indexCount_; }

private:
    static constexpr size_t kFloats = 25; // 16 model + 9 normal
    GLuint vao_ = 0, vbo_ = 0;
    uint32_t indexCount_ = 0;
    int capacity_ = 0;
    std::vector<float> data_;
};

// ================================================================ the lit renderer
inline constexpr const char* kGfxLitVs = R"(#version 300 es
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 3) in vec4 aM0;
layout(location = 4) in vec4 aM1;
layout(location = 5) in vec4 aM2;
layout(location = 6) in vec4 aM3;
layout(location = 7) in vec3 aN0;
layout(location = 8) in vec3 aN1;
layout(location = 9) in vec3 aN2;
uniform mat4 uViewProj;
uniform mat4 uModel;
uniform mat3 uNormal;
uniform bool uInstanced;
out highp vec3 vWorld;
out mediump vec3 vNormal;
void main() {
    mat4 m = uInstanced ? mat4(aM0, aM1, aM2, aM3) : uModel;
    mat3 n = uInstanced ? mat3(aN0, aN1, aN2) : uNormal;
    highp vec4 w = m * vec4(aPos, 1.0);
    vWorld = w.xyz;
    vNormal = n * aNormal;
    gl_Position = uViewProj * w;
}
)";

inline constexpr const char* kGfxLitFs = R"(#version 300 es
precision mediump float;
in highp vec3 vWorld;
in mediump vec3 vNormal;
uniform highp vec3 uEyePos;
uniform vec3 uKeyDir;
uniform vec3 uKeyColor;
uniform vec3 uSky;
uniform vec3 uGround;
uniform float uExposure;
uniform vec4 uBase;
uniform vec3 uEmissive;
uniform float uRoughness;
uniform float uRim;
uniform bool uUnlit;
uniform bool uTranslucent;
uniform vec2 uFogRange;
uniform vec3 uFogColor;
uniform float uFogOpacity;
out vec4 fragColor;
void main() {
    vec3 n = normalize(vNormal);
    if (!gl_FrontFacing) n = -n;
    vec3 v = normalize(uEyePos - vWorld);
    vec3 col;
    if (uUnlit) {
        col = uBase.rgb + uEmissive;
    } else {
        float ndl = max(dot(n, uKeyDir), 0.0);
        vec3 ambient = mix(uGround, uSky, n.y * 0.5 + 0.5);
        col = uBase.rgb * (ambient + uKeyColor * ndl);
        float shin = mix(96.0, 6.0, uRoughness);
        float spec = pow(max(dot(n, normalize(uKeyDir + v)), 0.0), shin) * (shin + 8.0) * 0.0398 * ndl * mix(0.5, 0.04, uRoughness);
        col += uKeyColor * spec;
        float fres = pow(1.0 - max(dot(n, v), 0.0), 3.0);
        col += (uBase.rgb + uEmissive) * (uRim * fres);
        col += uEmissive;
    }
    col = min(col * uExposure, vec3(1.0));
    float alpha = uTranslucent ? uBase.a : 1.0;
    vec3 premul = col * alpha;
    if (uFogRange.y > uFogRange.x) {
        float f = smoothstep(uFogRange.x, uFogRange.y, length(uEyePos - vWorld));
        premul = mix(premul, uFogColor * uFogOpacity, f);
        alpha = mix(alpha, uFogOpacity, f);
    }
    fragColor = vec4(premul, alpha);
}
)";

// one program for every lit object: hemisphere ambient, a key light, a soft specular, a Fresnel rim, emissive, fog
class Lit
{
public:
    Lit() = default;
    Lit(const Lit&) = delete;
    Lit& operator=(const Lit&) = delete;
    Lighting light;
    bool Init() // compiles the program; false (and a log line) when it fails
    {
        Destroy();
        prog_ = CreateProgram("lit", kGfxLitVs, kGfxLitFs);
        if (!prog_) return false;
        uViewProj_ = glGetUniformLocation(prog_, "uViewProj");
        uModel_ = glGetUniformLocation(prog_, "uModel");
        uNormal_ = glGetUniformLocation(prog_, "uNormal");
        uInstanced_ = glGetUniformLocation(prog_, "uInstanced");
        uEyePos_ = glGetUniformLocation(prog_, "uEyePos");
        uKeyDir_ = glGetUniformLocation(prog_, "uKeyDir");
        uKeyColor_ = glGetUniformLocation(prog_, "uKeyColor");
        uSky_ = glGetUniformLocation(prog_, "uSky");
        uGround_ = glGetUniformLocation(prog_, "uGround");
        uExposure_ = glGetUniformLocation(prog_, "uExposure");
        uBase_ = glGetUniformLocation(prog_, "uBase");
        uEmissive_ = glGetUniformLocation(prog_, "uEmissive");
        uRoughness_ = glGetUniformLocation(prog_, "uRoughness");
        uRim_ = glGetUniformLocation(prog_, "uRim");
        uUnlit_ = glGetUniformLocation(prog_, "uUnlit");
        uTranslucent_ = glGetUniformLocation(prog_, "uTranslucent");
        uFogRange_ = glGetUniformLocation(prog_, "uFogRange");
        uFogColor_ = glGetUniformLocation(prog_, "uFogColor");
        uFogOpacity_ = glGetUniformLocation(prog_, "uFogOpacity");
        return true;
    }
    // per eye, before the first draw: state, camera position and the light (eye.viewProj, eye.pose.position)
    void Begin(const Eye& eye)
    {
        if (!prog_) return;
        glUseProgram(prog_);
        glEnable(GL_DEPTH_TEST);
        glDepthFunc(GL_LEQUAL);
        glDepthMask(GL_TRUE);
        glDisable(GL_BLEND);
        glEnable(GL_CULL_FACE);
        glCullFace(GL_BACK);
        glFrontFace(GL_CCW);
        XrVector3f k = Vec3Normalize(light.keyDir);
        glUniformMatrix4fv(uViewProj_, 1, GL_FALSE, eye.viewProj.m);
        glUniform3f(uEyePos_, eye.pose.position.x, eye.pose.position.y, eye.pose.position.z);
        glUniform3f(uKeyDir_, k.x, k.y, k.z);
        glUniform3f(uKeyColor_, light.keyColor.x, light.keyColor.y, light.keyColor.z);
        glUniform3f(uSky_, light.sky.x, light.sky.y, light.sky.z);
        glUniform3f(uGround_, light.ground.x, light.ground.y, light.ground.z);
        glUniform1f(uExposure_, light.exposure);
        glUniform2f(uFogRange_, light.fogStart, light.fogEnd);
        glUniform3f(uFogColor_, light.fogColor.x, light.fogColor.y, light.fogColor.z);
        glUniform1f(uFogOpacity_, light.fogOpacity);
    }
    // opaque: writes depth, the colour is solid (alpha 1, or the fog's fade)
    void Draw(const Mesh& mesh, const Mat4& model, const Material& mat) const
    {
        if (!prog_ || !mesh.Ready()) return;
        SetModel(model);
        SetMaterial(mat, false);
        mesh.Draw();
    }
    // translucent: blended over what is behind (base alpha), writes no depth. Draw these last, back to front.
    void DrawTranslucent(const Mesh& mesh, const Mat4& model, const Material& mat) const
    {
        if (!prog_ || !mesh.Ready()) return;
        SetModel(model);
        SetMaterial(mat, true);
        glEnable(GL_BLEND);
        glBlendFuncSeparate(GL_ONE, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
        glDepthMask(GL_FALSE);
        mesh.Draw();
        glDepthMask(GL_TRUE);
        glDisable(GL_BLEND);
    }
    // one draw call for all the instances (uploads them first)
    void DrawInstanced(Instances& inst, const Material& mat) const { DrawInstancedImpl(inst, mat, false); }
    void DrawInstancedTranslucent(Instances& inst, const Material& mat) const { DrawInstancedImpl(inst, mat, true); }
    // after the last draw of the eye: the App defaults (depth test, culling and blending off, depth writes on)
    void End() const
    {
        glBindVertexArray(0);
        glDisable(GL_CULL_FACE);
        glDisable(GL_DEPTH_TEST);
        glDisable(GL_BLEND);
        glDepthMask(GL_TRUE);
        glUseProgram(0);
    }
    void Destroy()
    {
        if (prog_) glDeleteProgram(prog_);
        prog_ = 0;
    }

private:
    void SetModel(const Mat4& model) const
    {
        glUniform1i(uInstanced_, 0);
        glUniformMatrix4fv(uModel_, 1, GL_FALSE, model.m);
        GfxMat3 n = NormalMatrix(model);
        float nm[9] = {n.col[0].x, n.col[0].y, n.col[0].z, n.col[1].x, n.col[1].y, n.col[1].z, n.col[2].x, n.col[2].y, n.col[2].z};
        glUniformMatrix3fv(uNormal_, 1, GL_FALSE, nm);
    }
    void SetMaterial(const Material& m, bool translucent) const
    {
        glUniform4f(uBase_, m.base.r, m.base.g, m.base.b, m.base.a);
        glUniform3f(uEmissive_, m.emissive.x, m.emissive.y, m.emissive.z);
        glUniform1f(uRoughness_, m.roughness);
        glUniform1f(uRim_, m.rim);
        glUniform1i(uUnlit_, m.unlit ? 1 : 0);
        glUniform1i(uTranslucent_, translucent ? 1 : 0);
    }
    void DrawInstancedImpl(Instances& inst, const Material& mat, bool translucent) const
    {
        if (!prog_ || !inst.Vao() || inst.Count() == 0) return;
        inst.Upload();
        glUniform1i(uInstanced_, 1);
        SetMaterial(mat, translucent);
        if (translucent)
        {
            glEnable(GL_BLEND);
            glBlendFuncSeparate(GL_ONE, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
            glDepthMask(GL_FALSE);
        }
        glBindVertexArray(inst.Vao());
        glDrawElementsInstanced(GL_TRIANGLES, (GLsizei)inst.IndexCount(), GL_UNSIGNED_INT, nullptr, inst.Count());
        glBindVertexArray(0);
        if (translucent)
        {
            glDepthMask(GL_TRUE);
            glDisable(GL_BLEND);
        }
        glUniform1i(uInstanced_, 0);
    }

    GLuint prog_ = 0;
    GLint uViewProj_ = -1, uModel_ = -1, uNormal_ = -1, uInstanced_ = -1, uEyePos_ = -1, uKeyDir_ = -1, uKeyColor_ = -1;
    GLint uSky_ = -1, uGround_ = -1, uExposure_ = -1, uBase_ = -1, uEmissive_ = -1, uRoughness_ = -1, uRim_ = -1;
    GLint uUnlit_ = -1, uTranslucent_ = -1, uFogRange_ = -1, uFogColor_ = -1, uFogOpacity_ = -1;
};

// ================================================================ contact shadows: grounding on the floor
inline constexpr const char* kGfxShadowVs = R"(#version 300 es
layout(location = 0) in vec3 aPos;
uniform mat4 uViewProj;
uniform mat4 uModel;
out mediump vec2 vLocal;
void main() {
    vLocal = aPos.xz * 2.0;
    gl_Position = uViewProj * uModel * vec4(aPos, 1.0);
}
)";

inline constexpr const char* kGfxShadowFs = R"(#version 300 es
precision mediump float;
in mediump vec2 vLocal;
uniform float uStrength;
uniform float uFade;
out vec4 fragColor;
void main() {
    float r = length(vLocal);
    float a = (1.0 - smoothstep(0.2, 1.0, r)) * uStrength * uFade;
    fragColor = vec4(0.0, 0.0, 0.0, a);
}
)";

// a soft dark disc on the floor under an object: the cheap blob shadow that grounds it in the room
class ContactShadow
{
public:
    float fadeHeight = 1.2f; // metres above the floor where the shadow is gone
    ContactShadow() = default;
    ContactShadow(const ContactShadow&) = delete;
    ContactShadow& operator=(const ContactShadow&) = delete;
    bool Init()
    {
        Destroy();
        prog_ = CreateProgram("contact shadow", kGfxShadowVs, kGfxShadowFs);
        if (!prog_) return false;
        uViewProj_ = glGetUniformLocation(prog_, "uViewProj");
        uModel_ = glGetUniformLocation(prog_, "uModel");
        uStrength_ = glGetUniformLocation(prog_, "uStrength");
        uFade_ = glGetUniformLocation(prog_, "uFade");
        return disc_.Upload(MakePlane(1.f, 1.f, 1, 1));
    }
    // center and size of the object in metres, floorY the floor's height (see the skill: where the floor is);
    // radius = 0.55 x the larger horizontal side; the shadow fades with the object's height above the floor
    void Draw(const Eye& eye, XrVector3f center, XrVector3f size, float floorY, float strength = 0.5f) const
    {
        if (!prog_ || !disc_.Ready()) return;
        float radius = std::max(0.03f, 0.55f * std::max(size.x, size.z));
        float lift = std::max(0.f, center.y - 0.5f * size.y - floorY);
        float t = std::min(1.f, lift / std::max(fadeHeight, 1e-3f));
        float fade = 1.f - t * t * (3.f - 2.f * t);
        Mat4 model = Mat4::Translation(center.x, floorY + 0.002f, center.z) * Mat4::Scale(2.f * radius, 1.f, 2.f * radius);
        // the caller may be between Begin and End: save the state this call changes, and put it back at the end
        GLint prog = 0, depthFunc = GL_LESS;
        GLboolean depthTest = glIsEnabled(GL_DEPTH_TEST), blend = glIsEnabled(GL_BLEND), depthMask = GL_TRUE;
        glGetIntegerv(GL_CURRENT_PROGRAM, &prog);
        glGetIntegerv(GL_DEPTH_FUNC, &depthFunc);
        glGetBooleanv(GL_DEPTH_WRITEMASK, &depthMask);
        glUseProgram(prog_);
        glUniformMatrix4fv(uViewProj_, 1, GL_FALSE, eye.viewProj.m);
        glUniformMatrix4fv(uModel_, 1, GL_FALSE, model.m);
        glUniform1f(uStrength_, strength);
        glUniform1f(uFade_, fade);
        glEnable(GL_DEPTH_TEST);
        glDepthFunc(GL_LEQUAL);
        glDepthMask(GL_FALSE);
        glEnable(GL_BLEND);
        glBlendFuncSeparate(GL_ONE, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
        disc_.Draw();
        glUseProgram((GLuint)prog);
        glDepthFunc((GLenum)depthFunc);
        glDepthMask(depthMask);
        if (depthTest) glEnable(GL_DEPTH_TEST);
        else glDisable(GL_DEPTH_TEST);
        if (blend) glEnable(GL_BLEND);
        else glDisable(GL_BLEND);
    }
    void Destroy()
    {
        if (prog_) glDeleteProgram(prog_);
        prog_ = 0;
        disc_.Destroy();
    }

private:
    GLuint prog_ = 0;
    GLint uViewProj_ = -1, uModel_ = -1, uStrength_ = -1, uFade_ = -1;
    Mesh disc_;
};

// ================================================================ motion: easing, damping, pulses
// t is 0..1 (clamped). Arrive with EaseOut, leave with EaseIn, move from A to B with EaseInOut. EaseOutBack overshoots
// a little: game objects only, never UI. Damp follows a moving target at the same speed at any frame rate: `rate` per
// second (10: the gap closes 63 % in 0.1 s, settled in about 0.3 s). Progress turns a start time into 0..1.
inline float Clamp01(float t) { return t < 0.f ? 0.f : (t > 1.f ? 1.f : t); }
inline float EaseIn(float t) { t = Clamp01(t); return t * t * t; }
inline float EaseOut(float t) { t = 1.f - Clamp01(t); return 1.f - t * t * t; }
inline float EaseInOut(float t) { t = Clamp01(t); return t < 0.5f ? 4.f * t * t * t : 1.f - 4.f * (1.f - t) * (1.f - t) * (1.f - t); }
inline float EaseOutBack(float t, float overshoot = 1.70158f) { t = Clamp01(t) - 1.f; return 1.f + t * t * ((overshoot + 1.f) * t + overshoot); }
// 0..1 from `start` (Frame::seconds) over `duration` seconds
inline float Progress(double now, double start, float duration) { return duration > 0.f ? Clamp01((float)((now - start) / duration)) : 1.f; }
// 0..1..0 at `hz` cycles per second: idle breathing, a glow that pulses
inline float Pulse(double seconds, float hz) { return 0.5f - 0.5f * std::cos((float)std::fmod(seconds * hz, 1.0) * gfx_detail::kTwoPi); }
inline float Damp(float current, float target, float rate, float dt) { return target + (current - target) * std::exp(-rate * dt); }
inline XrVector3f Vec3Damp(XrVector3f current, XrVector3f target, float rate, float dt) { return Vec3Lerp(target, current, std::exp(-rate * dt)); }
inline XrQuaternionf QuatDamp(XrQuaternionf current, XrQuaternionf target, float rate, float dt) { return QuatSlerp(current, target, 1.f - std::exp(-rate * dt)); }

// ================================================================ glow sprites: additive light, one draw call
// Soft round sprites that face the eye and ADD light: halos round glowing objects, particles, sparks, trails, stars.
// The colour adds; the alpha grows only by `dim` x the halo. The layer is premultiplied, so over passthrough a sprite
// lights the room (and darkens it a little under its centre, so it still reads on a white wall).
// Fixed capacity (set in Init, no growth); Add copies 8 floats into reserved memory. Draw after the opaque objects:
// depth test on, no depth writes, so solid things hide sprites and sprites never hide anything. Additive: no sorting.
inline constexpr const char* kGfxSpriteVs = R"(#version 300 es
layout(location = 0) in vec2 aCorner;
layout(location = 1) in vec4 aPosSize;
layout(location = 2) in vec4 aColorCore;
uniform mat4 uViewProj;
uniform vec3 uRight;
uniform vec3 uUp;
out mediump vec2 vUv;
out mediump vec4 vColor;
void main() {
    vUv = aCorner;
    vColor = aColorCore;
    vec3 p = aPosSize.xyz + (uRight * aCorner.x + uUp * aCorner.y) * (0.5 * aPosSize.w);
    gl_Position = uViewProj * vec4(p, 1.0);
}
)";

inline constexpr const char* kGfxSpriteFs = R"(#version 300 es
precision mediump float;
in mediump vec2 vUv;
in mediump vec4 vColor;
uniform float uDim;
uniform float uIntensity;
out vec4 fragColor;
void main() {
    float h = 1.0 - min(dot(vUv, vUv), 1.0);
    float halo = h * h * h;
    float core = vColor.a * smoothstep(0.75, 0.94, h);
    fragColor = vec4(vColor.rgb * ((halo + core) * uIntensity), uDim * halo * uIntensity);
}
)";

class Sprites
{
public:
    float dim = 0.3f;      // 0..1: how much a sprite also darkens what is behind its centre (0: pure added light)
    bool depthTest = true; // false: drawn over everything (a marker that must always show)
    float intensity = 1.f; // multiplies every sprite: fades a whole set in or out (a fixed starfield) with no Add
    Sprites() = default;
    Sprites(const Sprites&) = delete;
    Sprites& operator=(const Sprites&) = delete;
    bool Init(int capacity) // compiles the program; false (and a log line) when it fails
    {
        Destroy();
        if (capacity < 1) return false;
        prog_ = CreateProgram("sprites", kGfxSpriteVs, kGfxSpriteFs);
        if (!prog_) return false;
        uViewProj_ = glGetUniformLocation(prog_, "uViewProj");
        uRight_ = glGetUniformLocation(prog_, "uRight");
        uUp_ = glGetUniformLocation(prog_, "uUp");
        uDim_ = glGetUniformLocation(prog_, "uDim");
        uIntensity_ = glGetUniformLocation(prog_, "uIntensity");
        capacity_ = capacity;
        data_.reserve((size_t)capacity * kFloats);
        static const float corners[8] = {-1.f, -1.f, 1.f, -1.f, -1.f, 1.f, 1.f, 1.f};
        glGenVertexArrays(1, &vao_);
        glGenBuffers(1, &quad_);
        glGenBuffers(1, &vbo_);
        glBindVertexArray(vao_);
        glBindBuffer(GL_ARRAY_BUFFER, quad_);
        glBufferData(GL_ARRAY_BUFFER, sizeof corners, corners, GL_STATIC_DRAW);
        glEnableVertexAttribArray(0);
        glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 2 * sizeof(float), nullptr);
        glBindBuffer(GL_ARRAY_BUFFER, vbo_);
        glBufferData(GL_ARRAY_BUFFER, (GLsizeiptr)((size_t)capacity * kFloats * sizeof(float)), nullptr, GL_DYNAMIC_DRAW);
        const GLsizei stride = (GLsizei)(kFloats * sizeof(float));
        glEnableVertexAttribArray(1);
        glVertexAttribPointer(1, 4, GL_FLOAT, GL_FALSE, stride, nullptr);
        glVertexAttribDivisor(1, 1);
        glEnableVertexAttribArray(2);
        glVertexAttribPointer(2, 4, GL_FLOAT, GL_FALSE, stride, reinterpret_cast<const void*>(4 * sizeof(float)));
        glVertexAttribDivisor(2, 1);
        glBindVertexArray(0);
        glBindBuffer(GL_ARRAY_BUFFER, 0);
        return true;
    }
    void Clear() // count 0, keeps the memory (each frame for moving sprites; never for a fixed starfield)
    {
        data_.clear();
        dirty_ = true;
    }
    // centre and diameter in metres; LINEAR colour with the brightness in it (mcx::GfxSrgbRgb once at init, scaled);
    // core 0..1: a small hot centre (stars, sparks). False when full.
    bool Add(XrVector3f position, float size, XrVector3f color, float core = 0.f)
    {
        if (Count() >= capacity_) return false;
        const float v[kFloats] = {position.x, position.y, position.z, size, color.x, color.y, color.z, core};
        data_.insert(data_.end(), v, v + kFloats);
        dirty_ = true;
        return true;
    }
    int Count() const { return (int)(data_.size() / kFloats); }
    int Capacity() const { return capacity_; }
    // one draw call into the eye, after the opaque objects. Uploads what changed since the last Draw. Saves the GL state
    // it changes (program, depth, culling, blending and its function) and puts it back, so it may run anywhere.
    void Draw(const Eye& eye)
    {
        if (!prog_ || data_.empty() || intensity <= 0.f) return;
        if (dirty_)
        {
            glBindBuffer(GL_ARRAY_BUFFER, vbo_);
            glBufferSubData(GL_ARRAY_BUFFER, 0, (GLsizeiptr)(data_.size() * sizeof(float)), data_.data());
            glBindBuffer(GL_ARRAY_BUFFER, 0);
            dirty_ = false;
        }
        GLint prog = 0, depthFunc = GL_LESS, srcRgb = GL_ONE, dstRgb = GL_ZERO, srcA = GL_ONE, dstA = GL_ZERO;
        GLboolean wasDepth = glIsEnabled(GL_DEPTH_TEST), wasBlend = glIsEnabled(GL_BLEND), wasCull = glIsEnabled(GL_CULL_FACE);
        GLboolean depthMask = GL_TRUE;
        glGetIntegerv(GL_CURRENT_PROGRAM, &prog);
        glGetIntegerv(GL_DEPTH_FUNC, &depthFunc);
        glGetBooleanv(GL_DEPTH_WRITEMASK, &depthMask);
        glGetIntegerv(GL_BLEND_SRC_RGB, &srcRgb);
        glGetIntegerv(GL_BLEND_DST_RGB, &dstRgb);
        glGetIntegerv(GL_BLEND_SRC_ALPHA, &srcA);
        glGetIntegerv(GL_BLEND_DST_ALPHA, &dstA);
        glUseProgram(prog_);
        glUniformMatrix4fv(uViewProj_, 1, GL_FALSE, eye.viewProj.m);
        glUniform3f(uRight_, eye.view.m[0], eye.view.m[4], eye.view.m[8]); // the eye's right and up axes in the world
        glUniform3f(uUp_, eye.view.m[1], eye.view.m[5], eye.view.m[9]);
        glUniform1f(uDim_, dim);
        glUniform1f(uIntensity_, intensity);
        if (depthTest)
        {
            glEnable(GL_DEPTH_TEST);
            glDepthFunc(GL_LEQUAL);
        }
        else glDisable(GL_DEPTH_TEST);
        glDepthMask(GL_FALSE);
        glDisable(GL_CULL_FACE);
        glEnable(GL_BLEND);
        glBlendFuncSeparate(GL_ONE, GL_ONE, GL_ONE, GL_ONE);
        glBindVertexArray(vao_);
        glDrawArraysInstanced(GL_TRIANGLE_STRIP, 0, 4, (GLsizei)Count());
        glBindVertexArray(0);
        glBlendFuncSeparate((GLenum)srcRgb, (GLenum)dstRgb, (GLenum)srcA, (GLenum)dstA);
        glUseProgram((GLuint)prog);
        glDepthFunc((GLenum)depthFunc);
        glDepthMask(depthMask);
        if (wasDepth) glEnable(GL_DEPTH_TEST);
        else glDisable(GL_DEPTH_TEST);
        if (wasCull) glEnable(GL_CULL_FACE);
        if (!wasBlend) glDisable(GL_BLEND);
    }
    void Destroy()
    {
        if (prog_) glDeleteProgram(prog_);
        if (vao_) glDeleteVertexArrays(1, &vao_);
        if (quad_) glDeleteBuffers(1, &quad_);
        if (vbo_) glDeleteBuffers(1, &vbo_);
        prog_ = vao_ = quad_ = vbo_ = 0;
        capacity_ = 0;
        data_.clear();
        dirty_ = true;
    }

private:
    static constexpr size_t kFloats = 8; // x y z size, r g b core
    GLuint prog_ = 0, vao_ = 0, quad_ = 0, vbo_ = 0;
    GLint uViewProj_ = -1, uRight_ = -1, uUp_ = -1, uDim_ = -1, uIntensity_ = -1;
    int capacity_ = 0;
    bool dirty_ = true;
    std::vector<float> data_;
};

// ================================================================ trails: the recent path of a moving thing
// A fixed ring of points (no growth after Init). Push the position each frame; a point is kept only when it is
// `spacing` metres from the last one, so the trail has the same length at any speed. AddTo draws it into Sprites,
// fading from the newest point to the oldest. Reset when the thing stops or jumps.
class Trail
{
public:
    bool Init(int points, float spacing)
    {
        if (points < 2) return false;
        pts_.assign((size_t)points, XrVector3f{0.f, 0.f, 0.f});
        head_ = count_ = 0;
        spacing_ = spacing;
        return true;
    }
    void Reset() { count_ = 0; }
    void Push(XrVector3f p)
    {
        if (pts_.empty()) return;
        if (count_ > 0)
        {
            XrVector3f d = Vec3Sub(p, pts_[(size_t)head_]);
            if (Vec3Dot(d, d) < spacing_ * spacing_) return;
        }
        head_ = (head_ + 1) % (int)pts_.size();
        pts_[(size_t)head_] = p;
        if (count_ < (int)pts_.size()) count_++;
    }
    int Count() const { return count_; }
    XrVector3f At(int i) const // 0 = the newest point; i < Count()
    {
        const int n = (int)pts_.size();
        return pts_[(size_t)(((head_ - i) % n + n) % n)];
    }
    // newest: `size` and `color`; both fade towards the oldest point
    void AddTo(Sprites& s, float size, XrVector3f color) const
    {
        for (int i = 0; i < count_; i++)
        {
            const float k = 1.f - (float)i / (float)count_;
            s.Add(At(i), size * (0.35f + 0.65f * k), Vec3Scale(color, k * k));
        }
    }

private:
    std::vector<XrVector3f> pts_;
    int head_ = 0, count_ = 0;
    float spacing_ = 0.01f;
};

// ================================================================ particles: short bursts of glow
// A fixed pool (no growth after Init): sparks on a hit, a celebration, dust. Burst starts some, Update moves them
// (drag + gravity), AddTo draws the live ones into Sprites; each shrinks and fades over its life.
class Particles
{
public:
    XrVector3f gravity{0.f, -0.6f, 0.f}; // metres per second squared: a light fall; {0, 0, 0} in space
    float drag = 1.5f;                   // per second: the share of speed lost (exponential)
    bool Init(int capacity)
    {
        if (capacity < 1) return false;
        p_.assign((size_t)capacity, P{});
        return true;
    }
    // up to `count` new particles at `at`: speeds up to `speed` m/s in random directions (`up` lifts them),
    // diameter about `size` metres, life about `life` seconds. Dead particles are reused; none when the pool is full.
    void Burst(XrVector3f at, int count, float speed, XrVector3f color, float size, float life, float up = 0.3f)
    {
        for (P& q : p_)
        {
            if (count <= 0) break;
            if (q.age < q.life) continue;
            XrVector3f d = Vec3Normalize({Rand() * 2.f - 1.f, Rand() * 2.f - 1.f, Rand() * 2.f - 1.f});
            d.y += up;
            q.pos = at;
            q.vel = Vec3Scale(Vec3Normalize(d), speed * (0.35f + 0.65f * Rand()));
            q.color = color;
            q.size = size * (0.6f + 0.4f * Rand());
            q.age = 0.f;
            q.life = life * (0.6f + 0.4f * Rand());
            count--;
        }
    }
    void Update(float dt)
    {
        const float k = std::exp(-drag * dt);
        for (P& q : p_)
        {
            if (q.age >= q.life) continue;
            q.age += dt;
            q.vel = Vec3Add(Vec3Scale(q.vel, k), Vec3Scale(gravity, dt));
            q.pos = Vec3Add(q.pos, Vec3Scale(q.vel, dt));
        }
    }
    void AddTo(Sprites& s) const
    {
        for (const P& q : p_)
        {
            if (q.age >= q.life) continue;
            const float k = 1.f - q.age / q.life;
            s.Add(q.pos, q.size * (0.4f + 0.6f * k), Vec3Scale(q.color, k * k), 0.5f * k);
        }
    }
    int Alive() const
    {
        int n = 0;
        for (const P& q : p_) n += q.age < q.life ? 1 : 0;
        return n;
    }

private:
    struct P
    {
        XrVector3f pos{0.f, 0.f, 0.f}, vel{0.f, 0.f, 0.f}, color{0.f, 0.f, 0.f};
        float size = 0.f, age = 1.f, life = 0.f; // age >= life: dead
    };
    float Rand() // 0..1, xorshift: no allocation, no global state
    {
        seed_ ^= seed_ << 13;
        seed_ ^= seed_ >> 17;
        seed_ ^= seed_ << 5;
        return (float)(seed_ & 0xFFFFFFu) / 16777216.f;
    }
    std::vector<P> p_;
    uint32_t seed_ = 0x9E3779B9u;
};

} // namespace mcx
