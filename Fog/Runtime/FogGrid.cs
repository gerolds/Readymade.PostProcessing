// ADR: Froxel grid dimensions are a pure function of FogQuality — kept allocation-free and side-effect-free so the pass, the feature, and edit-mode tests share one source of truth.
#nullable enable
using UnityEngine;

namespace Readymade.PostProcessing
{
    /// <summary>
    /// Resolves the froxel volume resolution for a <see cref="FogQuality"/> tier. XY tiles the screen,
    /// Z slices the view frustum from the camera near plane to the fog max distance.
    /// </summary>
    internal static class FogGrid
    {
        /// <summary>Froxel counts (x, y, z) for the given quality tier. Matches the tier comments on <see cref="FogQuality"/>.</summary>
        public static Vector3Int Resolve(FogQuality quality)
        {
            switch (quality)
            {
                case FogQuality.Low: return new Vector3Int(128, 72, 48);
                case FogQuality.High: return new Vector3Int(240, 135, 96);
                default: return new Vector3Int(160, 90, 64); // Medium
            }
        }
    }
}
