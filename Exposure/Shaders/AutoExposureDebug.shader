// ADR: Debug-only overlay reading the per-camera histogram + exposure buffers; alpha-blended into the final image, discarded outside its rect.
Shader "Hidden/Readymade/AutoExposureDebug"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "AutoExposureDebug"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            StructuredBuffer<uint> _DebugHistogram;
            StructuredBuffer<float2> _DebugExposure;
            float4 _OverlayRect;  // (x, y, w, h) in 0..1 screen space
            float2 _DebugEvRange; // (minEV, maxEV) mapped across the bins

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
                float2 local = (input.uv - _OverlayRect.xy) / _OverlayRect.zw;
                if (any(local < 0.0) || any(local > 1.0))
                    discard;

                uint maxCount = 1u;
                [loop] for (uint k = 0u; k < 256u; k++)
                    maxCount = max(maxCount, _DebugHistogram[k]);

                uint bin = (uint)(saturate(local.x) * 255.0);
                float barHeight = (float)_DebugHistogram[bin] / (float)maxCount;

                float3 color = (local.y < barHeight) ? float3(0.85, 0.85, 0.25) : float3(0.05, 0.05, 0.05);
                float alpha = (local.y < barHeight) ? 0.9 : 0.45;

                float evX = saturate((_DebugExposure[0].x - _DebugEvRange.x) / max(_DebugEvRange.y - _DebugEvRange.x, 1e-4));
                if (abs(local.x - evX) < 0.004)
                {
                    color = float3(1.0, 0.25, 0.25);
                    alpha = 1.0;
                }

                return float4(color, alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
