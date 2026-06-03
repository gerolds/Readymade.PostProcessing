// ADR: Shared front-end-agnostic core for both MeshBlend composite paths. The front-ends differ only in
// HOW they find the nearest seam; from there ComposeBlend() (refine + reflect + guards + band + mix) is
// identical. Single seam — stable at normal angles (3-way corners keep their inherent medial-axis seam;
// the corner-specific multi-way/miter experiments regressed normal angles and were reverted). Band WIDTH
// is keyed to the SEAM's depth so it doesn't inflate at glancing angles. Seam-FINDING is the variable.
// ADR: All texture reads are LOD-0 (MB_SAMPLE) — a plain sample is a gradient instruction and gradients
// in the varying-iteration search loops are UNDEFINED on Metal (compiles to a no-op, no error).
#ifndef MESHBLEND_COMMON_INCLUDED
#define MESHBLEND_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/GlobalSamplers.hlsl"

// Fine search grid half-extent = quality (_MeshBlendProj.z). Coarse probe early-outs.
#define MESHBLEND_MAX_K 12
#define MESHBLEND_PROBE_K 3

// LOD-0 sampling everywhere (no gradient instruction → safe inside the loops on Metal).
#define MB_SAMPLE(tex, smp, coord) SAMPLE_TEXTURE2D_X_LOD(tex, smp, coord, 0)

TEXTURE2D_X(_SourceTex);       // lit cameraColor (HDR)
TEXTURE2D_X(_MeshBlendId);
TEXTURE2D_X(_MeshBlendDepth);
TEXTURE2D_X(_MeshBlendNormals);
TEXTURE2D_X(_MeshBlendSeam);    // JFA seam field: .xy = nearest seam point in pixels, .x<0 = none

float4 _MeshBlendParams;   // x=glancingFilter(0=off) y=minScreenSize(px) z=depthFalloff(m) w=maxRadius(px)
float4 _MeshBlendSizeWidths;// ABSOLUTE blend half-width (m) per size class: x=Small y=Medium z=Large w=Huge
float4 _MeshBlendTexel;    // xy=1/wh zw=wh
float4 _MeshBlendProj;     // x=pxPerWorldAtUnitDepth y=blendStrength z=gridK w=hasNormals(0/1)
float4 _MeshBlendFidelity; // x=slopeFactor y=noiseScale z=noiseStrength w=hasNormals(0/1)
float4 _MeshBlendShape;    // x=falloff(power) y=bias(core fraction) z=noiseFade w=debugBlendArea(0/1)
float4 _MeshBlendJfa;      // x=stepPx (flood) y=maxRadius zw=unused

// Absolute world half-width (m) for a size class (0..3). Per-class LUT, NOT a multiplier — the size
// class SELECTS the width so it can't fight art direction. Ternary select avoids dynamic vector
// indexing (undefined/slow on some targets).
float WidthForClass(int c)
{
    return c <= 0 ? _MeshBlendSizeWidths.x
         : c == 1 ? _MeshBlendSizeWidths.y
         : c == 2 ? _MeshBlendSizeWidths.z
         :          _MeshBlendSizeWidths.w;
}

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

int SampleId(float2 uv)
{
    return (int)(MB_SAMPLE(_MeshBlendId, sampler_PointClamp, uv).r * 255.0 + 0.5);
}

float SampleRawDepth(float2 uv)
{
    return MB_SAMPLE(_MeshBlendDepth, sampler_PointClamp, uv).r;
}

float3 SampleNormal(float2 uv)
{
    float3 n = MB_SAMPLE(_MeshBlendNormals, sampler_PointClamp, uv).xyz;
    #if defined(_GBUFFER_NORMALS_OCT)
        // DEFERRED packs camera normals octahedral in [0,1]; decode to world [-1,1] exactly as URP's
        // SampleSceneNormals does. Without this |N·V| is garbage → the grazing fade (and the slope gate)
        // silently do nothing. Requires `#pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT` on the pass.
        float2 oct = Unpack888ToFloat2(n) * 2.0 - 1.0;
        n = UnpackNormalOctQuadEncode(oct);
    #endif
    return n;
}

// JFA field read: nearest seam point in PIXEL coords (.x < 0 → this pixel reached no seam).
float2 SampleSeam(float2 uv)
{
    return MB_SAMPLE(_MeshBlendSeam, sampler_PointClamp, uv).xy;
}

float Hash13(float3 p)
{
    p = frac(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}

float ValueNoise3D(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = Hash13(i + float3(0, 0, 0));
    float n100 = Hash13(i + float3(1, 0, 0));
    float n010 = Hash13(i + float3(0, 1, 0));
    float n110 = Hash13(i + float3(1, 1, 0));
    float n001 = Hash13(i + float3(0, 0, 1));
    float n101 = Hash13(i + float3(1, 0, 1));
    float n011 = Hash13(i + float3(0, 1, 1));
    float n111 = Hash13(i + float3(1, 1, 1));
    float nx00 = lerp(n000, n100, f.x);
    float nx10 = lerp(n010, n110, f.x);
    float nx01 = lerp(n001, n101, f.x);
    float nx11 = lerp(n011, n111, f.x);
    return lerp(lerp(nx00, nx10, f.y), lerp(nx01, nx11, f.y), f.z);
}

// World-locked noise on the band POSITION: breaks the visible boundary into an organic edge while the
// reflected texture stays coherent. Returns the noised x01 in [0,1]. Shared by every seam slot.
float NoiseBand(float x01, float3 wpos)
{
    float x01w = x01;
    if (_MeshBlendFidelity.z > 0.001)
    {
        float n = ValueNoise3D(wpos * _MeshBlendFidelity.y);
        float noiseAmt = _MeshBlendFidelity.z * saturate(x01 / max(1.0 - _MeshBlendShape.z, 0.05));
        x01w = saturate(x01 + (n - 0.5) * noiseAmt);
    }
    return x01w;
}

// Mirror the lit colour across the seam (the nearest different id) and apply all guards. Single seam —
// stable at normal angles. Band width is keyed to the seam depth by the caller (no glancing inflation).
float4 ComposeBlend(float2 uv, float3 ownCol, int myId, int otherId, float myEye, float3 wpos, float2 seamDir, float distToSeam, float x01w)
{
    // BINARY SEARCH the seam LINE to sub-pixel along the normal. Bracket on THIS seam's neighbour id
    // (not "any different id" — near a 3-way the ray can cross a third surface). dLo (d=0) is myId;
    // expand dHi until it lands on otherId, then bisect → exact reflection 2·distToSeam.
    float dLo = 0.0;
    float dHi = max(distToSeam, 0.5);
    int hiId = SampleId(uv + seamDir * dHi * _MeshBlendTexel.xy);
    [unroll] for (int e = 0; e < 3; e++)
    {
        if (hiId == otherId)
            break;
        dHi += 1.0;
        hiId = SampleId(uv + seamDir * dHi * _MeshBlendTexel.xy);
    }

    float reachPx;
    if (hiId == otherId)
    {
        [unroll] for (int i = 0; i < 6; i++)
        {
            float dMid = 0.5 * (dLo + dHi);
            int mId = SampleId(uv + seamDir * dMid * _MeshBlendTexel.xy);
            if (mId == otherId) dHi = dMid; else dLo = dMid;
        }
        distToSeam = dHi;
        reachPx = 2.0 * distToSeam;
    }
    else
    {
        reachPx = max(2.0 * distToSeam, 1.0);
    }

    float2 seamUv = uv + distToSeam * seamDir * _MeshBlendTexel.xy;
    float2 mirrorUv = uv + reachPx * seamDir * _MeshBlendTexel.xy;

    // 3-way / off-screen / concave guard: the mirror must land on the SAME neighbour id, else fall back
    // to the seam-boundary sample at reduced weight (don't pull a third surface or the sky in).
    int mirrorId = SampleId(mirrorUv);
    bool mirrorOk = all(mirrorUv >= 0.0) && all(mirrorUv <= 1.0) && (mirrorId == otherId);
    float2 otherUv = mirrorOk ? mirrorUv : seamUv;
    float guardW = mirrorOk ? 1.0 : 0.5;

    // Depth gate (real intersection vs incidental screen overlap / sky).
    float seamEye = LinearEyeDepth(SampleRawDepth(seamUv), _ZBufferParams);
    float depthW = saturate(1.0 - abs(seamEye - myEye) / max(_MeshBlendParams.z, 1e-4));

    // Slope gate: suppress blend across sharp inter-object corners. OFF by default (slopeFactor 0).
    float slopeW = 1.0;
    if (_MeshBlendFidelity.w > 0.5)
    {
        float ndot = saturate(dot(SampleNormal(uv), SampleNormal(otherUv)));
        slopeW = lerp(1.0, ndot, _MeshBlendFidelity.x);
    }

    // Screen-edge attenuation: the mirror reads off-screen near the border.
    float2 edge = min(uv, 1.0 - uv);
    float edgeW = saturate(min(edge.x, edge.y) / 0.05);

    // Band shape: flat 0.5 core over the inner `bias` fraction, then a `falloff` edge.
    float ramp = saturate((1.0 - x01w) / max(1.0 - _MeshBlendShape.y, 1e-4));
    float spatialW = 0.5 * pow(ramp, _MeshBlendShape.x);

    // GLANCING filter (texture-independent — DEPTH, not normals, which normal-maps perturb). Recover each
    // side's GRAZING from its per-pixel depth slope: tan(viewAngle) = depthSlope · pxPerWorld / (depth ·
    // screenSpan), where the span on both sides is distToSeam px. Fade only when the MIRROR (sampled) side
    // is much more glancing than the LOCAL side — the artefact case: facing surface mirrors INTO a glancing
    // one starved of pixels. A normal angled intersection has both tans moderate → tanB−tanA small → no
    // fade (vs the old |Δ|/Σ ratio, which fired at every intersection). Computed always (for the debug);
    // applied only when glancingFilter (x) > 0.
    float mirrorEye = LinearEyeDepth(SampleRawDepth(mirrorUv), _ZBufferParams);
    float spanDepth = max(distToSeam * seamEye * (1.0 / max(_MeshBlendProj.x, 1e-4)), 1e-6); // = screenSpan·depth/pxPerWorld
    float tanLocal  = abs(seamEye - myEye)     / spanDepth; // this surface's grazing (tan of view angle)
    float tanMirror = abs(mirrorEye - seamEye) / spanDepth; // the mirrored (sampled) surface's grazing
    float asymFade = smoothstep(1.5, 5.0, tanMirror - tanLocal); // mirror side much more glancing than local
    float asymW = 1.0 - _MeshBlendParams.x * asymFade; // × glancingFilter strength (Volume param; default ~0.85)

    float w = spatialW * depthW * slopeW * guardW * edgeW * _MeshBlendProj.y * asymW; // × blendStrength × asym

    // DEBUG (BlendArea): white = effective blend weight; RED = the band-asymmetry signal (where the
    // glancing filter acts) — visible regardless of glancingFilter, so you can confirm it's computing.
    if (_MeshBlendShape.w > 0.5)
    {
        float3 dbg = lerp(ownCol, float3(1.0, 1.0, 1.0), saturate(w * 2.0));
        dbg = lerp(dbg, float3(1.0, 0.0, 0.0), asymFade * 0.7);
        return float4(dbg, 1.0);
    }

    float3 otherCol = MB_SAMPLE(_SourceTex, sampler_LinearClamp, otherUv).rgb;
    return float4(lerp(ownCol, otherCol, w), 1.0);
}

#endif // MESHBLEND_COMMON_INCLUDED
