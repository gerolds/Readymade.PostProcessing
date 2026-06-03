// ADR: Per-camera, GPU-resident, frame-persistent exposure state — the coherence primitive. Never readback to CPU.
// ADR: Histogram buffer is per-camera too (cleared each frame) to avoid cross-camera atomic contention.
#nullable enable
using System;
using UnityEngine;

namespace Readymade.PostProcessing
{
    /// <summary>
    /// Owns the GPU buffers that survive frame-to-frame for one camera: the luminance histogram
    /// (rebuilt every frame) and the adapted exposure (EV, multiplier), which the reduce pass smooths
    /// toward the metered target. Imported into the render graph each frame; never read back to the CPU.
    /// </summary>
    internal sealed class AutoExposureState : IDisposable
    {
        public GraphicsBuffer Histogram { get; private set; }
        public GraphicsBuffer Exposure { get; private set; }

        /// <summary>When true the next reduce snaps to the target instead of adapting (first frame / camera cut).</summary>
        public bool NeedsReset;

        public int LastFrame;

        public AutoExposureState(int bins)
        {
            Histogram = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bins, sizeof(uint));
            // One element: (x = adapted EV, y = adapted multiplier). Seed to EV 0 / multiplier 1.
            Exposure = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 2);
            Exposure.SetData(new[] { new Vector2(0f, 1f) });
            NeedsReset = true;
        }

        public void Reset() => NeedsReset = true;

        public void Dispose()
        {
            Histogram?.Dispose();
            Exposure?.Dispose();
            Histogram = null!;
            Exposure = null!;
        }
    }
}
