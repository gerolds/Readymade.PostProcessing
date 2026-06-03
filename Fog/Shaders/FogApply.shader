// ADR: Fullscreen composite of the integrated froxel volume onto HDR scene color. Depth-correct: scene linear-eye-depth inverts the slice distribution to fetch transmittance/in-scatter at the surface, so geometry occludes and is occluded.
// ADR: Static (frame-invariant) blue-noise dither on the slice fetch breaks froxel banding at graze angles without flicker — temporal animation lives in the froxel jitter, not here. Output replaces cameraColor (no hardware blend) to dodge load-action ambiguity.
Shader "Hidden/Readymade/FogApply"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        Pass
        {
            Name "FogApply"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

            TEXTURE2D_X(_SourceTex);
            TEXTURE2D_X(_FogDepthTex);
            TEXTURE3D(_FogIntegrated);

            float4 _FogApplyParams; // (near, far, sliceExponent k, 1/sliceCount)

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

            // Interleaved gradient noise (Jimenez 2014) — spectrally near blue noise, texture-free.
            // Static (no frame term) so the slice-fetch dither never flickers; the froxel pass owns
            // temporal animation.
            float FogIGN(float2 p)
            {
                return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715))));
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float near = _FogApplyParams.x;
                float far  = _FogApplyParams.y;
                float k    = _FogApplyParams.z;

                float rawDepth = SAMPLE_TEXTURE2D_X(_FogDepthTex, sampler_PointClamp, input.uv).r;
                float linDepth = LinearEyeDepth(rawDepth, _ZBufferParams);

                float slice01 = pow(saturate((linDepth - near) / max(far - near, 1e-4)), 1.0 / k);
                slice01 += (FogIGN(input.positionCS.xy) - 0.5) * _FogApplyParams.w;
                slice01 = saturate(slice01);

                float4 integ = SAMPLE_TEXTURE3D_LOD(_FogIntegrated, sampler_LinearClamp, float3(input.uv, slice01), 0);
                float3 inscatter = integ.rgb;
                float transmittance = integ.a;

                float4 src = SAMPLE_TEXTURE2D_X(_SourceTex, sampler_LinearClamp, input.uv);
                return float4(src.rgb * transmittance + inscatter, src.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
