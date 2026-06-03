// ADR: Pre-post multiply of HDR linear color by exp2(EV); EV read from a per-camera StructuredBuffer (no readback) or a fixed value.
Shader "Hidden/Readymade/AutoExposureApply"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        Pass
        {
            Name "AutoExposureApply"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

            TEXTURE2D_X(_SourceTex);
            StructuredBuffer<float2> _ExposureBuffer; // [0].y = adapted multiplier
            float _UseFixed;
            float _FixedMultiplier;

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(uint vertexID : SV_VertexID)
            {
                Varyings output;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
                output.uv = GetFullScreenTriangleTexCoord(vertexID);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float4 color = SAMPLE_TEXTURE2D_X(_SourceTex, sampler_LinearClamp, input.uv);
                float multiplier = (_UseFixed > 0.5) ? _FixedMultiplier : _ExposureBuffer[0].y;
                color.rgb *= multiplier;
                return color;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
