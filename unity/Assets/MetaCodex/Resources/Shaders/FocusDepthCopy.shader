// MetaCodex Ambient Focus: copies the left eye's environment depth into a small float target for a CPU readback
// (the floor under the user's feet). Each texel holds the raw depth ((ndc + 1) / 2, as Meta's depth texture stores
// it) and the depth-map uv it was read at, so the reader does not depend on the readback's row order or flipping
// (same idea as SandyBridge's DepthCopy). w = 1 marks a written texel.
Shader "Hidden/MetaCodex/FocusDepthCopy"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            ZWrite Off ZTest Always Cull Off Blend Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_ARRAY(_EnvironmentDepthTexture); // sampler_PointClamp comes from URP (GlobalSamplers.hlsl)

            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings vert(uint id : SV_VertexID)
            {
                // one triangle covering the whole target
                float2 p = float2(id == 1 ? 3.0 : -1.0, id == 2 ? 3.0 : -1.0);
                Varyings o;
                o.positionCS = float4(p, 0.0, 1.0);
                o.uv = p * 0.5 + 0.5;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float d = SAMPLE_TEXTURE2D_ARRAY(_EnvironmentDepthTexture, sampler_PointClamp, i.uv, 0).r;
                return float4(d, i.uv, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
