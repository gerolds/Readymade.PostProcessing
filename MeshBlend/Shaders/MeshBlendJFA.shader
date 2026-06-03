// ADR: Jump-Flood seam distance field — the educational "escalation" of the per-pixel search. Two
// passes feed MeshBlend.shader Pass 1: SEED marks boundary pixels with the sub-pixel seam-LINE point
// (half a pixel toward the differing neighbour — both sides coincide on the line, so directions point
// across the seam and exact-seam pixels still blend); FLOOD ping-pongs log2(radius) times, each pixel
// adopting the nearest seam coordinate among self + 8 neighbours at the current step. After the last
// pass every pixel knows its nearest seam POINT in O(1).
// ADR: Field is RG float = absolute pixel coords of the nearest seam point; .x < 0 is the "no seam"
// sentinel. Absolute (not offset) coords so clamped edge reads stay correct. Flood step starts below
// the max blend radius (NOT screen/2): cost is capped at ~8 passes regardless of resolution.
// ADR: LOD-0 sampling (MB_SAMPLE) — gradients in flow are undefined on Metal.
Shader "Hidden/Readymade/MeshBlendJFA"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        // PASS 0 — SEED. A pixel seeds (stores its own pixel coord) iff it is blendable and borders a
        // DIFFERENT non-zero id (a real seam, not an edge against empty space). Else writes the sentinel.
        Pass
        {
            Name "MeshBlendJfaSeed"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragSeed
            #pragma target 4.5

            #include "MeshBlendCommon.hlsl"

            float4 FragSeed(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.uv;
                int myId = SampleId(uv);
                float2 sentinel = float2(-1.0, -1.0);
                if (myId == 0)
                    return float4(sentinel, 0.0, 0.0);

                // Sum the offsets to every DIFFERENT-id neighbour; the average points across the seam.
                float2 sumOff = 0.0;
                int diffCount = 0;
                [unroll] for (int y = -1; y <= 1; y++)
                {
                    [unroll] for (int x = -1; x <= 1; x++)
                    {
                        if (x == 0 && y == 0)
                            continue;
                        int nId = SampleId(uv + float2(x, y) * _MeshBlendTexel.xy);
                        if (nId != 0 && nId != myId)
                        {
                            sumOff += float2(x, y);
                            diffCount++;
                        }
                    }
                }
                if (diffCount == 0)
                    return float4(sentinel, 0.0, 0.0);

                // Seed ON the seam LINE (half a pixel toward the neighbour), NOT at the pixel centre.
                // Both sides of a boundary then store near-coincident line points, so (a) exact-seam
                // pixels get a ~0.5px distance and still blend (instead of snapping to "self", which
                // reads as a hard line), and (b) the flooded direction always points ACROSS the seam —
                // killing the per-pixel sign flips that look like salt-and-pepper sharpening.
                float2 selfPx = uv * _MeshBlendTexel.zw;
                float2 onLine = (dot(sumOff, sumOff) > 1e-6) ? selfPx + 0.5 * normalize(sumOff) : selfPx;
                return float4(onLine, 0.0, 1.0);
            }
            ENDHLSL
        }

        // PASS 1 — FLOOD. One jump step (_MeshBlendJfa.x px). Each pixel keeps, among self + 8 neighbours
        // at ±step, the seam coordinate nearest to ITSELF. Run repeatedly with halving step (+ a final
        // step-1 cleanup pass) to converge the field.
        Pass
        {
            Name "MeshBlendJfaFlood"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragFlood
            #pragma target 4.5

            #include "MeshBlendCommon.hlsl"

            float4 FragFlood(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.uv;
                float2 P = uv * _MeshBlendTexel.zw;
                float2 stepUv = _MeshBlendJfa.x * _MeshBlendTexel.xy;

                float bestD2 = 1e30;
                float2 bestSeed = float2(-1.0, -1.0);

                [unroll] for (int y = -1; y <= 1; y++)
                {
                    [unroll] for (int x = -1; x <= 1; x++)
                    {
                        float2 seed = SampleSeam(uv + float2(x, y) * stepUv);
                        if (seed.x < 0.0)
                            continue;
                        float2 d = seed - P;
                        float d2 = dot(d, d);
                        if (d2 < bestD2)
                        {
                            bestD2 = d2;
                            bestSeed = seed;
                        }
                    }
                }

                return float4(bestSeed, 0.0, bestSeed.x < 0.0 ? 0.0 : 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
