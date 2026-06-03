// ADR: Lit-colour screen-space MeshBlend (Tollenaar-style). Mirrors the already-lit cameraColor
// across the seam, so each surface's lighting comes FOR FREE and it works in Forward+ AND Deferred
// (cameraColor exists in both) — the GBuffer-swap path is retired.
// ADR: NOT geometrically correct — a screen-space reflection masked into looking right. Stability comes
// from WORLD-LOCKED, STATIC noise + de-quant jitter (anchored to wpos via UNITY_MATRIX_I_VP); no
// per-frame term, so it does not crawl. A temporal accumulator (TAA/TSR/DLSS/FSR) is recommended to
// resolve residual grain, not required.
// ADR: TWO composite passes share one tail (ComposeBlend in MeshBlendCommon.hlsl). Pass 0 finds the
// nearest seam by per-pixel SEARCH (probe → grid → 1/d²-weighted direction). Pass 1 reads a precomputed
// JFA field. Both blend a SINGLE seam (stable at normal angles); the glancing fade is opt-in/tunable.
Shader "Hidden/Readymade/MeshBlend"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off

        // ---------------------------------------------------------------------------------------------
        // PASS 0 — SEARCH composite. Per band-pixel: coarse probe, then a quality-sized grid search that
        // returns the nearest different-id pixel + a 1/d²-weighted (smooth) seam direction. O(k²)/pixel.
        // ---------------------------------------------------------------------------------------------
        Pass
        {
            Name "MeshBlendComposite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT // decode deferred camera normals

            #include "MeshBlendCommon.hlsl"

            // Coarse early-out probe: is there ANY different-id pixel within the radius? Cheap reject for
            // the interior of large objects, where most tagged pixels have no seam in reach.
            bool AnySeamWithin(float2 uv, float radiusPx, int myId)
            {
                float step = radiusPx / MESHBLEND_PROBE_K;
                float radius2 = radiusPx * radiusPx;
                [loop] for (int y = -MESHBLEND_PROBE_K; y <= MESHBLEND_PROBE_K; y++)
                {
                    [loop] for (int x = -MESHBLEND_PROBE_K; x <= MESHBLEND_PROBE_K; x++)
                    {
                        float2 offPx = float2(x, y) * step;
                        if (dot(offPx, offPx) > radius2)
                            continue;
                        float2 sUv = uv + offPx * _MeshBlendTexel.xy;
                        if (any(sUv < 0.0) || any(sUv > 1.0))
                            continue;
                        int oid = SampleId(sUv);
                        if (oid != myId && oid != 0)
                            return true;
                    }
                }
                return false;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.uv;
                float3 ownCol = MB_SAMPLE(_SourceTex, sampler_LinearClamp, uv).rgb;

                int myId = SampleId(uv);
                if (myId == 0)
                    return float4(ownCol, 1.0); // not a blendable

                float myRaw = SampleRawDepth(uv);
                float myEye = LinearEyeDepth(myRaw, _ZBufferParams);
                int mySize = (myId >> 6) & 0x3;
                // Surface world position from the engine's authoritative inverse-VP (no convention
                // guesswork). Both the de-quantization jitter and the band-break noise are anchored to
                // THIS, so they stay locked to the surface as the camera moves.
                float3 wpos = ComputeWorldSpacePosition(uv, myRaw, UNITY_MATRIX_I_VP);

                // World-stable radius: shrinks on screen with distance, constant in world space. Width
                // is the size class's ABSOLUTE world half-width (per-class LUT, not a multiplier).
                float radiusPx = WidthForClass(mySize) * _MeshBlendProj.x / max(myEye, 1e-4);
                radiusPx = clamp(radiusPx, _MeshBlendParams.y, _MeshBlendParams.w);
                if (radiusPx < 1.0)
                    return float4(ownCol, 1.0);

                if (!AnySeamWithin(uv, radiusPx, myId))
                    return float4(ownCol, 1.0);

                int gridK = clamp((int)(_MeshBlendProj.z + 0.5), 1, MESHBLEND_MAX_K);
                float step = radiusPx / gridK;
                float radius2 = radiusPx * radiusPx;
                // WORLD-LOCKED de-quantization jitter: offset the grid taps by a per-surface-point
                // amount (hashed from wpos) so the grid-step "slivers" dissolve into a SURFACE-STABLE
                // dither — not a screen-space, per-frame jitter that would crawl with the view.
                float2 jit = (float2(Hash13(wpos * 113.1), Hash13(wpos * 71.7)) - 0.5) * step;

                // Single nearest seam: 1/d²-weighted direction over the nearby different-id pixels (smooth
                // reflect vector), plus the nearest different id. Stable at normal angles. (3-way corners
                // get the inherent medial-axis seam — accepted; the corner-specific fixes regressed normal
                // angles, so they're reverted.)
                float bestDist2 = 1e20;
                float2 dirAccum = 0.0;
                float wAccum = 0.0;
                int otherId = 0;
                bool found = false;

                [loop] for (int y = -gridK; y <= gridK; y++)
                {
                    [loop] for (int x = -gridK; x <= gridK; x++)
                    {
                        float2 offPx = float2(x, y) * step + jit;
                        float d2 = dot(offPx, offPx);
                        if (d2 < 1e-4 || d2 > radius2)
                            continue;
                        float2 sUv = uv + offPx * _MeshBlendTexel.xy;
                        if (any(sUv < 0.0) || any(sUv > 1.0))
                            continue;
                        int oid = SampleId(sUv);
                        if (oid != myId && oid != 0)
                        {
                            float wgt = 1.0 / (d2 + 1.0);
                            dirAccum += offPx * wgt;
                            wAccum += wgt;
                            if (d2 < bestDist2)
                            {
                                bestDist2 = d2;
                                otherId = oid;
                                found = true;
                            }
                        }
                    }
                }

                if (!found || wAccum < 1e-6)
                    return float4(ownCol, 1.0);

                float2 seamDir = normalize(dirAccum);
                float distToSeam = sqrt(bestDist2);

                // Band WIDTH uses the depth AT THE SEAM, not this pixel's depth. On a glancing surface the
                // per-pixel depth plummets toward the camera, and radius ∝ 1/depth then BLOWS UP the band on
                // the near side (the "glancing inflation"). The seam depth is ~constant along the seam, so
                // the band keeps a consistent on-screen width at any view angle. Smaller-width-wins.
                float seamEye = LinearEyeDepth(SampleRawDepth(uv + distToSeam * seamDir * _MeshBlendTexel.xy), _ZBufferParams);
                int   otherSize = (otherId >> 6) & 0x3;
                float radiusEff = clamp(min(WidthForClass(mySize), WidthForClass(otherSize)) * _MeshBlendProj.x / max(seamEye, 1e-4), _MeshBlendParams.y, _MeshBlendParams.w);
                float x01w = NoiseBand(distToSeam / max(radiusEff, 1e-4), wpos);
                if (x01w >= 1.0)
                    return float4(ownCol, 1.0);

                return ComposeBlend(uv, ownCol, myId, otherId, myEye, wpos, seamDir, distToSeam, x01w);
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------------------------------------
        // PASS 1 — JFA composite. The seam direction+distance come from a precomputed jump-flood field
        // (MeshBlendJFA.shader). No per-pixel search: O(1) lookup. The seam field cost is a fixed
        // ~log2(radius) fullscreen flood passes, independent of how much of the screen is band.
        // ---------------------------------------------------------------------------------------------
        Pass
        {
            Name "MeshBlendCompositeJFA"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragJfa
            #pragma target 4.5
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT // decode deferred camera normals

            #include "MeshBlendCommon.hlsl"

            float4 FragJfa(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.uv;
                float3 ownCol = MB_SAMPLE(_SourceTex, sampler_LinearClamp, uv).rgb;

                int myId = SampleId(uv);
                if (myId == 0)
                    return float4(ownCol, 1.0);

                // O(1) lookup: the flood already wrote the nearest seam point (in pixels) for every
                // pixel. This is the whole escalation — the O(k²) search above collapses to one read.
                float2 sCenter = SampleSeam(uv);
                if (sCenter.x < 0.0)
                    return float4(ownCol, 1.0); // no seam reached this pixel

                // Smooth the nearest-seam POINT over a tiny 3x3 neighbourhood, REJECTING points that
                // belong to a different seam segment (> ~2px from the centre's point — medial axis /
                // 3-way junction) so we never average across a discontinuity. The raw field stores the
                // single nearest seed, whose direction is quantized PER PIXEL; that quantization is what
                // reads as salt-and-pepper "sharpening" at the crease. Averaging coherent neighbours
                // gives a smooth sub-pixel seam point → smooth direction AND distance — the JFA analogue
                // of the search path's 1/d²-weighted direction. Fixed 9 taps: still O(1), radius-free.
                float2 sAccum = 0.0;
                float sW = 0.0;
                [unroll] for (int sy = -1; sy <= 1; sy++)
                {
                    [unroll] for (int sx = -1; sx <= 1; sx++)
                    {
                        float2 s = SampleSeam(uv + float2(sx, sy) * _MeshBlendTexel.xy);
                        if (s.x < 0.0)
                            continue;
                        float2 dd = s - sCenter;
                        if (dot(dd, dd) > 4.0) // > 2px → different segment, don't average across it
                            continue;
                        sAccum += s;
                        sW += 1.0;
                    }
                }
                float2 seamPt = (sW > 0.0) ? sAccum / sW : sCenter;

                float myRaw = SampleRawDepth(uv);
                float myEye = LinearEyeDepth(myRaw, _ZBufferParams);
                int mySize = (myId >> 6) & 0x3;
                float3 wpos = ComputeWorldSpacePosition(uv, myRaw, UNITY_MATRIX_I_VP);

                float2 P = uv * _MeshBlendTexel.zw;     // this pixel in pixel coords
                float2 toSeam = seamPt - P;
                float distToSeam = length(toSeam);
                if (distToSeam < 1e-4)
                    return float4(ownCol, 1.0);          // exactly on the seam: invisible
                float2 seamDir = toSeam / distToSeam;

                // The flood field carries no id — recover the neighbour by sampling just past the seam.
                int otherId = SampleId(uv + seamDir * (distToSeam + 1.5) * _MeshBlendTexel.xy);
                if (otherId == 0 || otherId == myId)
                    return float4(ownCol, 1.0);          // seed faced empty/self (3-way fringe): skip

                // Band WIDTH from the SEAM depth (≈constant along the seam), not this pixel's depth, so a
                // glancing surface (depth plummeting toward the camera) doesn't inflate the near-side band.
                float seamEye = LinearEyeDepth(SampleRawDepth(uv + distToSeam * seamDir * _MeshBlendTexel.xy), _ZBufferParams);
                int otherSize = (otherId >> 6) & 0x3;
                float radiusEff = min(WidthForClass(mySize), WidthForClass(otherSize)) * _MeshBlendProj.x / max(seamEye, 1e-4);
                radiusEff = clamp(radiusEff, _MeshBlendParams.y, _MeshBlendParams.w);

                // World-locked de-quantization jitter on the BAND distance ONLY: dither the residual JFA
                // distance staircase into a surface-stable grain (TAA resolves it) so it stops reading as a
                // fixed-frequency zebra. Anchored to wpos → no crawl. ComposeBlend keeps the exact distance.
                float jit = Hash13(wpos * 131.7) - 0.5; // ~±0.5 px, surface-anchored
                float x01w = NoiseBand(saturate((distToSeam + jit) / max(radiusEff, 1e-4)), wpos);
                if (x01w >= 1.0)
                    return float4(ownCol, 1.0);

                return ComposeBlend(uv, ownCol, myId, otherId, myEye, wpos, seamDir, distToSeam, x01w);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
