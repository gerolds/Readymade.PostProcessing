// ADR: P0 smoke — the module compiles and the Volume gate defaults off (drop-in
// is opt-in only). P1 adds the ID-packing contract (size-class high bits + id low
// bits, reserved 0). Real PlayMode coverage (ID prepass, seam-color delta,
// collision recompute) lands in P5.
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Readymade.PostProcessing
{
    public sealed class MeshBlendTests
    {
        [Test]
        public void VolumeComponent_DefaultsInactive()
        {
            var component = ScriptableObject.CreateInstance<MeshBlendVolumeComponent>();
            try
            {
                Assert.IsFalse(component.IsActive(), "MeshBlend must default to disabled — the feature is opt-in.");
            }
            finally
            {
                Object.DestroyImmediate(component);
            }
        }

        [Test]
        public void IsActive_RequiresPositiveWidthOrDebug()
        {
            var c = ScriptableObject.CreateInstance<MeshBlendVolumeComponent>();
            try
            {
                c.enabled.value = true;
                c.blendWidths.value = Vector4.zero;
                c.debugMode.value = MeshBlendDebugMode.Normal;
                Assert.IsFalse(c.IsActive(), "zero widths + no debug must be inactive.");

                c.blendWidths.value = new Vector4(0.02f, 0.03f, 0.04f, 0.05f);
                Assert.IsTrue(c.IsActive(), "a positive per-class width must activate.");

                c.blendWidths.value = Vector4.zero;
                c.debugMode.value = MeshBlendDebugMode.Ids;
                Assert.IsTrue(c.IsActive(), "a debug mode keeps it active at zero width.");

                c.enabled.value = false;
                Assert.IsFalse(c.IsActive(), "disabled is inactive regardless of other params.");
            }
            finally
            {
                Object.DestroyImmediate(c);
            }
        }

        [Test]
        public void MeshBlendObject_PacksSizeClassInHighBits()
        {
            var go = new GameObject("mb");
            try
            {
                var mb = go.AddComponent<MeshBlendObject>();
                mb.SizeClass = MeshBlendSizeClass.Large; // 2
                mb.BlendId = 5;
                Assert.AreEqual((2 << 6) | 5, mb.Packed, "size class must occupy bits[7:6], id bits[5:0].");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void MeshBlendObject_DefaultsToNonZeroPackedId()
        {
            var go = new GameObject("mb");
            try
            {
                var mb = go.AddComponent<MeshBlendObject>();
                Assert.AreNotEqual(0, mb.Packed, "a tagged object must not pack to the reserved 'no blendable' value 0.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void MeshBlendObject_ClampsIdToSixBits()
        {
            var go = new GameObject("mb");
            try
            {
                var mb = go.AddComponent<MeshBlendObject>();
                mb.SizeClass = MeshBlendSizeClass.Small; // 0 -> packed == id
                mb.BlendId = 999;                        // clamped to 63
                Assert.AreEqual(63, mb.Packed, "id must clamp to 0..63 (6 bits).");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // --- P4: collision-free coloring -------------------------------------------------

        static int[] Color(int n, int[] sizeClass, int[] pinned, (int, int)[] edges, out int forced)
        {
            var outPacked = new int[n];
            forced = MeshBlendColoring.Assign(n, sizeClass, pinned, edges, outPacked);
            return outPacked;
        }

        static int[] Free(int n)
        {
            var p = new int[n];
            for (int i = 0; i < n; i++) p[i] = -1;
            return p;
        }

        [Test]
        public void Coloring_TouchingSameClass_DiffersById()
        {
            int[] outP = Color(2, new[] { 1, 1 }, Free(2), new[] { (0, 1) }, out _);
            Assert.AreNotEqual(outP[0], outP[1], "two touching same-size objects must get different ids.");
        }

        [Test]
        public void Coloring_NonTouchingSameClass_MayShare()
        {
            int[] outP = Color(2, new[] { 1, 1 }, Free(2), System.Array.Empty<(int, int)>(), out _);
            Assert.AreEqual(outP[0], outP[1], "non-touching same-size objects share the smallest id (no constraint).");
        }

        [Test]
        public void Coloring_CliqueOfThree_AllDistinct()
        {
            int[] outP = Color(3, new[] { 1, 1, 1 }, Free(3), new[] { (0, 1), (1, 2), (0, 2) }, out _);
            Assert.AreNotEqual(outP[0], outP[1]);
            Assert.AreNotEqual(outP[1], outP[2]);
            Assert.AreNotEqual(outP[0], outP[2]);
        }

        [Test]
        public void Coloring_DifferentClassesTouching_PackedDiffer()
        {
            // Different size classes occupy different high bits, so they never collide
            // and need no edge constraint — but their packed bytes still differ (blendable).
            int[] outP = Color(2, new[] { 0, 1 }, Free(2), new[] { (0, 1) }, out _);
            Assert.AreNotEqual(outP[0], outP[1]);
            Assert.AreEqual(0, MeshBlendColoring.SizeClassOf(outP[0]));
            Assert.AreEqual(1, MeshBlendColoring.SizeClassOf(outP[1]));
        }

        [Test]
        public void Coloring_PinnedNeighbour_IsAvoided()
        {
            int pinnedTerrain = MeshBlendColoring.Pack(1, 5);
            int[] outP = Color(2, new[] { 1, 1 }, new[] { pinnedTerrain, -1 }, new[] { (0, 1) }, out _);
            Assert.AreEqual(pinnedTerrain, outP[0], "pinned node keeps its id.");
            Assert.AreNotEqual(5, MeshBlendColoring.LowId(outP[1]), "free neighbour must avoid the pinned id.");
        }

        [Test]
        public void Coloring_OverflowingClique_ReportsForcedCollision()
        {
            const int n = MeshBlendColoring.IdsPerClass + 1; // 65 in a 64-id class
            var sc = new int[n];
            for (int i = 0; i < n; i++) sc[i] = 1;
            var edges = new List<(int, int)>();
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    edges.Add((i, j));
            Color(n, sc, Free(n), edges.ToArray(), out int forced);
            Assert.GreaterOrEqual(forced, 1, "a clique larger than the id space must report a forced collision.");
        }

        [Test]
        public void Coloring_SizeClassZero_NeverPacksReservedZero()
        {
            int[] outP = Color(1, new[] { 0 }, Free(1), System.Array.Empty<(int, int)>(), out _);
            Assert.AreNotEqual(MeshBlendColoring.ReservedPacked, outP[0], "size class 0 must skip id 0 so it never packs to the reserved 'no blendable' value.");
        }
    }
}
