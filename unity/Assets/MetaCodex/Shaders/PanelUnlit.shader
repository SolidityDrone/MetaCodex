// Opaque vertex-colour unlit for panels, cell backgrounds and the cursor (URP, single-pass instanced stereo).
// Vertex colours are written in linear space by the C# side. A renderer tagged with its panel's slot (UiPanel.TagRenderer:
// a terminal in its frame) is cast with that panel: it exists only behind the scan line, hot for a moment.
Shader "MetaCodex/PanelUnlit"
{
    Properties
    {
        _PanelIndex ("Panel slot (set per renderer)", Float) = -1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/MetaCodex/Shaders/McxOcclusion.hlsl"
            #include "Assets/MetaCodex/Shaders/McxPanelFx.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _PanelIndex;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.color = v.color;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // opaque: where something real is in front, write "nothing" (alpha 0 lets the passthrough show)
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 pfx = McxPanelFx(_PanelIndex);
                float bd = McxBehindScan(_PanelIndex, i.positionWS);
                clip(min(bd - 0.012, 0.5 - pfx.z)); // not cast yet, or already gone: the glass of its panel shows instead
                half v = (half)McxVisibilityIn(_PanelIndex, i.positionWS);
                half3 rgb = i.color.rgb + half3(0.04, 0.63, 1.0) * (half)(McxHeat(bd) * 0.5 + saturate(pfx.y) * 0.12);
                return half4(rgb * v, v);
            }
            ENDHLSL
        }
    }
}
