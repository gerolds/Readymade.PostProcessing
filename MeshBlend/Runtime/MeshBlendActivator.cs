// ADR: Drop-in collision-free ID assignment — removes manual ID typing. Scans
// blendable renderers, builds inflated-bounds adjacency (O(N^2), fine for sparse
// props), greedy-colors via MeshBlendColoring, writes ids back through each
// renderer's MaterialPropertyBlock. Two participant sources: tagged MeshBlendObjects
// (default) or every renderer on a layer (zero per-object components). Optional shared
// pinned terrain id so intra-terrain chunk boundaries don't manufacture false seams.
// ADR: Static-scene drop-in only. Streamed-prop assignment that tracks the residency
// ring + persists ids is the deferred Orogen MeshBlendSystem; re-run Recompute after
// scene changes here, or graduate to the System when props stream.
#nullable enable
using System.Collections.Generic;
using NaughtyAttributes;
using UnityEngine;

namespace Readymade.PostProcessing
{
    /// <summary>
    /// Assigns collision-free blend ids to scene renderers so touching same-size objects
    /// always differ — no manual ID authoring. Run <see cref="Recompute"/> on enable, on
    /// demand (context menu), or after spawning blendables.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MeshBlendActivator : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Layer mode: color every renderer on the Blendable layers (no per-object component). Off: color tagged MeshBlendObjects.")]
        private bool m_LayerMode;

        [SerializeField]
        [Tooltip("Renderers on these layers are blendable (layer mode only).")]
        private LayerMask m_BlendableLayers = 0;

        [SerializeField, Range(0, 3)]
        [Tooltip("Size class assigned to layer-mode renderers.")]
        private int m_LayerSizeClass = 1;

        [SerializeField, Min(0f)]
        [Tooltip("Bounds inflation (meters) for adjacency — objects within this gap count as touching.")]
        private float m_AdjacencyPadding = 0.05f;

        [Header("Terrain (optional)")]
        [SerializeField]
        [Tooltip("Pin renderers on the terrain layers to one shared id so intra-terrain chunk boundaries don't blend, while props still blend against terrain.")]
        private bool m_ReserveTerrain;

        [SerializeField] private LayerMask m_TerrainLayers = 0;

        [SerializeField, Range(0, 3)] private int m_TerrainSizeClass = 3;

        [SerializeField, Range(0, 63)] private int m_TerrainId = 63;

        private static readonly int s_BlendIdProp = Shader.PropertyToID("_BlendId");

        private readonly List<Renderer> m_Renderers = new List<Renderer>();
        private readonly List<MeshBlendObject?> m_Objects = new List<MeshBlendObject?>();
        private readonly List<int> m_SizeClasses = new List<int>();
        private readonly List<int> m_Pinned = new List<int>();
        private MaterialPropertyBlock? m_Block;

        private void OnEnable() => Recompute();

        [Button("Recompute IDs")]
        public void Recompute()
        {
            Collect();
            int n = m_Renderers.Count;
            if (n == 0)
                return;

            (int, int)[] edges = BuildAdjacency(n);
            int[] outPacked = new int[n];
            int forced = MeshBlendColoring.Assign(n, m_SizeClasses.ToArray(), m_Pinned.ToArray(), edges, outPacked);
            if (forced > 0)
                Debug.LogWarning($"[MeshBlend] {forced} forced ID collision(s): more than {MeshBlendColoring.IdsPerClass} mutually-touching same-size objects. Seams between the colliding pair(s) won't blend — split their size classes.", this);

            WriteBack(n, outPacked);
        }

        private void Collect()
        {
            m_Renderers.Clear();
            m_Objects.Clear();
            m_SizeClasses.Clear();
            m_Pinned.Clear();

            if (m_LayerMode)
            {
                foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    if ((m_BlendableLayers.value & (1 << r.gameObject.layer)) == 0)
                        continue;
                    Add(r, null, m_LayerSizeClass, -1);
                }
            }
            else
            {
                foreach (MeshBlendObject o in FindObjectsByType<MeshBlendObject>(FindObjectsSortMode.None))
                {
                    Renderer? r = o.GetComponent<Renderer>();
                    if (r != null)
                        Add(r, o, (int)o.SizeClass, -1);
                }
            }

            if (m_ReserveTerrain)
            {
                int terrainPacked = MeshBlendColoring.Pack(m_TerrainSizeClass, m_TerrainId);
                foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    if ((m_TerrainLayers.value & (1 << r.gameObject.layer)) == 0 || m_Renderers.Contains(r))
                        continue;
                    Add(r, null, m_TerrainSizeClass, terrainPacked);
                }
            }
        }

        private void Add(Renderer r, MeshBlendObject? o, int sizeClass, int pinned)
        {
            m_Renderers.Add(r);
            m_Objects.Add(o);
            m_SizeClasses.Add(sizeClass);
            m_Pinned.Add(pinned);
        }

        private (int, int)[] BuildAdjacency(int n)
        {
            Bounds[] bounds = new Bounds[n];
            for (int i = 0; i < n; i++)
            {
                Bounds b = m_Renderers[i].bounds;
                b.Expand(m_AdjacencyPadding * 2f); // Expand adds to full size; padding is per-side
                bounds[i] = b;
            }

            var edges = new List<(int, int)>();
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    if (bounds[i].Intersects(bounds[j]))
                        edges.Add((i, j));
            return edges.ToArray();
        }

        private void WriteBack(int n, int[] outPacked)
        {
            m_Block ??= new MaterialPropertyBlock();
            for (int i = 0; i < n; i++)
            {
                MeshBlendObject? o = m_Objects[i];
                if (o != null)
                {
                    // Object owns its authored size class; push only the colored id.
                    o.BlendId = MeshBlendColoring.LowId(outPacked[i]);
                    continue;
                }

                Renderer r = m_Renderers[i];
                r.GetPropertyBlock(m_Block);
                m_Block.SetFloat(s_BlendIdProp, outPacked[i]);
                r.SetPropertyBlock(m_Block);
            }
        }
    }
}
