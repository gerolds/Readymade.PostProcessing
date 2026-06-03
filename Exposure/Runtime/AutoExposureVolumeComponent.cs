// ADR: Volume-framework tunable surface — the ONLY designer-facing config (drop-in: no bespoke config asset).
// ADR: Holds no temporal state; AutoExposurePass reads the active stack instance each frame. State lives in AutoExposureState.
// ADR: Center/Spot/Average are one radial weight family; mask-texture metering deferred.
#nullable enable
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Readymade.PostProcessing
{
    public enum ExposureMode
    {
        Fixed = 0,
        Automatic = 1,
    }

    public enum MeteringMode
    {
        CenterWeighted = 0,
        Spot = 1,
        Average = 2,
    }

    [Serializable]
    public sealed class ExposureModeParameter : VolumeParameter<ExposureMode>
    {
        public ExposureModeParameter(ExposureMode value, bool overrideState = false) : base(value, overrideState) { }
    }

    [Serializable]
    public sealed class MeteringModeParameter : VolumeParameter<MeteringMode>
    {
        public MeteringModeParameter(MeteringMode value, bool overrideState = false) : base(value, overrideState) { }
    }

    /// <summary>
    /// Volume-driven settings for the center-weighted, histogram-based auto exposure feature. Holds no
    /// runtime state; the renderer feature owns per-camera adaptation.
    /// </summary>
    [Serializable]
    [VolumeComponentMenu("Post-processing/Auto Exposure")]
    [SupportedOnRenderPipeline(typeof(UniversalRenderPipelineAsset))]
    public sealed class AutoExposureVolumeComponent : VolumeComponent, IPostProcessComponent
    {
        [Header("Mode")]
        [Tooltip("Fixed uses the explicit exposure below; Automatic meters the scene each frame.")]
        public ExposureModeParameter mode = new ExposureModeParameter(ExposureMode.Automatic);

        [Tooltip("Exposure in EV100 applied when Mode is Fixed.")]
        public FloatParameter fixedExposure = new FloatParameter(0f);

        [Header("Metering")]
        public MeteringModeParameter metering = new MeteringModeParameter(MeteringMode.CenterWeighted);

        [Tooltip("Metering center in viewport space (0..1).")]
        public Vector2Parameter meterCenter = new Vector2Parameter(new Vector2(0.5f, 0.5f));

        [Tooltip("Radius of the metering region as a fraction of half-height.")]
        public ClampedFloatParameter meterSize = new ClampedFloatParameter(0.7f, 0.05f, 1.5f);

        [Tooltip("How strongly the center is favored over the edges (Center-Weighted only).")]
        public ClampedFloatParameter centerStrength = new ClampedFloatParameter(0.8f, 0f, 1f);

        [Header("Range & Adaptation")]
        [Tooltip("Lower exposure clamp in EV100.")]
        public FloatParameter minExposure = new FloatParameter(-8f);

        [Tooltip("Upper exposure clamp in EV100.")]
        public FloatParameter maxExposure = new FloatParameter(8f);

        [Tooltip("Exposure offset applied to the metered target, in EV.")]
        public FloatParameter exposureCompensation = new FloatParameter(0f);

        [Tooltip("Adaptation speed when the scene gets brighter (per second).")]
        public MinFloatParameter adaptationSpeedUp = new MinFloatParameter(3f, 0f);

        [Tooltip("Adaptation speed when the scene gets darker (per second).")]
        public MinFloatParameter adaptationSpeedDown = new MinFloatParameter(1f, 0f);

        [Header("Histogram")]
        [Tooltip("Lowest scene luminance mapped into the histogram, in EV.")]
        public FloatParameter histogramMinEV = new FloatParameter(-10f);

        [Tooltip("Highest scene luminance mapped into the histogram, in EV.")]
        public FloatParameter histogramMaxEV = new FloatParameter(10f);

        [Tooltip("Reject the darkest fraction of metered pixels, in percent.")]
        public ClampedFloatParameter histogramLowPercent = new ClampedFloatParameter(40f, 0f, 100f);

        [Tooltip("Reject the brightest fraction of metered pixels, in percent.")]
        public ClampedFloatParameter histogramHighPercent = new ClampedFloatParameter(90f, 0f, 100f);

        [Header("Debug")]
        [Tooltip("Draw the live luminance histogram and the current exposure on screen.")]
        public BoolParameter showDebugOverlay = new BoolParameter(false);

        /// <summary>True when the effect should run this frame.</summary>
        public bool IsActive() => active;
    }
}
