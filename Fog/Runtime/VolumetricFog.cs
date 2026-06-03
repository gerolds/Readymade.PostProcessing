// ADR: The one sanctioned render-loop-boundary static for this module (cf.
// Shader.SetGlobalTexture). Render features run outside the scene graph / IoC,
// so game code signals camera cuts here. P3 will add SetFieldSource on the same
// seam. Auto-detection via view-proj delta is the primary; this is a precise opt-in.

using UnityEngine;

namespace Readymade.PostProcessing
{
    /// <summary>
    /// Process-wide control surface for the volumetric fog render feature.
    /// Game code that performs camera cuts/teleports should call
    /// <see cref="NotifyCameraCut"/> so temporal history is dropped that frame.
    /// </summary>
    public static class VolumetricFog
    {
        static int s_CutFrame = -1;

        /// <summary>Drop temporal fog history on the next rendered frame.</summary>
        public static void NotifyCameraCut()
        {
            s_CutFrame = Time.frameCount;
        }

        /// <summary>True if a cut was requested for the frame currently being rendered.</summary>
        internal static bool ConsumeCutThisFrame()
        {
            return s_CutFrame == Time.frameCount;
        }
    }
}
