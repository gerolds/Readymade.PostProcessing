// ADR: Volume-framework tunable surface — the ONLY designer-facing config for mesh-blend (drop-in:
// no bespoke config asset). IsActive() gates the feature; MeshBlendPass reads the active stack each frame.
// ADR: This drives the lit-colour screen-space MIRROR (works Forward+ and Deferred). The break-up
// noise and the de-quantization jitter are WORLD-LOCKED + static (no per-frame term) so they stay on
// the surface; a temporal accumulator (TAA/TSR/DLSS/FSR) is recommended to resolve the residual grain
// but is not required and there is no temporal toggle.
// ADR: useJumpFlood switches the seam-finding front-end to a Jump-Flood distance field — same visual
// output, coverage-independent cost. Experimental/educational; default OFF.

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Readymade.PostProcessing
{
    /// <summary>Kernel cost/quality tier for the seam search (grid tap density).</summary>
    public enum MeshBlendQuality
    {
        Low,
        Medium,
        High
    }

    /// <summary>Debug visualization mode.</summary>
    public enum MeshBlendDebugMode
    {
        /// <summary>Composite the blend normally.</summary>
        Normal,

        /// <summary>False-colour the per-object / per-submesh ID buffer.</summary>
        Ids,

        /// <summary>Effective blend weight (after all guards) as an unlit white overlay.</summary>
        BlendArea
    }

    [Serializable]
    [VolumeComponentMenu("Post-processing/Mesh Blend")]
    public sealed class MeshBlendVolumeComponent : VolumeComponent, IPostProcessComponent
    {
        [Header("Enable")]
        public BoolParameter enabled = new BoolParameter(false);

        [Header("Blend")]
        [Tooltip("Absolute blend half-width in METERS per object size class: x=Small, y=Medium, z=Large, w=Huge. A seam uses the SMALLER of the two objects' widths (so a pebble against a cliff stays tight). All-zero disables. Absolute per-class — no global multiplier — so it doesn't fight art direction.")]
        public Vector4Parameter blendWidths = new Vector4Parameter(new Vector4(0.02f, 0.03f, 0.04f, 0.05f));

        [Tooltip("Scales the blend peak at the seam. 1 = true half/half (50% each side at the seam), 0 = no blend. Above half/half would swap the surfaces, so this caps at the 50/50 mix.")]
        public ClampedFloatParameter blendStrength = new ClampedFloatParameter(1f, 0f, 1f);

        [Tooltip("Width of the full-strength (seamless 50/50) core as a fraction of the blend radius, before the edge falloff begins. 0 = a thin peak right at the seam (reads as a hard edge); higher = a wide seamless transition zone. Main lever for 'not enough blend'.")]
        public ClampedFloatParameter blendBias = new ClampedFloatParameter(0.1f, 0f, 1f);

        [Tooltip("Shapes the edge falloff OUTSIDE the core. 1 = linear; below 1 = softer/wider edge; above 1 = sharper edge.")]
        public ClampedFloatParameter blendFalloff = new ClampedFloatParameter(1f, 0.1f, 6f);

        [Tooltip("Floor on the on-screen blend width (pixels) so distant seams don't collapse to nothing.")]
        public ClampedFloatParameter minScreenSize = new ClampedFloatParameter(2f, 0f, 128f);

        [Tooltip("Depth-difference gate width (meters). Surfaces farther apart than this in depth do not blend — stops screen-space overlaps that are not real intersections, and silhouettes against the sky. Raise it if intersecting curved surfaces blend too weakly.")]
        public MinFloatParameter depthFalloff = new MinFloatParameter(2f, 0f);

        [Header("Fidelity")]
        [Tooltip("Suppresses blend across sharp inter-object corners (high surface-normal difference) where the mirror is least reliable. 0 = off. Needs the DepthNormals prepass (forced by the feature).")]
        public ClampedFloatParameter slopeFactor = new ClampedFloatParameter(0f, 0f, 1f);

        [Tooltip("Fades the blend where the seam is viewed too ASYMMETRICALLY — one surface near head-on, the other glancing/foreshortened to too few pixels to mirror cleanly (the main glancing artefact). Depth-based, so unlike normals it's not fooled by normal-mapped textures. On by default; 0 = off. Higher = fades more aggressively. (Tick the override checkbox to change it — URP ignores un-ticked params and uses this default.)")]
        public ClampedFloatParameter glancingFilter = new ClampedFloatParameter(0.85f, 0f, 1f);

        [Tooltip("World-space frequency of the boundary-masking value noise (units = 1/meters). Higher = finer breakup.")]
        public MinFloatParameter noiseScale = new MinFloatParameter(4f, 0f);

        [Tooltip("How strongly the world-locked noise breaks the seam boundary into an organic edge. The seam-line reflection already gives texture + continuity, so this is just the break-up. 0 = a clean lerp.")]
        public ClampedFloatParameter noiseStrength = new ClampedFloatParameter(0.5f, 0f, 1f);

        [Tooltip("How quickly the band-break noise ramps in from the seam. The exact midpoint stays clean (so both sides stay continuous); higher values bring the noise closer to the seam.")]
        public ClampedFloatParameter noiseFade = new ClampedFloatParameter(0.7f, 0f, 1f);

        [Header("Quality")]
        public VolumeParameter<MeshBlendQuality> quality = new VolumeParameter<MeshBlendQuality> { value = MeshBlendQuality.Medium };

        [Header("Experimental")]
        [Tooltip("Find seams with a Jump-Flood distance field (seed + ~8 fullscreen flood passes) instead of the per-pixel search. Same visual result; cost is independent of how much screen is seam (flat, not per-band). Educational A/B — leave OFF for production unless a frame capture shows the search is the bottleneck.")]
        public BoolParameter useJumpFlood = new BoolParameter(false);

        [Header("Debug")]
        [Tooltip("Normal = composite. IDs = false-colour the per-object/per-submesh ID buffer. Blend Area = the effective blend weight (after every guard) as an unlit white overlay — shows exactly where blending happens and how wide.")]
        public VolumeParameter<MeshBlendDebugMode> debugMode = new VolumeParameter<MeshBlendDebugMode> { value = MeshBlendDebugMode.Normal };

        public bool IsActive()
        {
            Vector4 w = blendWidths.value;
            float maxWidth = Mathf.Max(Mathf.Max(w.x, w.y), Mathf.Max(w.z, w.w));
            return enabled.value && (maxWidth > 0f || debugMode.value != MeshBlendDebugMode.Normal);
        }
    }
}
