// Hello Native: the smallest native Quest app MetaCodex builds and installs, and the template of `mcx-app new`.
// C++ on MetaCodex's native XR layer (mcx_xr.h: OpenXR + OpenGL ES 3, the session, input and the app menu).
// It shows a dark space with three slowly turning coloured cubes in front of you.
// The app menu is built in: the left controller's menu button or the left hand's menu gesture opens Back to
// MetaCodex / Report to MetaCodex / Restart / Quit. Build: `mcx-app build` (in MetaCodex's Linux).
#include <mcx_xr.h>
#include <cmath>
#include <vector>

namespace {

const char* kVertex = R"(#version 300 es
layout(location = 0) in vec3 position;
layout(location = 1) in vec3 color;
uniform mat4 mvp;
out vec3 vColor;
void main() { vColor = color; gl_Position = mvp * vec4(position, 1.0); }
)";

const char* kFragment = R"(#version 300 es
precision mediump float;
in vec3 vColor;
uniform vec3 tint;
out vec4 outColor;
void main() { outColor = vec4(vColor * tint, 1.0); }
)";

GLuint Compile(GLenum type, const char* src)
{
    GLuint s = glCreateShader(type);
    glShaderSource(s, 1, &src, nullptr);
    glCompileShader(s);
    GLint ok = 0;
    glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
    if (!ok) { char log[512]; glGetShaderInfoLog(s, sizeof log, nullptr, log); mcx::Log("shader: %s", log); }
    return s;
}

struct Scene
{
    GLuint program = 0, vao = 0, vbo = 0;
    GLint mvp = -1, tint = -1;
};

void BuildScene(Scene& sc)
{
    sc.program = glCreateProgram();
    glAttachShader(sc.program, Compile(GL_VERTEX_SHADER, kVertex));
    glAttachShader(sc.program, Compile(GL_FRAGMENT_SHADER, kFragment));
    glLinkProgram(sc.program);
    sc.mvp = glGetUniformLocation(sc.program, "mvp");
    sc.tint = glGetUniformLocation(sc.program, "tint");
    // a unit cube, one grey shade per face (position xyz, shade rgb); each cube gets its colour from `tint`
    static const float faces[6][4][3] = {
        {{-1, -1, 1}, {1, -1, 1}, {1, 1, 1}, {-1, 1, 1}},      {{1, -1, -1}, {-1, -1, -1}, {-1, 1, -1}, {1, 1, -1}},
        {{-1, -1, -1}, {-1, -1, 1}, {-1, 1, 1}, {-1, 1, -1}},  {{1, -1, 1}, {1, -1, -1}, {1, 1, -1}, {1, 1, 1}},
        {{-1, 1, 1}, {1, 1, 1}, {1, 1, -1}, {-1, 1, -1}},      {{-1, -1, -1}, {1, -1, -1}, {1, -1, 1}, {-1, -1, 1}},
    };
    static const float shade[6] = {1.0f, 0.55f, 0.75f, 0.8f, 0.95f, 0.45f};
    std::vector<float> v;
    for (int f = 0; f < 6; f++)
    {
        const int idx[6] = {0, 1, 2, 0, 2, 3};
        for (int k : idx)
        {
            v.insert(v.end(), {faces[f][k][0], faces[f][k][1], faces[f][k][2]});
            v.insert(v.end(), {shade[f], shade[f], shade[f]});
        }
    }
    glGenVertexArrays(1, &sc.vao);
    glBindVertexArray(sc.vao);
    glGenBuffers(1, &sc.vbo);
    glBindBuffer(GL_ARRAY_BUFFER, sc.vbo);
    glBufferData(GL_ARRAY_BUFFER, (GLsizeiptr)(v.size() * sizeof(float)), v.data(), GL_STATIC_DRAW);
    glEnableVertexAttribArray(0);
    glVertexAttribPointer(0, 3, GL_FLOAT, GL_FALSE, 6 * sizeof(float), (void*)0);
    glEnableVertexAttribArray(1);
    glVertexAttribPointer(1, 3, GL_FLOAT, GL_FALSE, 6 * sizeof(float), (void*)(3 * sizeof(float)));
    glBindVertexArray(0);
}

// yaw (around y) then pitch (around x), uniform scale, then the position
mcx::Mat4 Model(float x, float y, float z, float size, float yaw, float pitch)
{
    float cy = cosf(yaw), sy = sinf(yaw), cp = cosf(pitch), sp = sinf(pitch);
    mcx::Mat4 m{};
    m.m[0] = cy * size;  m.m[2] = -sy * size;
    m.m[4] = sy * sp * size; m.m[5] = cp * size; m.m[6] = cy * sp * size;
    m.m[8] = sy * cp * size; m.m[9] = -sp * size; m.m[10] = cy * cp * size;
    m.m[12] = x; m.m[13] = y; m.m[14] = z; m.m[15] = 1.f;
    return m;
}

void Draw(const Scene& sc, const mcx::Eye& eye, double t)
{
    glClearColor(0.015f, 0.025f, 0.05f, 1.f);
    glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
    glEnable(GL_DEPTH_TEST);
    glEnable(GL_CULL_FACE);
    glUseProgram(sc.program);
    glBindVertexArray(sc.vao);
    // three cubes ahead of where the app started: cyan, magenta, amber
    struct Cube { float x, y, z, size, speed, r, g, b; } cubes[3] = {
        {0.0f, 0.0f, -1.5f, 0.12f, 0.6f, 0.21f, 0.82f, 1.0f},
        {-0.55f, 0.18f, -1.9f, 0.09f, -0.9f, 0.85f, 0.35f, 1.0f},
        {0.6f, -0.12f, -1.8f, 0.10f, 0.75f, 1.0f, 0.72f, 0.28f},
    };
    for (const Cube& c : cubes)
    {
        mcx::Mat4 mvp = eye.viewProj * Model(c.x, c.y, c.z, c.size, (float)(t * c.speed), (float)(t * c.speed * 0.6));
        glUniformMatrix4fv(sc.mvp, 1, GL_FALSE, mvp.m);
        glUniform3f(sc.tint, c.r, c.g, c.b);
        glDrawArrays(GL_TRIANGLES, 0, 36);
    }
    glBindVertexArray(0);
    glDisable(GL_CULL_FACE);
}

} // namespace

void android_main(android_app* android)
{
    mcx::Config cfg;
    cfg.name = "Hello Native";
    mcx::App app;
    if (!app.Init(android, cfg)) { while (app.Pump()) {} return; }
    Scene sc;
    BuildScene(sc);
    while (app.Pump())
    {
        mcx::Frame f;
        if (!app.BeginFrame(f)) continue;
        for (int e = 0; e < 2; e++)
        {
            mcx::Eye eye;
            app.BeginEye(e, eye);
            Draw(sc, eye, f.seconds);
            app.EndEye(e);
        }
        app.EndFrame();
    }
    app.Shutdown();
}
