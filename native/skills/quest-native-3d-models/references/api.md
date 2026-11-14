# mcx_model.h API, name by name

All names are in namespace `mcx`. Header `mcx_model.h`, which includes `third_party/cgltf.h` and `third_party/stb_image.h`
from its own folder. Units: metres, Y up. Colours: linear (use `ModelSrgb` for hex colours).

## Macro

- `MCX_MODEL_IMPLEMENTATION`: define it in exactly one .cpp, before the first include of mcx_model.h. That .cpp compiles cgltf and stb_image.

## Types

- `ModelAlpha`: enum class with `Opaque`, `Mask` (cut out below the cutoff) and `Blend` (see-through, drawn last).
- `ModelBounds`: `valid`, `min[3]` and `max[3]` in metres, `Size(axis)` and `Centre(axis)`.
- `ModelImage`: a decoded picture: `width`, `height`, `rgba` (8 bits per channel, row 0 first).
- `ModelTexture`: `image` (index into ModelData::images, -1 for none), `clampS` and `clampT` (false: repeat).
- `ModelMaterial`: `baseColor[4]` (linear), `texture` (index into ModelData::textures), `metallic`, `roughness`, `emissive[3]` (linear, strength applied), `alpha`, `alphaCutoff`, `doubleSided`, `unlit`.
- `ModelPrimitive`: `material` (index, -1 for white, matte, non-metal), `vertices` (8 floats each: px py pz nx ny nz u v), `indices` (uint32 triangle list), `world[16]` (node world matrix, column-major).
- `ModelData`: a parsed model on the CPU: `name`, `primitives`, `materials`, `textures`, `images`, `bounds`, and `Triangles()`.
- `ModelLight`: the renderer's light: `dir[3]` (towards the light), `color[3]`, `sky[3]` and `ground[3]` (linear).
- `Model`: a model on the GPU: owns its buffers, vertex arrays and textures. Copy is disabled. No destructor: call `Destroy()`.
- `ModelRenderer`: the built-in lit renderer, one per app. Public member: `light` (a ModelLight).

## Functions

- `ModelSrgbToLinear(float c)`: one sRGB channel to linear.
- `ModelSrgb(uint32_t hex, float alpha = 1)`: an sRGB hex colour to a linear `XrColor4f`.
- `ModelReadFile(const char* path, ModelData& out, std::string* error = nullptr)`: parses a .glb or .gltf file on the CPU. Returns false and logs the error under the file name.
- `ModelReadAsset(AAssetManager* assets, const char* name, ModelData& out, std::string* error = nullptr)`: the same for a file in the app's assets/ folder (Android).
- `ModelFitHeight(const ModelBounds& b, float heightMetres)`: the matrix that makes the model that tall, with its lowest point on y = 0 and centred on x and z = 0.
- `Model::Load(const char* path)`: ModelReadFile, then Upload. Needs the GL context.
- `Model::LoadAsset(AAssetManager* assets, const char* name)`: ModelReadAsset, then Upload. Needs the GL context.
- `Model::Upload(const ModelData& data)`: creates the GL objects (buffers, vertex arrays, mipmapped sRGB textures) from parsed data.
- `Model::SetRoot(const Mat4& m)`: the model's own transform, applied before the world matrix that Draw gets.
- `Model::FitHeight(float metres)`: SetRoot with ModelFitHeight of the loaded bounds. Call it after Load.
- `Model::Bounds()`: the bounds after the root transform, in metres.
- `Model::Loaded()`: true when the model has GL objects.
- `Model::Name()`: the file or asset name.
- `Model::Error()`: the last error text (empty after a good load).
- `Model::Primitives()`: draw calls for one Draw.
- `Model::Triangles()`: triangles on the GPU.
- `Model::Destroy()`: frees the GL objects and the CPU copies, and resets the root transform.
- `ModelRenderer::Init()`: compiles the built-in lit shader and makes a 1x1 white texture. Needs the GL context.
- `ModelRenderer::SetLight(const XrColor4f& sky, const XrColor4f& ground, const XrColor4f& sun, const XrVector3f& towardsLight)`: sets the ambient colours, the sun colour and the sun direction.
- `ModelRenderer::Draw(const Eye& eye, const Model& m, const Mat4& world)`: draws every primitive of m with eye.viewProj and eye.pose.position. World in metres: rigid or uniform scale only.
- `ModelRenderer::Ready()`: true after a successful Init.
- `ModelRenderer::Destroy()`: frees the shader and the white texture.
