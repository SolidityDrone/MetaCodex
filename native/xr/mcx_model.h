// mcx_model.h: glTF 2.0 models (.glb and .gltf) for native Quest apps: load, upload to the GPU, draw lit, per eye.
//
// Implementation: one .cpp of the app defines MCX_MODEL_IMPLEMENTATION before its first include of this header.
// That .cpp compiles cgltf and stb_image once. Every other .cpp includes this header without the define.
// Two .cpp files that define it give duplicate-symbol link errors. A .cpp that forgets it gives undefined
// references to cgltf_* at link time.
//     main.cpp:   #define MCX_MODEL_IMPLEMENTATION
//                 #include <mcx_model.h>
//     scene.cpp:  #include <mcx_model.h>
//
// Needs mcx_xr.h (Mat4, Eye, Log) and third_party/cgltf.h, third_party/stb_image.h next to this file.
// Units: metres, Y up, right-handed. glTF uses the same units. Fit a model with ModelFitHeight or Model::FitHeight.
// Colour: material factors and base colour textures are linear (the GPU decodes the texture as sRGB). The renderer
// writes linear colour; the eye buffers are sRGB.
//
// Loaded: triangle lists, strips and fans. POSITION, NORMAL (flat normals when missing), TEXCOORD_0 (0 when missing).
// Node hierarchy flattened into one world matrix per primitive (the default scene). Base colour texture with mipmaps.
// Factors: base colour, metallic, roughness, emissive. Alpha mode, double-sided, KHR_materials_unlit,
// KHR_materials_emissive_strength. Images: PNG and JPEG (embedded, data URI or external file).
// Not loaded: skins (drawn in rest pose, the mesh node's transform is ignored as glTF says), morph targets, animation,
// cameras, lights, normal and other maps, COLOR_0, KHR_texture_transform, Draco and meshopt, KTX2 and WebP.
//
// GL state: Draw enables depth test (LESS), writes depth for opaque and mask primitives and not for blend ones, enables
// blending for blend primitives only, enables back-face culling for single-sided primitives. After Draw: depth test on,
// depth write on, blending off, culling off, program 0, VAO 0, texture 0 unbound.
// Needs GL ES 3.0 (sRGB textures, mipmaps, VAOs).
#pragma once
#include <mcx_xr.h>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <utility>
#include <vector>

#if defined(__ANDROID__)
#include <android/asset_manager.h>
#else
struct AAssetManager; // host builds (tests): asset loading reports an error
#endif

// third-party single-file libraries, vendored in third_party/ (see third_party/README.md)
#ifndef STBI_ONLY_PNG
#define STBI_ONLY_PNG // glTF images are PNG or JPEG only
#endif
#ifndef STBI_ONLY_JPEG
#define STBI_ONLY_JPEG
#endif
#ifndef STBI_NO_STDIO
#define STBI_NO_STDIO // images are decoded from memory
#endif
#ifdef MCX_MODEL_IMPLEMENTATION
#define CGLTF_IMPLEMENTATION
#define STB_IMAGE_IMPLEMENTATION
#endif
#include "third_party/cgltf.h"
#include "third_party/stb_image.h"

namespace mcx {

// ---------------------------------------------------------------- CPU data (no GL: parse on any thread)
enum class ModelAlpha { Opaque, Mask, Blend };

struct ModelBounds
{
    bool valid = false;
    float min[3] = {0, 0, 0}, max[3] = {0, 0, 0}; // metres
    float Size(int axis) const { return valid ? max[axis] - min[axis] : 0.f; }
    float Centre(int axis) const { return valid ? 0.5f * (max[axis] + min[axis]) : 0.f; }
};

struct ModelImage
{
    int width = 0, height = 0;
    std::vector<uint8_t> rgba; // 8 bits per channel, width * height * 4, row 0 first
};

struct ModelTexture
{
    int image = -1;                       // index into ModelData::images
    bool clampS = false, clampT = false;  // false: repeat (the glTF default)
};

struct ModelMaterial
{
    float baseColor[4] = {1, 1, 1, 1};    // linear; multiplies the texture
    int texture = -1;                     // index into ModelData::textures (base colour), -1: none
    float metallic = 0.f, roughness = 0.8f;
    float emissive[3] = {0, 0, 0};        // linear, with the emissive strength applied
    ModelAlpha alpha = ModelAlpha::Opaque;
    float alphaCutoff = 0.5f;
    bool doubleSided = false;
    bool unlit = false;
};

struct ModelPrimitive
{
    int material = -1;                    // index into ModelData::materials; -1: white, matte, non-metal
    std::vector<float> vertices;          // 8 floats per vertex: px py pz nx ny nz u v, node-local metres
    std::vector<uint32_t> indices;        // triangle list (counter-clockwise front faces, as glTF)
    float world[16] = {1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1}; // node world matrix, column-major
};

struct ModelData
{
    std::string name;                     // file or asset name (for log lines)
    std::vector<ModelPrimitive> primitives;
    std::vector<ModelMaterial> materials;
    std::vector<ModelTexture> textures;
    std::vector<ModelImage> images;
    ModelBounds bounds;                   // all primitives, after their world matrices (metres)
    int Triangles() const
    {
        int n = 0;
        for (const ModelPrimitive& p : primitives) n += (int)(p.indices.size() / 3);
        return n;
    }
};

// sRGB hex colour to linear (the kit's own helper: other kits define their own name)
inline float ModelSrgbToLinear(float c) { return c <= 0.04045f ? c / 12.92f : std::pow((c + 0.055f) / 1.055f, 2.4f); }
inline XrColor4f ModelSrgb(uint32_t hex, float alpha = 1.f)
{
    XrColor4f c;
    c.r = ModelSrgbToLinear(((hex >> 16) & 0xFF) / 255.f);
    c.g = ModelSrgbToLinear(((hex >> 8) & 0xFF) / 255.f);
    c.b = ModelSrgbToLinear((hex & 0xFF) / 255.f);
    c.a = alpha;
    return c;
}

// the renderer's light: a hemisphere ambient (sky above, ground below) and one directional light. Linear colours.
// The defaults suit a bright room; tune them per app with ModelRenderer::SetLight.
struct ModelLight
{
    float dir[3] = {0.4f, 0.8f, 0.5f};     // towards the light (normalised in the shader)
    float color[3] = {0.75f, 0.66f, 0.56f};
    float sky[3] = {0.22f, 0.28f, 0.36f};
    float ground[3] = {0.05f, 0.04f, 0.035f};
};

namespace detail {

inline Mat4 ModelIdentity()
{
    Mat4 r;
    for (int k = 0; k < 16; k++) r.m[k] = (k % 5 == 0) ? 1.f : 0.f;
    return r;
}

// the upper 3x3 of a column-major matrix -> its inverse transpose (the normal matrix), column-major
inline void ModelNormalMatrix(const float m[16], float out[9])
{
    const float a = m[0], b = m[4], c = m[8];  // row 0
    const float d = m[1], e = m[5], f = m[9];  // row 1
    const float g = m[2], h = m[6], i = m[10]; // row 2
    const float c00 = e * i - f * h, c01 = f * g - d * i, c02 = d * h - e * g;
    const float c10 = c * h - b * i, c11 = a * i - c * g, c12 = b * g - a * h;
    const float c20 = b * f - c * e, c21 = c * d - a * f, c22 = a * e - b * d;
    const float det = a * c00 + b * c01 + c * c02;
    if (std::fabs(det) < 1e-20f)
    {
        for (int k = 0; k < 9; k++) out[k] = (k % 4 == 0) ? 1.f : 0.f;
        return;
    }
    const float k = 1.f / det;
    out[0] = c00 * k; out[1] = c10 * k; out[2] = c20 * k;
    out[3] = c01 * k; out[4] = c11 * k; out[5] = c21 * k;
    out[6] = c02 * k; out[7] = c12 * k; out[8] = c22 * k;
}

// out = a * b for column-major 3x3
inline void ModelMul3(const float a[9], const float b[9], float out[9])
{
    for (int col = 0; col < 3; col++)
        for (int row = 0; row < 3; row++)
            out[col * 3 + row] = a[row] * b[col * 3] + a[3 + row] * b[col * 3 + 1] + a[6 + row] * b[col * 3 + 2];
}

inline void ModelPoint(const float m[16], const float p[3], float out[3])
{
    out[0] = m[0] * p[0] + m[4] * p[1] + m[8] * p[2] + m[12];
    out[1] = m[1] * p[0] + m[5] * p[1] + m[9] * p[2] + m[13];
    out[2] = m[2] * p[0] + m[6] * p[1] + m[10] * p[2] + m[14];
}

inline void ModelExtend(ModelBounds& b, const float p[3])
{
    if (!b.valid)
    {
        for (int k = 0; k < 3; k++) b.min[k] = b.max[k] = p[k];
        b.valid = true;
        return;
    }
    for (int k = 0; k < 3; k++)
    {
        if (p[k] < b.min[k]) b.min[k] = p[k];
        if (p[k] > b.max[k]) b.max[k] = p[k];
    }
}

inline std::string ModelBaseName(const std::string& path)
{
    size_t slash = path.find_last_of("/\\");
    return slash == std::string::npos ? path : path.substr(slash + 1);
}

// a file next to the glTF: the glTF's folder + the relative uri
inline std::string ModelJoin(const std::string& gltfPath, const std::string& rel)
{
    size_t slash = gltfPath.find_last_of("/\\");
    return slash == std::string::npos ? rel : gltfPath.substr(0, slash + 1) + rel;
}

// where the bytes come from: the file system, or (Android) the app's assets/ folder through AAssetManager
struct ModelFiles
{
    AAssetManager* assets = nullptr;
    bool fromAssets = false;
};

inline bool ModelReadBytes(const ModelFiles& files, const std::string& path, std::vector<uint8_t>& out, std::string& err)
{
    out.clear();
    if (files.fromAssets)
    {
        if (!files.assets)
        {
            err = "no asset manager (the app's activity has none)";
            return false;
        }
#if defined(__ANDROID__)
        AAssetManager* assets = files.assets;
        AAsset* a = AAssetManager_open(assets, path.c_str(), AASSET_MODE_BUFFER);
        if (!a)
        {
            err = "not in the app's assets/ folder";
            return false;
        }
        const off_t n = AAsset_getLength(a);
        const void* p = AAsset_getBuffer(a);
        if (n > 0 && p) out.assign((const uint8_t*)p, (const uint8_t*)p + n);
        AAsset_close(a);
        if (n > 0 && !p)
        {
            err = "asset has no buffer";
            return false;
        }
        return true;
#else
        err = "assets need Android (use a file path on this host)";
        return false;
#endif
    }
    FILE* f = std::fopen(path.c_str(), "rb");
    if (!f)
    {
        err = "cannot open the file";
        return false;
    }
    std::fseek(f, 0, SEEK_END);
    const long n = std::ftell(f);
    std::fseek(f, 0, SEEK_SET);
    if (n < 0)
    {
        std::fclose(f);
        err = "cannot read the file size";
        return false;
    }
    out.resize((size_t)n);
    const size_t got = n > 0 ? std::fread(out.data(), 1, (size_t)n, f) : 0;
    std::fclose(f);
    if (got != (size_t)n)
    {
        err = "short read";
        return false;
    }
    return true;
}

// cgltf's file callbacks: every external buffer goes through the same reader as the glTF file itself
inline cgltf_result ModelCgltfRead(const cgltf_memory_options*, const cgltf_file_options* fo, const char* path,
                                   cgltf_size* size, void** data)
{
    const ModelFiles* files = (const ModelFiles*)fo->user_data;
    std::vector<uint8_t> bytes;
    std::string err;
    if (!files || !ModelReadBytes(*files, path, bytes, err)) return cgltf_result_file_not_found;
    // the buffer's declared byteLength is what cgltf checks against: a shorter file must not pass
    if (size && *size > bytes.size()) return cgltf_result_data_too_short;
    void* copy = std::malloc(bytes.empty() ? 1 : bytes.size());
    if (!copy) return cgltf_result_out_of_memory;
    if (!bytes.empty()) std::memcpy(copy, bytes.data(), bytes.size());
    *data = copy;
    if (size) *size = bytes.size();
    return cgltf_result_success;
}

inline void ModelCgltfRelease(const cgltf_memory_options*, const cgltf_file_options*, void* data) { std::free(data); }

inline const char* ModelCgltfError(cgltf_result r)
{
    switch (r)
    {
    case cgltf_result_data_too_short: return "file too short";
    case cgltf_result_unknown_format: return "not a glTF or GLB file";
    case cgltf_result_invalid_json: return "invalid JSON";
    case cgltf_result_invalid_gltf: return "invalid glTF";
    case cgltf_result_file_not_found: return "a buffer file is missing";
    case cgltf_result_io_error: return "read error";
    case cgltf_result_out_of_memory: return "out of memory";
    case cgltf_result_legacy_gltf: return "glTF 1.0 is not supported";
    default: return "glTF error";
    }
}

// one glTF file -> ModelData. Keeps the cgltf data only for the duration of the call.
struct ModelParser
{
    const cgltf_data* data = nullptr;
    cgltf_options opt;
    const ModelFiles* files = nullptr;
    std::string path;                    // the glTF file or asset name (base of relative uris)
    ModelData* out = nullptr;
    std::vector<int> imageOf;            // cgltf image -> ModelData::images, -2 not tried, -1 failed
    std::vector<int> textureOf;          // cgltf texture -> ModelData::textures, -1 not yet
    std::vector<int> materialOf;         // cgltf material -> ModelData::materials, -1 not yet
    std::string err;

    int Image(const cgltf_image* img)
    {
        const size_t idx = cgltf_image_index(data, img);
        if (imageOf[idx] != -2) return imageOf[idx];
        imageOf[idx] = -1;
        ModelImage im;
        std::string why;
        if (DecodeImage(img, im, why))
        {
            if (im.width > 1024 || im.height > 1024)
                mcx::Log("model %s: texture %dx%d is over 1024 px: resize it for Quest", out->name.c_str(), im.width,
                         im.height);
            out->images.push_back(std::move(im));
            imageOf[idx] = (int)out->images.size() - 1;
        }
        else
        {
            mcx::Log("model %s: image %zu not loaded (%s): drawn white", out->name.c_str(), idx, why.c_str());
        }
        return imageOf[idx];
    }

    int Texture(const cgltf_texture* tex)
    {
        const size_t idx = cgltf_texture_index(data, tex);
        if (textureOf[idx] >= 0) return textureOf[idx];
        ModelTexture t;
        t.image = tex->image ? Image(tex->image) : -1;
        if (tex->sampler)
        {
            t.clampS = tex->sampler->wrap_s == cgltf_wrap_mode_clamp_to_edge;
            t.clampT = tex->sampler->wrap_t == cgltf_wrap_mode_clamp_to_edge;
        }
        out->textures.push_back(t);
        textureOf[idx] = (int)out->textures.size() - 1;
        return textureOf[idx];
    }

    int Material(const cgltf_material* mat)
    {
        const size_t idx = cgltf_material_index(data, mat);
        if (materialOf[idx] >= 0) return materialOf[idx];
        ModelMaterial m;
        if (mat->has_pbr_metallic_roughness)
        {
            const cgltf_pbr_metallic_roughness& p = mat->pbr_metallic_roughness;
            for (int k = 0; k < 4; k++) m.baseColor[k] = p.base_color_factor[k];
            m.metallic = p.metallic_factor;
            m.roughness = p.roughness_factor;
            if (p.base_color_texture.texture) m.texture = Texture(p.base_color_texture.texture);
        }
        const float strength = mat->has_emissive_strength ? mat->emissive_strength.emissive_strength : 1.f;
        for (int k = 0; k < 3; k++) m.emissive[k] = mat->emissive_factor[k] * strength;
        m.alpha = mat->alpha_mode == cgltf_alpha_mode_mask    ? ModelAlpha::Mask
                  : mat->alpha_mode == cgltf_alpha_mode_blend ? ModelAlpha::Blend
                                                              : ModelAlpha::Opaque;
        m.alphaCutoff = mat->alpha_cutoff;
        m.doubleSided = mat->double_sided != 0;
        m.unlit = mat->unlit != 0;
        out->materials.push_back(m);
        materialOf[idx] = (int)out->materials.size() - 1;
        return materialOf[idx];
    }

    void AddPrimitive(const cgltf_primitive* prim, const float world[16])
    {
        if (prim->type != cgltf_primitive_type_triangles && prim->type != cgltf_primitive_type_triangle_strip &&
            prim->type != cgltf_primitive_type_triangle_fan)
        {
            // cgltf's enum starts at 1 for points; glTF's mode number starts at 0
            mcx::Log("model %s: a primitive that is not triangles is skipped (glTF mode %d)", out->name.c_str(),
                     (int)prim->type - 1);
            return;
        }
        const cgltf_accessor *pos = nullptr, *nrm = nullptr, *uv = nullptr;
        for (cgltf_size a = 0; a < prim->attributes_count; a++)
        {
            const cgltf_attribute& at = prim->attributes[a];
            if (at.type == cgltf_attribute_type_position && !pos) pos = at.data;
            else if (at.type == cgltf_attribute_type_normal && !nrm) nrm = at.data;
            else if (at.type == cgltf_attribute_type_texcoord && at.index == 0 && !uv) uv = at.data;
        }
        if (prim->has_draco_mesh_compression)
        {
            mcx::Log("model %s: a Draco-compressed primitive is skipped (not supported)", out->name.c_str());
            return;
        }
        if (!pos || pos->type != cgltf_type_vec3 || pos->count == 0)
        {
            mcx::Log("model %s: a primitive without POSITION is skipped", out->name.c_str());
            return;
        }
        const size_t n = pos->count;
        std::vector<float> p(n * 3), nn, tt;
        if (cgltf_accessor_unpack_floats(pos, p.data(), n * 3) != n * 3) return;
        if (nrm && nrm->type == cgltf_type_vec3 && nrm->count == n)
        {
            nn.resize(n * 3);
            if (cgltf_accessor_unpack_floats(nrm, nn.data(), n * 3) != n * 3) nn.clear();
        }
        if (uv && uv->type == cgltf_type_vec2 && uv->count == n)
        {
            tt.resize(n * 2);
            if (cgltf_accessor_unpack_floats(uv, tt.data(), n * 2) != n * 2) tt.clear();
        }

        // indices as they are in the file (or 0..n-1), then the triangle list for the mode
        std::vector<uint32_t> ids;
        if (prim->indices)
        {
            const size_t k = prim->indices->count;
            ids.resize(k);
            if (cgltf_accessor_unpack_indices(prim->indices, ids.data(), sizeof(uint32_t), k) != k)
                for (size_t i = 0; i < k; i++) ids[i] = (uint32_t)cgltf_accessor_read_index(prim->indices, i);
        }
        else
        {
            ids.resize(n);
            for (size_t i = 0; i < n; i++) ids[i] = (uint32_t)i;
        }
        std::vector<uint32_t> tri;
        const size_t k = ids.size();
        if (prim->type == cgltf_primitive_type_triangles)
        {
            tri.assign(ids.begin(), ids.begin() + (k / 3) * 3);
        }
        else if (prim->type == cgltf_primitive_type_triangle_strip)
        {
            for (size_t i = 2; i < k; i++)
            {
                if (i % 2 == 0) tri.insert(tri.end(), {ids[i - 2], ids[i - 1], ids[i]});
                else tri.insert(tri.end(), {ids[i - 1], ids[i - 2], ids[i]});
            }
        }
        else
        {
            for (size_t i = 2; i < k; i++) tri.insert(tri.end(), {ids[0], ids[i - 1], ids[i]});
        }
        size_t bad = 0;
        for (size_t i = 0; i < tri.size(); i++)
            if (tri[i] >= n) bad++;
        if (bad)
        {
            mcx::Log("model %s: %zu index(es) out of range: those triangles are dropped", out->name.c_str(), bad);
            std::vector<uint32_t> ok;
            for (size_t t = 0; t + 2 < tri.size(); t += 3)
                if (tri[t] < n && tri[t + 1] < n && tri[t + 2] < n) ok.insert(ok.end(), {tri[t], tri[t + 1], tri[t + 2]});
            tri.swap(ok);
        }
        if (tri.empty()) return;

        ModelPrimitive mp;
        std::memcpy(mp.world, world, sizeof mp.world);
        mp.material = prim->material ? Material(prim->material) : -1;
        const size_t tris = tri.size() / 3;
        if (!nn.empty())
        {
            mp.vertices.resize(n * 8);
            for (size_t i = 0; i < n; i++)
            {
                float* v = &mp.vertices[i * 8];
                std::memcpy(v, &p[i * 3], 3 * sizeof(float));
                std::memcpy(v + 3, &nn[i * 3], 3 * sizeof(float));
                v[6] = tt.empty() ? 0.f : tt[i * 2];
                v[7] = tt.empty() ? 0.f : tt[i * 2 + 1];
            }
            mp.indices = tri;
        }
        else
        {
            // no normals: flat shading. Each triangle gets its own three vertices with its face normal.
            mp.vertices.resize(tris * 3 * 8);
            mp.indices.resize(tris * 3);
            for (size_t t = 0; t < tris; t++)
            {
                const uint32_t i0 = tri[t * 3], i1 = tri[t * 3 + 1], i2 = tri[t * 3 + 2];
                const float* a = &p[i0 * 3];
                const float* b = &p[i1 * 3];
                const float* c = &p[i2 * 3];
                const float e1[3] = {b[0] - a[0], b[1] - a[1], b[2] - a[2]};
                const float e2[3] = {c[0] - a[0], c[1] - a[1], c[2] - a[2]};
                float fn[3] = {e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0]};
                const float len = std::sqrt(fn[0] * fn[0] + fn[1] * fn[1] + fn[2] * fn[2]);
                if (len > 1e-20f)
                {
                    fn[0] /= len; fn[1] /= len; fn[2] /= len;
                }
                else
                {
                    fn[0] = 0.f; fn[1] = 1.f; fn[2] = 0.f;
                }
                for (int corner = 0; corner < 3; corner++)
                {
                    const uint32_t src = tri[t * 3 + corner];
                    float* v = &mp.vertices[(t * 3 + corner) * 8];
                    std::memcpy(v, &p[src * 3], 3 * sizeof(float));
                    std::memcpy(v + 3, fn, 3 * sizeof(float));
                    v[6] = tt.empty() ? 0.f : tt[src * 2];
                    v[7] = tt.empty() ? 0.f : tt[src * 2 + 1];
                    mp.indices[t * 3 + corner] = (uint32_t)(t * 3 + corner);
                }
            }
        }
        for (size_t i = 0; i < mp.vertices.size(); i += 8)
        {
            float q[3], r[3] = {0, 0, 0};
            std::memcpy(q, &mp.vertices[i], 3 * sizeof(float));
            ModelPoint(world, q, r);
            ModelExtend(out->bounds, r);
        }
        out->primitives.push_back(std::move(mp));
    }

    void Visit(const cgltf_node* node, int depth)
    {
        if (!node || depth > 64) return; // 64: a cycle in a broken file stops here
        if (node->mesh)
        {
            float world[16];
            if (node->skin)
            {
                // skinned: the mesh node's transform is ignored (glTF); the rest pose is drawn as stored
                const Mat4 id = ModelIdentity();
                std::memcpy(world, id.m, sizeof world);
            }
            else
            {
                cgltf_node_transform_world(node, world);
            }
            for (cgltf_size i = 0; i < node->mesh->primitives_count; i++) AddPrimitive(&node->mesh->primitives[i], world);
        }
        for (cgltf_size c = 0; c < node->children_count; c++) Visit(node->children[c], depth + 1);
    }

    bool Run()
    {
        if (data->scene)
            for (cgltf_size i = 0; i < data->scene->nodes_count; i++) Visit(data->scene->nodes[i], 0);
        else if (data->scenes_count)
            for (cgltf_size i = 0; i < data->scenes[0].nodes_count; i++) Visit(data->scenes[0].nodes[i], 0);
        else
            for (cgltf_size i = 0; i < data->nodes_count; i++)
                if (!data->nodes[i].parent) Visit(&data->nodes[i], 0);
        if (out->primitives.empty())
        {
            err = "no triangles to draw";
            return false;
        }
        return true;
    }

    bool DecodeImage(const cgltf_image* img, ModelImage& im, std::string& why)
    {
        std::vector<uint8_t> file;      // an external file's bytes
        void* decoded = nullptr;        // base64 bytes (free with free)
        const uint8_t* bytes = nullptr;
        size_t size = 0;
        bool ok = true;
        if (img->buffer_view)
        {
            bytes = cgltf_buffer_view_data(img->buffer_view);
            size = img->buffer_view->size;
            if (!bytes)
            {
                ok = false;
                why = "image bytes are not loaded";
            }
        }
        else if (img->uri && std::strncmp(img->uri, "data:", 5) == 0)
        {
            const char* comma = std::strchr(img->uri, ',');
            if (!comma || comma - img->uri < 7 || std::strncmp(comma - 7, ";base64", 7) != 0)
            {
                ok = false;
                why = "data URI is not base64";
            }
            else
            {
                const char* b64 = comma + 1;
                size_t len = std::strlen(b64);
                while (len > 0 && b64[len - 1] == '=') len--;
                size = len * 3 / 4;
                if (size == 0 || cgltf_load_buffer_base64(&opt, size, b64, &decoded) != cgltf_result_success)
                {
                    ok = false;
                    why = "data URI is not valid base64";
                }
                else
                {
                    bytes = (const uint8_t*)decoded;
                }
            }
        }
        else if (img->uri)
        {
            std::vector<char> buf(std::strlen(img->uri) + 1);
            std::memcpy(buf.data(), img->uri, buf.size());
            cgltf_decode_uri(buf.data()); // %20 and friends
            const std::string full = ModelJoin(path, std::string(buf.data()));
            if (!ModelReadBytes(*files, full, file, why)) ok = false;
            bytes = file.data();
            size = file.size();
        }
        else
        {
            ok = false;
            why = "image has no data";
        }
        if (ok)
        {
            int w = 0, h = 0, comp = 0;
            stbi_uc* px = size ? stbi_load_from_memory(bytes, (int)size, &w, &h, &comp, 4) : nullptr;
            if (!px)
            {
                const char* reason = stbi_failure_reason();
                why = reason ? reason : "not decoded";
                ok = false;
            }
            else
            {
                im.width = w;
                im.height = h;
                im.rgba.assign(px, px + (size_t)w * (size_t)h * 4);
                stbi_image_free(px);
            }
        }
        std::free(decoded);
        return ok;
    }
};

// the whole path: read, parse, buffers, meshes. Errors are logged with the file name and returned.
inline bool ModelParseFile(const std::string& path, const ModelFiles& files, ModelData& out, std::string* error)
{
    out = ModelData();
    out.name = ModelBaseName(path);
    std::string err;
    std::vector<uint8_t> bytes;
    cgltf_data* data = nullptr;
    ModelParser pr;
    bool ok = ModelReadBytes(files, path, bytes, err);
    if (ok)
    {
        std::memset(&pr.opt, 0, sizeof pr.opt);
        pr.opt.file.read = &ModelCgltfRead;
        pr.opt.file.release = &ModelCgltfRelease;
        pr.opt.file.user_data = (void*)&files;
        const cgltf_result r = cgltf_parse(&pr.opt, bytes.data(), bytes.size(), &data);
        if (r != cgltf_result_success)
        {
            err = ModelCgltfError(r);
            ok = false;
        }
        else
        {
            const cgltf_result b = cgltf_load_buffers(&pr.opt, data, path.c_str());
            if (b != cgltf_result_success)
            {
                err = std::string("buffers: ") + ModelCgltfError(b);
                ok = false;
            }
            else
            {
                // extents of every accessor and buffer view inside its buffer (no reads past the data)
                const cgltf_result v = cgltf_validate(data);
                for (cgltf_size i = 0; ok && i < data->buffer_views_count; i++)
                    if (data->buffer_views[i].has_meshopt_compression)
                    {
                        err = "meshopt compression is not supported";
                        ok = false;
                    }
                if (ok && v != cgltf_result_success)
                {
                    err = std::string("invalid glTF data: ") + ModelCgltfError(v);
                    ok = false;
                }
            }
        }
    }
    if (ok)
    {
        pr.data = data;
        pr.files = &files;
        pr.path = path;
        pr.out = &out;
        pr.imageOf.assign(data->images_count, -2);
        pr.textureOf.assign(data->textures_count, -1);
        pr.materialOf.assign(data->materials_count, -1);
        ok = pr.Run();
        if (!ok) err = pr.err;
    }
    if (data) cgltf_free(data);
    if (!ok)
    {
        mcx::Log("model %s: %s", out.name.c_str(), err.c_str());
        if (error) *error = err;
        return false;
    }
    if (error) error->clear();
    return true;
}

// the fit matrix: the model's height becomes `heightMetres`, its lowest point y = 0, centred on x = z = 0
inline Mat4 ModelFitMatrix(const ModelBounds& b, float heightMetres)
{
    if (!b.valid || b.Size(1) <= 0.f || heightMetres <= 0.f) return ModelIdentity();
    const float s = heightMetres / b.Size(1);
    return Mat4::Translation(-b.Centre(0) * s, -b.min[1] * s, -b.Centre(2) * s) * Mat4::Scale(s, s, s);
}

// the built-in lit shader (GLSL ES 3.00). Per eye: uViewProj, uEye. Per primitive: uModel, uNormal, material.
inline const char* const kModelVertexSource = R"(#version 300 es
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUv;
uniform mat4 uViewProj;
uniform mat4 uModel;
uniform mat3 uNormal;
out highp vec3 vWorld;
out mediump vec3 vNormal;
out mediump vec2 vUv;
void main() {
    vec4 w = uModel * vec4(aPos, 1.0);
    vWorld = w.xyz;
    vNormal = uNormal * aNormal;
    vUv = aUv;
    gl_Position = uViewProj * w;
}
)";

inline const char* const kModelFragmentSource = R"(#version 300 es
precision highp float;
precision highp int;
in highp vec3 vWorld;
in mediump vec3 vNormal;
in mediump vec2 vUv;
uniform sampler2D uBase;
uniform vec4 uBaseColor;
uniform vec3 uEmissive;
uniform float uMetallic;
uniform float uRoughness;
uniform int uAlphaMode;   // 0 opaque, 1 mask, 2 blend
uniform float uCutoff;
uniform int uUnlit;
uniform vec3 uEye;
uniform vec3 uLightDir;
uniform vec3 uLightColor;
uniform vec3 uSky;
uniform vec3 uGround;
out vec4 fragColor;
void main() {
    vec4 base = uBaseColor * texture(uBase, vUv);
    if (uAlphaMode == 1 && base.a < uCutoff) discard;
    vec3 colour;
    if (uUnlit == 1) {
        colour = base.rgb;
    } else {
        vec3 n = normalize(vNormal);
        vec3 l = normalize(uLightDir);
        vec3 v = normalize(uEye - vWorld);
        vec3 h = normalize(l + v);
        float ndl = max(dot(n, l), 0.0);
        float shin = mix(96.0, 6.0, uRoughness);
        float spec = ndl > 0.0 ? pow(max(dot(n, h), 0.0), shin) * (shin + 8.0) / 25.13 : 0.0;
        vec3 ambient = mix(uGround, uSky, 0.5 + 0.5 * n.y);
        vec3 f0 = mix(vec3(0.04), base.rgb, uMetallic);
        vec3 diffuse = base.rgb * (1.0 - uMetallic);
        colour = diffuse * (ambient + uLightColor * ndl) + f0 * uLightColor * spec;
    }
    colour += uEmissive;
    fragColor = vec4(colour, uAlphaMode == 2 ? base.a : 1.0);
}
)";

inline GLuint ModelCompileShader(GLenum type, const char* src)
{
    GLuint s = glCreateShader(type);
    glShaderSource(s, 1, &src, nullptr);
    glCompileShader(s);
    GLint ok = GL_FALSE;
    glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
    if (ok != GL_TRUE)
    {
        char log[512] = {0};
        glGetShaderInfoLog(s, sizeof log, nullptr, log);
        mcx::Log("model shader: %s", log);
        glDeleteShader(s);
        return 0;
    }
    return s;
}

} // namespace detail

// ---------------------------------------------------------------- CPU loading (no GL needed)
// A glTF file by path (.glb, or .gltf with its buffers and images next to it). Returns false and logs the file name.
inline bool ModelReadFile(const char* path, ModelData& out, std::string* error = nullptr)
{
    detail::ModelFiles files;
    return detail::ModelParseFile(path, files, out, error);
}

// A glTF from the app's assets/ folder (mcx-build packs <app>/assets/ into the APK). `name` is relative to assets/,
// for example "models/chair.glb". Android only: other hosts report an error.
inline bool ModelReadAsset(AAssetManager* assets, const char* name, ModelData& out, std::string* error = nullptr)
{
    detail::ModelFiles files;
    files.assets = assets;
    files.fromAssets = true;
    return detail::ModelParseFile(name, files, out, error);
}

// the fit matrix for a height (see Model::FitHeight)
inline Mat4 ModelFitHeight(const ModelBounds& b, float heightMetres) { return detail::ModelFitMatrix(b, heightMetres); }

// ---------------------------------------------------------------- GPU model
class ModelRenderer;

class Model
{
public:
    Model() = default;
    Model(const Model&) = delete;             // owns GL objects: call Destroy()
    Model& operator=(const Model&) = delete;

    // GL objects from a file or an asset (a CPU load then Upload). Needs the GL context. Returns false on error.
    bool Load(const char* path);
    bool LoadAsset(AAssetManager* assets, const char* name);
    bool Upload(const ModelData& data);

    // the model's own transform (a fit, a flip). Applied before the world matrix that Draw gets.
    void SetRoot(const Mat4& m);
    // scale so the model is `metres` tall, lowest point on y = 0, centred on x and z = 0 (then Draw's world places it)
    void FitHeight(float metres);
    const ModelBounds& Bounds() const { return bounds_; } // after SetRoot, metres

    bool Loaded() const { return loaded_; }
    const std::string& Name() const { return name_; }
    const std::string& Error() const { return error_; }
    int Primitives() const { return (int)parts_.size(); } // draw calls per Draw
    int Triangles() const { return triangles_; }
    void Destroy();                                       // frees GL objects and CPU copies

private:
    struct Part
    {
        GLuint vao = 0, vbo = 0, ibo = 0;
        GLsizei count = 0;                // indices
        GLuint texture = 0;               // 0: none
        ModelMaterial mat;
        Mat4 world, pre;                  // pre = root * world
        float normal[9];                  // normal matrix of pre
    };
    void Recompute();
    void Release();
    std::vector<Part> parts_;
    std::vector<GLuint> textures_;
    Mat4 root_ = detail::ModelIdentity();
    ModelBounds raw_, bounds_;
    std::string name_, error_;
    int triangles_ = 0;
    bool loaded_ = false;
    friend class ModelRenderer;
};

inline bool Model::Load(const char* path)
{
    ModelData d;
    if (!ModelReadFile(path, d, &error_))
    {
        loaded_ = false;
        return false;
    }
    return Upload(d);
}

inline bool Model::LoadAsset(AAssetManager* assets, const char* name)
{
    ModelData d;
    if (!ModelReadAsset(assets, name, d, &error_))
    {
        loaded_ = false;
        return false;
    }
    return Upload(d);
}

inline bool Model::Upload(const ModelData& data)
{
    Release();
    name_ = data.name;
    raw_ = data.bounds;
    textures_.assign(data.textures.size(), 0);
    for (size_t t = 0; t < data.textures.size(); t++)
    {
        const ModelTexture& tx = data.textures[t];
        if (tx.image < 0 || (size_t)tx.image >= data.images.size()) continue;
        const ModelImage& im = data.images[tx.image];
        if (im.width <= 0 || im.height <= 0 || im.rgba.size() != (size_t)im.width * (size_t)im.height * 4) continue;
        glGenTextures(1, &textures_[t]);
        glBindTexture(GL_TEXTURE_2D, textures_[t]);
        glTexImage2D(GL_TEXTURE_2D, 0, GL_SRGB8_ALPHA8, im.width, im.height, 0, GL_RGBA, GL_UNSIGNED_BYTE, im.rgba.data());
        glGenerateMipmap(GL_TEXTURE_2D);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR_MIPMAP_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, tx.clampS ? GL_CLAMP_TO_EDGE : GL_REPEAT);
        glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, tx.clampT ? GL_CLAMP_TO_EDGE : GL_REPEAT);
    }
    glBindTexture(GL_TEXTURE_2D, 0);

    for (const ModelPrimitive& p : data.primitives)
    {
        if (p.indices.empty() || p.vertices.empty()) continue;
        Part part;
        std::memcpy(part.world.m, p.world, sizeof part.world.m);
        if (p.material >= 0 && (size_t)p.material < data.materials.size()) part.mat = data.materials[p.material];
        if (part.mat.texture >= 0 && (size_t)part.mat.texture < textures_.size()) part.texture = textures_[part.mat.texture];
        part.count = (GLsizei)p.indices.size();
        glGenVertexArrays(1, &part.vao);
        glGenBuffers(1, &part.vbo);
        glGenBuffers(1, &part.ibo);
        glBindVertexArray(part.vao);
        glBindBuffer(GL_ARRAY_BUFFER, part.vbo);
        glBufferData(GL_ARRAY_BUFFER, (GLsizeiptr)(p.vertices.size() * sizeof(float)), p.vertices.data(), GL_STATIC_DRAW);
        glEnableVertexAttribArray(0);
        glVertexAttribPointer(0, 3, GL_FLOAT, GL_FALSE, 32, (const void*)0);
        glEnableVertexAttribArray(1);
        glVertexAttribPointer(1, 3, GL_FLOAT, GL_FALSE, 32, (const void*)12);
        glEnableVertexAttribArray(2);
        glVertexAttribPointer(2, 2, GL_FLOAT, GL_FALSE, 32, (const void*)24);
        glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, part.ibo);
        glBufferData(GL_ELEMENT_ARRAY_BUFFER, (GLsizeiptr)(p.indices.size() * sizeof(uint32_t)), p.indices.data(), GL_STATIC_DRAW);
        glBindVertexArray(0);
        glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, 0);
        glBindBuffer(GL_ARRAY_BUFFER, 0);
        parts_.push_back(part);
    }
    triangles_ = data.Triangles();
    Recompute();
    loaded_ = !parts_.empty();
    if (!loaded_)
    {
        error_ = "no triangles on the GPU";
        mcx::Log("model %s: %s", name_.c_str(), error_.c_str());
    }
    else
    {
        error_.clear();
    }
    return loaded_;
}

inline void Model::SetRoot(const Mat4& m)
{
    root_ = m;
    Recompute();
}

inline void Model::FitHeight(float metres) { SetRoot(detail::ModelFitMatrix(raw_, metres)); }

inline void Model::Recompute()
{
    for (Part& p : parts_)
    {
        p.pre = root_ * p.world;
        detail::ModelNormalMatrix(p.pre.m, p.normal);
    }
    bounds_ = ModelBounds();
    if (!raw_.valid) return;
    for (int corner = 0; corner < 8; corner++)
    {
        const float q[3] = {(corner & 1) ? raw_.max[0] : raw_.min[0], (corner & 2) ? raw_.max[1] : raw_.min[1],
                            (corner & 4) ? raw_.max[2] : raw_.min[2]};
        float r[3];
        detail::ModelPoint(root_.m, q, r);
        detail::ModelExtend(bounds_, r);
    }
}

// frees the GL objects and the bounds; the root transform stays (Upload keeps the fit of a reloaded model)
inline void Model::Release()
{
    for (Part& p : parts_)
    {
        if (p.vao) glDeleteVertexArrays(1, &p.vao);
        if (p.vbo) glDeleteBuffers(1, &p.vbo);
        if (p.ibo) glDeleteBuffers(1, &p.ibo);
    }
    parts_.clear();
    for (GLuint t : textures_)
        if (t) glDeleteTextures(1, &t);
    textures_.clear();
    raw_ = ModelBounds();
    bounds_ = ModelBounds();
    triangles_ = 0;
    loaded_ = false;
}

inline void Model::Destroy()
{
    Release();
    root_ = detail::ModelIdentity();
}

// ---------------------------------------------------------------- the renderer (one per app)
class ModelRenderer
{
public:
    ModelLight light;

    bool Init();                                                    // compiles the shader; needs the GL context
    void SetLight(const XrColor4f& sky, const XrColor4f& ground, const XrColor4f& sun, const XrVector3f& towardsLight);
    // draws every primitive of `m`, placed by `world` (metres; rigid or uniform scale only), with the eye's view
    void Draw(const Eye& eye, const Model& m, const Mat4& world);
    bool Ready() const { return prog_ != 0; }
    void Destroy();

private:
    GLuint prog_ = 0, white_ = 0;
    GLint uViewProj_ = -1, uEye_ = -1, uModel_ = -1, uNormal_ = -1, uLightDir_ = -1, uLightColor_ = -1, uSky_ = -1,
          uGround_ = -1, uBaseColor_ = -1, uEmissive_ = -1, uMetallic_ = -1, uRoughness_ = -1, uAlphaMode_ = -1,
          uCutoff_ = -1, uUnlit_ = -1;
};

inline bool ModelRenderer::Init()
{
    Destroy();
    const GLuint vs = detail::ModelCompileShader(GL_VERTEX_SHADER, detail::kModelVertexSource);
    const GLuint fs = detail::ModelCompileShader(GL_FRAGMENT_SHADER, detail::kModelFragmentSource);
    if (!vs || !fs)
    {
        if (vs) glDeleteShader(vs);
        if (fs) glDeleteShader(fs);
        return false;
    }
    prog_ = glCreateProgram();
    glAttachShader(prog_, vs);
    glAttachShader(prog_, fs);
    glLinkProgram(prog_);
    glDeleteShader(vs);
    glDeleteShader(fs);
    GLint linked = GL_FALSE;
    glGetProgramiv(prog_, GL_LINK_STATUS, &linked);
    if (linked != GL_TRUE)
    {
        char log[512] = {0};
        glGetProgramInfoLog(prog_, sizeof log, nullptr, log);
        mcx::Log("model program: %s", log);
        glDeleteProgram(prog_);
        prog_ = 0;
        return false;
    }
    uViewProj_ = glGetUniformLocation(prog_, "uViewProj");
    uEye_ = glGetUniformLocation(prog_, "uEye");
    uModel_ = glGetUniformLocation(prog_, "uModel");
    uNormal_ = glGetUniformLocation(prog_, "uNormal");
    uLightDir_ = glGetUniformLocation(prog_, "uLightDir");
    uLightColor_ = glGetUniformLocation(prog_, "uLightColor");
    uSky_ = glGetUniformLocation(prog_, "uSky");
    uGround_ = glGetUniformLocation(prog_, "uGround");
    uBaseColor_ = glGetUniformLocation(prog_, "uBaseColor");
    uEmissive_ = glGetUniformLocation(prog_, "uEmissive");
    uMetallic_ = glGetUniformLocation(prog_, "uMetallic");
    uRoughness_ = glGetUniformLocation(prog_, "uRoughness");
    uAlphaMode_ = glGetUniformLocation(prog_, "uAlphaMode");
    uCutoff_ = glGetUniformLocation(prog_, "uCutoff");
    uUnlit_ = glGetUniformLocation(prog_, "uUnlit");
    glUseProgram(prog_);
    glUniform1i(glGetUniformLocation(prog_, "uBase"), 0);
    glUseProgram(0);

    // a 1x1 white texture for primitives without a base colour texture (MIN_FILTER must not need mips)
    glGenTextures(1, &white_);
    glBindTexture(GL_TEXTURE_2D, white_);
    const uint8_t white[4] = {255, 255, 255, 255};
    glTexImage2D(GL_TEXTURE_2D, 0, GL_SRGB8_ALPHA8, 1, 1, 0, GL_RGBA, GL_UNSIGNED_BYTE, white);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glBindTexture(GL_TEXTURE_2D, 0);
    return true;
}

inline void ModelRenderer::SetLight(const XrColor4f& sky, const XrColor4f& ground, const XrColor4f& sun,
                                    const XrVector3f& towardsLight)
{
    light.sky[0] = sky.r; light.sky[1] = sky.g; light.sky[2] = sky.b;
    light.ground[0] = ground.r; light.ground[1] = ground.g; light.ground[2] = ground.b;
    light.color[0] = sun.r; light.color[1] = sun.g; light.color[2] = sun.b;
    light.dir[0] = towardsLight.x; light.dir[1] = towardsLight.y; light.dir[2] = towardsLight.z;
}

inline void ModelRenderer::Draw(const Eye& eye, const Model& m, const Mat4& world)
{
    if (!prog_ || !m.loaded_) return;
    glUseProgram(prog_);
    glEnable(GL_DEPTH_TEST);
    glDepthFunc(GL_LESS);
    glDepthMask(GL_TRUE);
    glUniformMatrix4fv(uViewProj_, 1, GL_FALSE, eye.viewProj.m);
    glUniform3f(uEye_, eye.pose.position.x, eye.pose.position.y, eye.pose.position.z);
    glUniform3f(uLightDir_, light.dir[0], light.dir[1], light.dir[2]);
    glUniform3f(uLightColor_, light.color[0], light.color[1], light.color[2]);
    glUniform3f(uSky_, light.sky[0], light.sky[1], light.sky[2]);
    glUniform3f(uGround_, light.ground[0], light.ground[1], light.ground[2]);
    const float w3[9] = {world.m[0], world.m[1], world.m[2], world.m[4], world.m[5],
                         world.m[6], world.m[8], world.m[9], world.m[10]};
    for (int pass = 0; pass < 2; pass++) // pass 0: opaque and mask; pass 1: blend (no depth write)
    {
        if (pass == 1)
        {
            glEnable(GL_BLEND);
            glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
            glDepthMask(GL_FALSE);
        }
        for (const Model::Part& p : m.parts_)
        {
            const bool blend = p.mat.alpha == ModelAlpha::Blend;
            if (blend != (pass == 1)) continue;
            const Mat4 model = world * p.pre;
            float n3[9];
            detail::ModelMul3(w3, p.normal, n3);
            glUniformMatrix4fv(uModel_, 1, GL_FALSE, model.m);
            glUniformMatrix3fv(uNormal_, 1, GL_FALSE, n3);
            glUniform4f(uBaseColor_, p.mat.baseColor[0], p.mat.baseColor[1], p.mat.baseColor[2], p.mat.baseColor[3]);
            glUniform3f(uEmissive_, p.mat.emissive[0], p.mat.emissive[1], p.mat.emissive[2]);
            glUniform1f(uMetallic_, p.mat.metallic);
            glUniform1f(uRoughness_, p.mat.roughness);
            glUniform1i(uAlphaMode_, p.mat.alpha == ModelAlpha::Mask ? 1 : (blend ? 2 : 0));
            glUniform1f(uCutoff_, p.mat.alphaCutoff);
            glUniform1i(uUnlit_, p.mat.unlit ? 1 : 0);
            if (p.mat.doubleSided)
            {
                glDisable(GL_CULL_FACE);
            }
            else
            {
                glEnable(GL_CULL_FACE);
                glCullFace(GL_BACK);
            }
            glActiveTexture(GL_TEXTURE0);
            glBindTexture(GL_TEXTURE_2D, p.texture ? p.texture : white_);
            glBindVertexArray(p.vao);
            glDrawElements(GL_TRIANGLES, p.count, GL_UNSIGNED_INT, nullptr);
        }
        if (pass == 1)
        {
            glDisable(GL_BLEND);
            glDepthMask(GL_TRUE);
        }
    }
    glBindVertexArray(0);
    glBindTexture(GL_TEXTURE_2D, 0);
    glDisable(GL_CULL_FACE);
    glUseProgram(0);
}

inline void ModelRenderer::Destroy()
{
    if (prog_) glDeleteProgram(prog_);
    if (white_) glDeleteTextures(1, &white_);
    prog_ = 0;
    white_ = 0;
}

} // namespace mcx
