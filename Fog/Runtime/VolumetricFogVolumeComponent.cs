// ADR: Volume-framework tunable surface — the ONLY designer-facing config for
// the fog (drop-in: no bespoke config asset). IsActive() gates the whole feature.
// VolumetricFogPass reads the active stack instance each frame.
// ADR: P1' pivot — sunless interiors want LOCAL lights, not the sun; localLights
// is the reason the fog is volumetric. Geometry occlusion (shadow-atlas gate) is the
// non-negotiable and is wired. voxelFieldWeight/dustIntensity are inert P3 doors.

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Readymade.PostProcessing
{
    /// <summary>Froxel grid resolution tier. Trades cost for fidelity.</summary>
    public enum FogQuality
    {
        Low,    // 128 x 72 x 48
        Medium, // 160 x 90 x 64
        High    // 240 x 135 x 96
    }

    [Serializable]
    [VolumeComponentMenu("Post-processing/Volumetric Fog")]
    public sealed class VolumetricFogVolumeComponent : VolumeComponent, IPostProcessComponent
    {
        [Header("Enable")]
        public BoolParameter enabled = new BoolParameter(false);

        [Header("Density")]
        [Tooltip("Base extinction (fog thickness) per meter at/below heightStart.")]
        public MinFloatParameter density = new MinFloatParameter(0.02f, 0f);

        [Tooltip("World-space Y where the height layer begins to thin.")]
        public FloatParameter heightStart = new FloatParameter(0f);

        [Tooltip("Exponential density falloff above heightStart (1/m). 0 = uniform.")]
        public MinFloatParameter heightFalloff = new MinFloatParameter(0.05f, 0f);

        [Header("Scattering")]
        [Tooltip("Scattering albedo / fog tint.")]
        public ColorParameter albedo = new ColorParameter(Color.white, false, false, true);

        [Tooltip("Ambient in-scatter added regardless of the main light.")]
        public ColorParameter ambientTint = new ColorParameter(new Color(0.10f, 0.12f, 0.15f), true, false, true);

        [Tooltip("Henyey-Greenstein anisotropy. >0 forward (toward sun), <0 back.")]
        public ClampedFloatParameter anisotropy = new ClampedFloatParameter(0.4f, -0.95f, 0.95f);

        [Tooltip("Scales the main directional light's contribution to in-scatter. In sunless interiors keep low/zero — local lights carry the look.")]
        public MinFloatParameter lightIntensityScale = new MinFloatParameter(1f, 0f);

        [Header("Local lights (P1')")]
        [Tooltip("In-scatter from scene point/spot lights through the fog — the reason it is volumetric indoors. Geometry occlusion is applied via each light's shadow map, so lights only light the fog they can actually reach (no glow through walls). Requires the light to cast shadows.")]
        public BoolParameter localLights = new BoolParameter(true);

        [Tooltip("Scales local-light in-scatter contribution.")]
        public MinFloatParameter localLightIntensity = new MinFloatParameter(1f, 0f);

        [Tooltip("Phase anisotropy for LOCAL lights only (separate from the sun's). Low/0 = stable cone visible from any angle (dust look). High = forward god-ray streaks that track the view.")]
        public ClampedFloatParameter localAnisotropy = new ClampedFloatParameter(0.2f, -0.95f, 0.95f);

        [Tooltip("Treats each local light as a sphere of this radius (m) instead of a point: bounds the 1/d² spike near the source so fog there stops aliasing into froxel/dither patterns. Larger = softer, dimmer core. 0 = hard point.")]
        public MinFloatParameter localLightRadius = new MinFloatParameter(0.5f, 0f);

        [Header("Range")]
        [Tooltip("Far bound of the froxel volume (meters). Near-field: keep modest.")]
        public ClampedFloatParameter maxDistance = new ClampedFloatParameter(256f, 8f, 500f);

        [Tooltip("Froxel Z distribution exponent. >1 packs more slices near the camera.")]
        public ClampedFloatParameter sliceDistributionExponent = new ClampedFloatParameter(2.5f, 1f, 8f);

        [Header("Quality")]
        public VolumeParameter<FogQuality> quality = new VolumeParameter<FogQuality> { value = FogQuality.Medium };

        [Tooltip("Temporal reprojection: stabilises and amortises. Disable to debug.")]
        public BoolParameter temporal = new BoolParameter(true);

        [Tooltip("History blend length in frames; higher = smoother but laggier.")]
        public ClampedFloatParameter historyFrames = new ClampedFloatParameter(8f, 1f, 32f);

        [Header("Voxel field (inert until a source is registered — P3)")]
        [Tooltip("How strongly the external voxel field modulates density/colour.")]
        public ClampedFloatParameter voxelFieldWeight = new ClampedFloatParameter(0f, 0f, 1f);

        [Tooltip("Extra high-frequency dust modulation. Inert until a field source exists.")]
        public ClampedFloatParameter dustIntensity = new ClampedFloatParameter(0f, 0f, 4f);

        public bool IsActive() => enabled.value && density.value > 0f;
    }
}
