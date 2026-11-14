# mcx_gfx.h: every public name, and the sources

File: `native/xr/mcx_gfx.h`. Header-only, namespace `mcx`, includes `mcx_xr.h`.
Conventions: metres, Y up, right-handed, angles in radians. Vertex: 8 floats (`px py pz nx ny nz u v`) at locations
0, 1 and 2. Indices: `uint32_t`. Shaders write linear colour. A translucent colour is premultiplied. A draw call
allocates nothing.

Status: the kit's API test, which uses every name below, compiles with the NDK (`check.sh`). A GPU test on Mesa (GLES
3.2, software) passes its 45 checks. Three checks for the contact-shadow state were added. They fail before the fix to
`ContactShadow::Draw` and pass after it. Not run on a headset.

## Vectors and quaternions
- `Vec3Add(a, b)`, `Vec3Sub(a, b)`: component-wise sum and difference.
- `Vec3Scale(a, s)`: a vector times a scalar.
- `Vec3Dot(a, b)`, `Vec3Cross(a, b)`: dot product and cross product.
- `Vec3Normalize(a)`: unit length; a zero vector gives +Y.
- `Vec3Lerp(a, b, t)`: the point the fraction `t` of the way from `a` to `b`.
- `QuatMul(a, b)`: rotates by `b`, then by `a`.
- `QuatConjugate(q)`: the inverse of a unit quaternion.
- `QuatNormalize(q)`: unit length; zero gives the identity.
- `QuatAxisAngle(axis, radians)`: a counter-clockwise turn about `axis` (right-hand rule).
- `QuatYawPitch(yaw, pitch)`: yaw turns left about +Y; pitch tilts up about +X and is applied first.
- `QuatSlerp(a, b, t)`: the short way between two orientations.
- `QuatLookAt(from, to, up)`: the orientation whose forward (-Z) points from `from` to `to`.

## Matrices
- `MulDir(m, v)`: the upper 3x3 of a Mat4 applied to a vector (no translation).
- `MulPoint(m, p)`: a Mat4 applied to a point.
- `Trs(position, rotation, scale)`: a model matrix. Scale first, then rotate, then move.
- `GfxMat3`: a 3x3 matrix stored as columns: `col[3]` and `Mul(v)`.
- `NormalMatrix(m)`: the inverse transpose of a model's upper 3x3. Normals stay perpendicular under non-uniform scale.

## Colour
- `GfxSrgbToLinear(c)`: one sRGB channel (0..1) to linear.
- `GfxSrgbRgb(rgb)`: 0xRRGGBB to a linear rgb vector.
- `GfxSrgb(rgb, alpha = 1)`: 0xRRGGBB and an alpha to a linear `XrColor4f`.

## Shader helpers
- `CompileShader(type, src, name)`: one shader stage. Returns 0 on failure; `mcx::Log` says why.
- `CreateProgram(name, vsSrc, fsSrc)`: a linked program. Returns 0 on failure. The shader objects are deleted.
- `kGfxLitVs`, `kGfxLitFs`: the source of the lit program (`inline constexpr const char*`).
- `kGfxShadowVs`, `kGfxShadowFs`: the source of the contact shadow program.

## Mesh data (CPU)
- `MeshVertex`: `pos`, `normal`, `uv`. 32 bytes.
- `MeshData`: `vertices`, and `indices` (triangles, counter-clockwise seen from outside).
- `MeshData::Append(other, t)`: adds a copy of `other` moved by `t`. Normals follow the transform; a mirror flips the winding back.
- `MeshData::Bounds(lo, hi)`: the box around the vertices.
- `MakeBox(size)`: a centred box with flat normals.
- `MakeRoundedBox(size, radius, samples)`: a box with rounded edges. The radius is clamped to 0.45 x the smallest half size.
- `MakePlane(sizeX, sizeZ, divX, divZ)`: a flat square on XZ with normal +Y.
- `MakeCylinder(radius, height, segments)`: along Y, with flat caps.
- `MakeCone(radius, height, segments)`: along Y. The base is at -height/2 and the apex at +height/2.
- `MakeCapsule(radius, height, segments, rings)`: along Y. `height` is the total; 2 x radius gives a sphere.
- `MakeSphere(radius, segments, rings)`: a sphere.
- `MakeTorus(major, minor, segments, tubeSegments)`: a ring that lies flat (axis Y).

## Mesh (GPU)
- `Mesh`: one VAO and one index buffer, in the shared layout.
- `Mesh::Upload(data)`: creates or replaces the buffers. Returns false when the data is empty.
- `Mesh::Draw()`: one draw call. A program must be bound; `Lit::Begin` does it.
- `Mesh::Destroy()`, `Mesh::Ready()`, `Mesh::IndexCount()`, `Mesh::Vbo()`, `Mesh::Ibo()`.
- No destructor: call `Destroy()` at exit. Copying is deleted.

## Materials and light
- `Material`: `base` (linear `XrColor4f`), `emissive`, `roughness` (0 glossy, 1 matte), `rim`, `unlit`.
- `Material::Matte(srgb)`: roughness 0.85.
- `Material::Glow(srgb, strength = 1)`: body at 25% of the colour, emissive 0.8 x strength, rim 0.6 x strength, roughness 0.35.
- `Material::Glass(srgb, alpha = 0.25)`: base alpha, rim 0.9, roughness 0.1. Draw it with `DrawTranslucent`.
- `Lighting`: `keyDir` (towards the light), `keyColor`, `sky`, `ground`, `exposure`, `fogStart`, `fogEnd`, `fogColor`, `fogOpacity`.
  `fogOpacity` 0 fades objects into the room; 1 fades them into `fogColor`. The defaults suit passthrough.

## Instances
- `Instances`: one mesh, many model matrices, one draw call.
- `Instances::Init(mesh, capacity)`: returns bool. Keep the mesh alive. Init again after a mesh upload.
- `Instances::Clear()`: count 0; keeps the memory.
- `Instances::Add(model)`: returns false when full.
- `Instances::Count()`, `Instances::Upload()` (`Lit` calls it), `Instances::Destroy()`, `Instances::Vao()`, `Instances::IndexCount()`.

## Lit renderer
- `Lit`: one program for every lit object. `light` is a public `Lighting`.
- `Lit::Init()`: returns bool; compiles the program.
- `Lit::Begin(eye)`: binds the program and sets the eye's camera and light. Depth test on (LEQUAL), culling on (back faces), blending off, depth writes on.
- `Lit::Draw(mesh, model, mat)`: opaque. Alpha 1. Writes depth.
- `Lit::DrawTranslucent(mesh, model, mat)`: blended over what is behind. No depth writes. Draw last, back to front.
- `Lit::DrawInstanced(inst, mat)`, `Lit::DrawInstancedTranslucent(inst, mat)`: the same, for `Instances`, in one call.
- `Lit::End()`: puts the App defaults back (depth test, culling and blending off; depth writes on).
- `Lit::Destroy()`: deletes the program.

## Contact shadows
- `ContactShadow`: a soft dark disc on the floor under an object.
- `ContactShadow::fadeHeight`: metres above the floor where the shadow is gone. Default 1.2.
- `ContactShadow::Init()`: returns bool.
- `ContactShadow::Draw(eye, center, size, floorY, strength = 0.5)`: `center` and `size` in metres. The radius is 0.55 x the larger horizontal side. The shadow fades with height above `floorY`. It saves and restores the program, depth test, depth writes, depth function and blending, so it may run between `Begin` and `End`.
- `ContactShadow::Destroy()`: deletes the program and the disc.

## Internal (not for apps)
- `gfx_detail`: the helpers behind the primitives and the vertex layout.

## Sources: Meta docs (metavr, fetched 2026-11-14)
Fetch with `metavr docs fetch "<path>"`; search with `metavr docs search "<query>"`. Blog posts cannot be fetched by
metavr, so none is cited.

| Path | Supports |
|---|---|
| documentation/native/android/os-missed-frames.md | frame budgets 13.9, 11.1 and 8.3 ms at 72, 90 and 120 Hz; a missed frame is a stale frame |
| documentation/native/android/mobile-display-refresh-rate.md | refresh list (Quest 3S: 72, 80, 90, 120; 96 and 100 depend on the display); 72 Hz default; the runtime rejects unlisted rates; hold the rate or fail review; 60 Hz for media only |
| documentation/native/android/os-fixed-foveated-rendering.md | foveation levels; about 25% gain in pixel-heavy apps; simple shaders may gain nothing; text in the centre; compositor layers unaffected; change between scenes |
| documentation/unity/unity-perf.md | draw calls on Quest 3 and 3S (200-300 busy, 400-600 medium, 700-1000 light); triangles (1.3 M-1.8 M); triangles over many tiles cost more; only position needs full precision |
| documentation/native/android/po-art-direction.md | dynamic shadows are costly; avoid full-screen effects; triangle count limits on tiled GPUs; alpha cards overdraw |
| design/design-graphic-rendering-pipeline.md | alpha blending is expensive; each overlapping pixel needs more work |
| essentials/compositor-layers.md | alpha blending writes no depth, so blended content can draw in the wrong order |
| design/display.md | LCDs cannot tell levels below 13 of 255 (sRGB) apart; shader output is linear RGB; Meta VR devices cover at least sRGB |
| design/mr-design-guideline.md | virtual objects match the size and depth of reality |
| design/mr-design-scene.md | a halo or drop shadow on the floor pins an object |
| documentation/native/android/ts-renderdoc-shaderstats.md | 16-bit ALU instructions are faster and use less register space; use lower precision where possible |
| documentation/native/android/os-vulkan-opengl.md | OpenGL ES is legacy; new features are Vulkan only |
| documentation/native/android/po-advanced-gpu-pipelines.md | clear or invalidate colour buffers before the first draw; invalidate depth before flush |
| documentation/native/android/gpu-tiled.md | all Meta VR devices render tile by tile; Quest 3 default eye size 1680 x 1760 |
| documentation/native/android/os-cpu-gpu-levels.md | Quest 3 and 3S: CPU 0-3 and GPU 0-2 always available; CPU 4 and GPU 3-4 only without passthrough features |
| documentation/native/android/po-quest-boost.md | level trading is set in the manifest (`com.oculus.trade_cpu_for_gpu_amount`): 1 = +1 level to GPU, -1 to CPU; OpenXR apps only |
| documentation/unity/gpu-improved-algorithms.md | 4x MSAA is cheap, and Meta recommends it (Unity page; same GPU) |

## Not in the kit, and why
- MSAA: the eye buffers have one sample (`sampleCount = 1`, native/xr/mcx_xr.cpp, `App::InitSwapchains`). Turning it on is a layer change.
- Smaller vertex layout: the shared layout is 8 floats for every attribute. Meta says only position needs full precision (unity-perf.md). Changing the layout is a later kit change.
- Dynamic shadow maps, post-processing, reflections: left out on purpose (po-art-direction.md).
