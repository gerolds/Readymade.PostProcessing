// ADR: Fullscreen false-color of the ID buffer for the Volume debug mode = IDs.
// Decodes the packed byte (size class + id), hashes id->hue, tints by size; pixels
// with no blendable (packed 0) show a dimmed source so scene context stays visible.
// Pass-through invocation shape mirrors FogApply (DrawProcedural triangle + MPB).
Shader "Hidden/Readymade/MeshBlendDebug"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        Pass
        {
            Name "MeshBlendDebug"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

            TEXTURE2D_X(_SourceTex);
            TEXTURE2D_X(_MeshBlendId);

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

            float3 HashIdToColor(int id)
            {
                // Cheap per-id pseudo-random hue — distinct, stable, debug-only.
                float3 h = frac(sin(float3(id, id * 2 + 1, id * 3 + 2)) * float3(43758.5453, 22578.1459, 19642.3490));
                return 0.25 + 0.75 * h;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float idN = SAMPLE_TEXTURE2D_X(_MeshBlendId, sampler_PointClamp, input.uv).r;
                int packed = (int)(idN * 255.0 + 0.5);

                float3 source = SAMPLE_TEXTURE2D_X(_SourceTex, sampler_LinearClamp, input.uv).rgb;
                if (packed == 0)
                    return float4(source * 0.15, 1.0); // no blendable: dim context

                int sizeClass = (packed >> 6) & 0x3;
                int id = packed & 0x3F;
                float3 col = HashIdToColor(id) * lerp(0.45, 1.0, sizeClass / 3.0);
                return float4(col, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
