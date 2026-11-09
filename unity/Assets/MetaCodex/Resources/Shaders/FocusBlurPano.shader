// MetaCodex Ambient Focus BLUR: paints the passthrough camera's picture into a panorama of the room around the user
// (equirectangular, world-oriented: x east of +z, y up), one camera frame at a time. Each panorama texel is a world
// direction; it is projected through the camera's pose and intrinsics (as PassthroughCameraAccess.
// WorldToViewportPoint) and takes the picture's colour there through a round mask: full weight in the middle of the
// picture, none before its edges (no rectangle, no corners). Blended over what is already there, so turning the
// head keeps what was seen: the blur stays with the room, not with the camera.
// Stored premultiplied (rgb = colour x coverage, a = coverage, from a cleared start), so its mip levels average
// correctly and unseen texels never darken the blur. AmbientFocus reads it by view direction at a high mip.
//   _McxCamSrc the camera picture   _PanoCamW2C world -> camera (x right, y up, z forward)   _PanoCamIntr focal xy,
//   principal point zw (sensor pixels)   _PanoCamCrop the sensor region of the picture   _PanoBlend weight per frame
Shader "Hidden/MetaCodex/FocusBlurPano"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            ZWrite Off ZTest Always Cull Off
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_McxCamSrc);
            SAMPLER(sampler_McxCamSrc);
            float4x4 _PanoCamW2C;
            float4 _PanoCamIntr;
            float4 _PanoCamCrop;
            float _PanoBlend;

            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(uint id : SV_VertexID)
            {
                float2 p = float2(id == 1 ? 3.0 : -1.0, id == 2 ? 3.0 : -1.0);
                Varyings o;
                o.positionCS = float4(p, 0.0, 1.0);
                o.uv = p * 0.5 + 0.5;
                #if UNITY_UV_STARTS_AT_TOP
                o.uv.y = 1.0 - o.uv.y; // a texel's sampling uv is the uv it is computed for (as GetFullScreenTriangleTexCoord)
                #endif
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float lon = (i.uv.x - 0.5) * 6.28318531, lat = (i.uv.y - 0.5) * 3.14159265;
                float3 d = float3(sin(lon) * cos(lat), sin(lat), cos(lon) * cos(lat));
                // the panorama is centred on the camera: only the direction matters
                float3 pc = mul((float3x3)_PanoCamW2C, d);
                if (pc.z < 0.05) discard;
                float2 sensor = pc.xy / pc.z * _PanoCamIntr.xy + _PanoCamIntr.zw;
                float2 uvc = (sensor - _PanoCamCrop.xy) / _PanoCamCrop.zw;
                float r = length((uvc - 0.5) * 2.0); // 1 on the ellipse touching the picture's edges
                if (r >= 1.0) discard;
                float w = smoothstep(1.0, 0.55, r) * _PanoBlend;
                float3 c = SAMPLE_TEXTURE2D_LOD(_McxCamSrc, sampler_McxCamSrc, uvc, 0).rgb;
                return float4(c, w);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
