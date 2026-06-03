// ADR: Single source of truth for the C# ID encoding + the collision-free assignment.
// Encoding mirrors the shaders (MeshBlendId/MeshBlend/Debug): bits[7:6]=size class,
// bits[5:0]=id, whole-byte 0 = "no blendable". Coloring is per-size-class — different
// classes occupy different high bits so they never collide and need no edge constraint.
// Pure + allocation-light so MeshBlendActivator (and tests) can exercise it directly.
#nullable enable
using System;
using System.Collections.Generic;

namespace Readymade.PostProcessing
{
    /// <summary>
    /// Greedy graph-coloring for collision-free blend ids: touching objects of the same
    /// size class receive different ids so every real seam is detectable. Pinned nodes
    /// (e.g. a shared terrain id) keep their value and constrain their neighbours.
    /// </summary>
    internal static class MeshBlendColoring
    {
        public const int IdsPerClass = 64;    // 6 bits
        public const int ReservedPacked = 0;  // whole-byte 0 == no blendable

        public static int Pack(int sizeClass, int id) => ((sizeClass & 0x3) << 6) | (id & 0x3F);
        public static int LowId(int packed) => packed & 0x3F;
        public static int SizeClassOf(int packed) => (packed >> 6) & 0x3;

        /// <summary>
        /// Colors every free node (<paramref name="pinnedPacked"/>[i] &lt; 0) so no two
        /// adjacent same-size nodes share a packed value; pinned nodes keep their id.
        /// Welsh-Powell order (descending degree) for fewer collisions. Returns the count
        /// of forced collisions — cliques larger than the 64-id space that could not resolve.
        /// </summary>
        public static int Assign(int count, int[] sizeClass, int[] pinnedPacked, (int a, int b)[] edges, int[] outPacked)
        {
            var adj = new List<int>[count];
            for (int i = 0; i < count; i++)
                adj[i] = new List<int>();
            foreach ((int a, int b) in edges)
            {
                if (a == b || a < 0 || b < 0 || a >= count || b >= count)
                    continue;
                adj[a].Add(b);
                adj[b].Add(a);
            }

            for (int i = 0; i < count; i++)
                outPacked[i] = pinnedPacked[i] >= 0 ? pinnedPacked[i] : -1;

            var order = new List<int>();
            for (int i = 0; i < count; i++)
                if (pinnedPacked[i] < 0)
                    order.Add(i);
            order.Sort((x, y) =>
            {
                int byDegree = adj[y].Count.CompareTo(adj[x].Count);
                return byDegree != 0 ? byDegree : x.CompareTo(y);
            });

            int forced = 0;
            Span<bool> used = stackalloc bool[IdsPerClass];
            foreach (int v in order)
            {
                int sc = sizeClass[v] & 0x3;
                used.Clear();
                foreach (int u in adj[v])
                {
                    if (outPacked[u] < 0 || SizeClassOf(outPacked[u]) != sc)
                        continue;
                    used[LowId(outPacked[u])] = true;
                }

                int start = sc == 0 ? 1 : 0; // sc0 + id0 == reserved 0
                int chosen = -1;
                for (int id = start; id < IdsPerClass; id++)
                {
                    if (!used[id])
                    {
                        chosen = id;
                        break;
                    }
                }

                if (chosen < 0)
                {
                    forced++;
                    chosen = start; // out of ids: collide on the first valid
                }

                outPacked[v] = Pack(sc, chosen);
            }

            return forced;
        }
    }
}
