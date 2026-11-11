// Hello Module: the template of `mcx-app new --module`. A native Quest app written as a module of MetaCodex's host
// (mcx_module.h): the host owns the activity, EGL and the OpenXR session; this file owns the scene.
//   mcx-app dev <dir>     compile this module and run it in the Runner while you edit (seconds, no APK, no install)
//   mcx-app build <dir>   the same module + the host -> the app's own APK (Library icon, runs without MetaCodex)
// Meta's OpenXR extensions: host->hasExtension("XR_FB_passthrough") etc. and host->getProcAddr(host->instance, ...).
// A reload (dev) calls mcx_app_shutdown on the old module and mcx_app_init on the new one: free what you made.
#include <mcx_module.h>
#include <cmath>
#include <cstring>
#include <vector>

MCX_MODULE_ABI_EXPORT

namespace {

const McxHost* g_host = nullptr;
GLuint g_program = 0, g_vao = 0, g_vbo = 0;
GLint g_mvp = -1, g_tint = -1;

struct Mat { float m[16]; };

Mat Mul(const float* a, const float* b)
{
    Mat r{};
    for (int c = 0; c < 4; c++)
        for (int k = 0; k < 4; k++)
        {
            float s = 0.f;
            for (int i = 0; i < 4; i++) s += a[i * 4 + k] * b[c * 4 + i];
            r.m[c * 4 + k] = s;
        }
    return r;
}

Mat Model(float x, float y, float z, float size, float yaw, float pitch)
{
    float cy = cosf(yaw), sy = sinf(yaw), cp = cosf(pitch), sp = sinf(pitch);
    Mat m{};
    m.m[0] = cy * size;  m.m[2] = -sy * size;
    m.m[4] = sy * sp * size; m.m[5] = cp * size; m.m[6] = cy * sp * size;
    m.m[8] = sy * cp * size; m.m[9] = -sp * size; m.m[10] = cy * cp * size;
    m.m[12] = x; m.m[13] = y; m.m[14] = z; m.m[15] = 1.f;
    return m;
}

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
    if (!ok) { char log[512]; glGetShaderInfoLog(s, sizeof log, nullptr, log); g_host->log("shader: %s", log); }
    return s;
}

double g_seconds = 0;

} // namespace

extern "C" MCX_EXPORT int mcx_app_init(const McxHost* host)
{
    g_host = host;
    host->log("Hello Module: init (%s, passthrough %s, hands %s)", host->dev ? "Runner" : "own APK",
              host->hasExtension("XR_FB_passthrough") ? "yes" : "no", host->hasExtension("XR_EXT_hand_tracking") ? "yes" : "no");
    g_program = glCreateProgram();
    GLuint vs = Compile(GL_VERTEX_SHADER, kVertex), fs = Compile(GL_FRAGMENT_SHADER, kFragment);
    glAttachShader(g_program, vs);
    glAttachShader(g_program, fs);
    glLinkProgram(g_program);
    glDeleteShader(vs);
    glDeleteShader(fs);
    g_mvp = glGetUniformLocation(g_program, "mvp");
    g_tint = glGetUniformLocation(g_program, "tint");
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
    glGenVertexArrays(1, &g_vao);
    glBindVertexArray(g_vao);
    glGenBuffers(1, &g_vbo);
    glBindBuffer(GL_ARRAY_BUFFER, g_vbo);
    glBufferData(GL_ARRAY_BUFFER, (GLsizeiptr)(v.size() * sizeof(float)), v.data(), GL_STATIC_DRAW);
    glEnableVertexAttribArray(0);
    glVertexAttribPointer(0, 3, GL_FLOAT, GL_FALSE, 6 * sizeof(float), (void*)0);
    glEnableVertexAttribArray(1);
    glVertexAttribPointer(1, 3, GL_FLOAT, GL_FALSE, 6 * sizeof(float), (void*)(3 * sizeof(float)));
    glBindVertexArray(0);
    return 0;
}

extern "C" MCX_EXPORT void mcx_app_frame(const McxFrame* frame, McxFrameOut*)
{
    g_seconds = frame->seconds;
}

extern "C" MCX_EXPORT void mcx_app_draw(const McxEye* eye)
{
    glClearColor(0.015f, 0.025f, 0.05f, 1.f);
    glClearDepthf(1.f);
    glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
    glEnable(GL_DEPTH_TEST);
    glEnable(GL_CULL_FACE);
    glUseProgram(g_program);
    glBindVertexArray(g_vao);
    Mat vp = Mul(eye->projection, eye->viewMatrix);
    struct Cube { float x, y, z, size, speed, r, g, b; } cubes[3] = {
        {0.0f, 0.0f, -1.5f, 0.12f, 0.6f, 0.21f, 0.82f, 1.0f},
        {-0.55f, 0.18f, -1.9f, 0.09f, -0.9f, 0.85f, 0.35f, 1.0f},
        {0.6f, -0.12f, -1.8f, 0.10f, 0.75f, 1.0f, 0.72f, 0.28f},
    };
    for (const Cube& c : cubes)
    {
        Mat model = Model(c.x, c.y, c.z, c.size, (float)(g_seconds * c.speed), (float)(g_seconds * c.speed * 0.6));
        Mat mvp = Mul(vp.m, model.m);
        glUniformMatrix4fv(g_mvp, 1, GL_FALSE, mvp.m);
        glUniform3f(g_tint, c.r, c.g, c.b);
        glDrawArrays(GL_TRIANGLES, 0, 36);
    }
    glBindVertexArray(0);
}

extern "C" MCX_EXPORT void mcx_app_shutdown(void)
{
    glDeleteBuffers(1, &g_vbo);
    glDeleteVertexArrays(1, &g_vao);
    glDeleteProgram(g_program);
    g_vbo = g_vao = g_program = 0;
}
