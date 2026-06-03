// ADR: Edit-mode smoke tests for the two pure-logic seams — the Volume IsActive gate and the quality->grid mapping. GPU/visual correctness is verified in a real scene, not here.
#nullable enable
using NUnit.Framework;
using UnityEngine;

namespace Readymade.PostProcessing.Tests
{
    public sealed class VolumetricFogTests
    {
        [Test]
        public void IsActive_RequiresEnabledPositiveDensityAndDistance()
        {
            var fog = ScriptableObject.CreateInstance<VolumetricFogVolumeComponent>();
            try
            {
                Assert.IsFalse(fog.IsActive(), "disabled by default");

                fog.enabled.value = true;
                Assert.IsTrue(fog.IsActive(), "enabled with positive defaults is active");

                fog.density.value = 0f;
                Assert.IsFalse(fog.IsActive(), "zero density gates off");
            }
            finally
            {
                Object.DestroyImmediate(fog);
            }
        }

        [Test]
        public void FogGrid_TiersAreDistinctAndMonotonic()
        {
            Vector3Int low = FogGrid.Resolve(FogQuality.Low);
            Vector3Int medium = FogGrid.Resolve(FogQuality.Medium);
            Vector3Int high = FogGrid.Resolve(FogQuality.High);

            Assert.AreEqual(new Vector3Int(128, 72, 48), low);
            Assert.AreEqual(new Vector3Int(160, 90, 64), medium);
            Assert.AreEqual(new Vector3Int(240, 135, 96), high);

            long lowCount = (long)low.x * low.y * low.z;
            long medCount = (long)medium.x * medium.y * medium.z;
            long highCount = (long)high.x * high.y * high.z;
            Assert.Less(lowCount, medCount, "Medium has more froxels than Low");
            Assert.Less(medCount, highCount, "High has more froxels than Medium");
        }
    }
}
