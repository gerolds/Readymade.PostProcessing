// ADR: Override material for the ID prepass. Outputs the packed ID (0..255; 0 = no blendable) to an R8
// target. The ID is a uniform, never an interpolated varying — flat by construction (encoding-audit rule).
// ADR: _BlendId is a BARE uniform, deliberately NOT a material Property. A material Property would carry
// a serialized default (0) that SHADOWS a SetGlobalFloat — which silently zeroed the manual per-submesh
// prepass (writes the id via a per-draw global). As a bare uniform it takes the global (manual path) OR
// an MPB override (RendererList path); both reach it, a material default would shadow the former.
// ADR: Occlusion is hardware-tested via the shader's own ZTest LEqual / ZWrite Off against the bound
// depth attachment (the RendererList path additionally sets a matching RenderStateBlock).
Shader "Hidden/Readymade/MeshBlendId"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "MeshBlendId"
            ZWrite Off ZTest LEqual Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Bare uniform (NOT a material Property — see banner): set per-draw via SetGlobalFloat
            // (manual prepass) or overridden per-renderer via MPB (RendererList prepass). Kept out of
            // any CBUFFER so both override paths reach it.
            float _BlendId;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return float4(_BlendId / 255.0, 0.0, 0.0, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
