// MetaCodex: real things in front of the UI. Included by every UI shader (URP HLSL and the CG TextMeshPro copy), so
// it uses plain HLSL objects only. ShellAmbient publishes, each frame while the room's depth runs:
//   _McxDepthSharp   Texture2DArray, one slice per eye: the sharp linear depth (metres, 0 = no reading) of
//                    Resources/Shaders/DepthSharp (Meta's depth upscaled with the passthrough camera's picture)
//   _EnvironmentDepthReprojectionMatrices[2], _EnvironmentDepthZBufferParams: Meta's (world -> depth clip, ndc -> linear)
//   _McxOcclusion    1 = hide UI where something real is in front of it
//   _McxUiHide       0..1: the UI steps aside (the Horizon OS menu is open, or a hand does the system gesture)
// McxVisibility(positionWS) multiplies a fragment's alpha (and so its premultiplied colour): 0 where a real surface
// is 6 cm (+2 % of the distance) or more in front of it, 1 when it is not in front by more than 2 cm (+1 %).
// McxOcclusionCoord (vertex) + McxVisibilityAt (fragment) give the same value with the matrix product per vertex.
#ifndef MCX_OCCLUSION_INCLUDED
#define MCX_OCCLUSION_INCLUDED

Texture2DArray _McxDepthSharp;
SamplerState sampler_McxDepthSharp;
#ifndef MCX_DEPTH_MATRICES_DECLARED
#define MCX_DEPTH_MATRICES_DECLARED
float4x4 _EnvironmentDepthReprojectionMatrices[2];
float4 _EnvironmentDepthZBufferParams;
#endif
float _McxOcclusion;
float _McxUiHide;

uint McxEyeIndex()
{
#if defined(UNITY_STEREO_INSTANCING_ENABLED) || defined(UNITY_STEREO_MULTIVIEW_ENABLED)
    return (uint)unity_StereoEyeIndex;
#else
    return 0; // a mono camera (Editor, self-tests): the left eye's depth
#endif
}

float McxVisibility(float3 positionWS)
{
    float shown = 1.0 - saturate(_McxUiHide);
    if (_McxOcclusion < 0.5) return shown;
    uint eye = McxEyeIndex();
    float4 q = mul(_EnvironmentDepthReprojectionMatrices[eye], float4(positionWS, 1.0));
    if (q.w <= 1e-5) return shown;
    float2 uv = (q.xy / q.w + 1.0) * 0.5;
    if (any(uv < 0.0) || any(uv > 1.0)) return shown;
    float zf = _EnvironmentDepthZBufferParams.x / (q.z / q.w + _EnvironmentDepthZBufferParams.y); // this fragment, linear
    float zr = _McxDepthSharp.SampleLevel(sampler_McxDepthSharp, float3(uv, eye), 0).r;         // the real surface
    if (zr <= 0.01) return shown; // no reading there
    float d = zf - zr;            // > 0: the real surface is in front of the fragment
    return shown * (1.0 - smoothstep(0.02 + 0.01 * zf, 0.06 + 0.02 * zf, d));
}

// The same test split in two for shaders that draw many pixels per vertex (SciFiUI, TextBend): the vertex shader
// computes McxOcclusionCoord (the point in the depth's clip space; it is linear in the position, so it interpolates
// exactly) and the fragment calls McxVisibilityAt with it. Same result, no 4x4 product per pixel.
float4 McxOcclusionCoord(float3 positionWS)
{
    return mul(_EnvironmentDepthReprojectionMatrices[McxEyeIndex()], float4(positionWS, 1.0));
}

float McxVisibilityAt(float4 q)
{
    float shown = 1.0 - saturate(_McxUiHide);
    if (_McxOcclusion < 0.5) return shown;
    if (q.w <= 1e-5) return shown;
    uint eye = McxEyeIndex();
    float2 uv = (q.xy / q.w + 1.0) * 0.5;
    if (any(uv < 0.0) || any(uv > 1.0)) return shown;
    float zf = _EnvironmentDepthZBufferParams.x / (q.z / q.w + _EnvironmentDepthZBufferParams.y);
    float zr = _McxDepthSharp.SampleLevel(sampler_McxDepthSharp, float3(uv, eye), 0).r;
    if (zr <= 0.01) return shown;
    float d = zf - zr;
    return shown * (1.0 - smoothstep(0.02 + 0.01 * zf, 0.06 + 0.02 * zf, d));
}

#endif
