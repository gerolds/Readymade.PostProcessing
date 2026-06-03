// ADR: Drop-in per-renderer ID authoring. Packs size-class (bits[7:6]) + blend-id
// (bits[5:0]) into the byte the ID prepass reads via _BlendId on the renderer's
// MaterialPropertyBlock. Manual IDs in P1; MeshBlendActivator (P4) overwrites them
// with collision-free assignments through this same MPB path.
// ADR: GetPropertyBlock before SetFloat — preserves any other per-renderer MPB
// props (e.g. splat ids) instead of clobbering the block.
// SPIKE: Manual-mode path (s_ManualMode) drops the MPB entirely and feeds a static
// registry the prepass reads, so the prop stays SRP-batchable in its MAIN pass (an
// MPB de-batches ALL of a renderer's passes, not just the id write) AND gets per-submesh
// ids for free. Validates the no-MPB architecture before productizing. Generic props only
// (SetPropertyBlock(null) clobbers any other per-renderer MPB props — fine when there are none).
#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace Readymade.PostProcessing
{
    /// <summary>Size class for the seam blend; packed into the high 2 bits of the ID byte (4 classes).</summary>
    public enum MeshBlendSizeClass
    {
        Small,
        Medium,
        Large,
        Huge
    }

    /// <summary>
    /// Tags a renderer as a mesh-blend participant. Pushes a packed per-object ID onto the renderer's
    /// <see cref="MaterialPropertyBlock"/> so the ID prepass can write an occlusion-correct,
    /// interpolation-free ID buffer. Drop-in: no scene wiring required beyond adding this component.
    /// </summary>
    // SPIKE: ExecuteAlways so OnEnable/OnDisable (registry add/remove) run in EDIT mode too. Without it,
    // OnEnable never fires outside Play mode, so the manual prepass's registry stays empty and the id
    // buffer renders all-black in the Scene/Game view. (The MPB path was unaffected — it applies via
    // OnValidate, which does run in edit mode.)
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public sealed class MeshBlendObject : MonoBehaviour
    {
        [SerializeField, Range(0, 63)]
        [Tooltip("Blend ID within the size class (0..63). Touching objects must differ; the Activator (P4) assigns these collision-free. 0 with the Small class packs to the reserved 'no blendable' value — avoid it.")]
        private int m_BlendId = 1;

        [SerializeField]
        [Tooltip("Size class — selects the world-space blend width (per-class absolute widths on the Volume) and separates the ID space into 4 bands.")]
        private MeshBlendSizeClass m_SizeClass = MeshBlendSizeClass.Medium;

        [SerializeField]
        [Tooltip("Blend across this object's OWN internal material/submesh boundaries (each submesh gets a distinct ID). Off keeps intentional hard material lines crisp. Only takes effect under the manual ID prepass.")]
        private bool m_BlendInternal = false;

        private static readonly int s_BlendIdProp = Shader.PropertyToID("_BlendId");

        // SPIKE: registry + mode. The prepass reads this when manual mode is on.
        private static readonly List<MeshBlendObject> s_Active = new List<MeshBlendObject>();
        private static bool s_ManualMode;
        public static IReadOnlyList<MeshBlendObject> Active => s_Active;

        private Renderer? m_Renderer;
        private MaterialPropertyBlock? m_Block;

        /// <summary>Blend ID (0..63) within the size class.</summary>
        public int BlendId
        {
            get => m_BlendId;
            set { m_BlendId = Mathf.Clamp(value, 0, 63); Apply(); }
        }

        /// <summary>Size class (4 bands).</summary>
        public MeshBlendSizeClass SizeClass
        {
            get => m_SizeClass;
            set { m_SizeClass = value; Apply(); }
        }

        /// <summary>The packed ID byte: bits[7:6] = size class, bits[5:0] = blend id. 0 = no blendable.</summary>
        public int Packed => MeshBlendColoring.Pack((int)m_SizeClass, m_BlendId);

        /// <summary>Renderer this tag drives (resolved lazily).</summary>
        public Renderer? Renderer
        {
            get
            {
                if (m_Renderer == null)
                    m_Renderer = GetComponent<Renderer>();
                return m_Renderer;
            }
        }

        // SPIKE: submesh count from the mesh (MeshFilter or SkinnedMeshRenderer); 0 if none.
        public int SubmeshCount
        {
            get
            {
                Mesh? mesh = ResolveMesh();
                return mesh != null ? mesh.subMeshCount : 0;
            }
        }

        // Per-submesh packed id. Submesh 0 always keeps the object's base id (object↔object behaviour
        // preserved). When BlendInternal is on, each further submesh offsets by +1 within the class so
        // the INTERNAL material boundary becomes an id boundary; when off, every submesh shares the base
        // id (no internal seam — intentional hard material lines stay crisp). Collision-free coloring
        // across objects is a later (Activator) concern.
        public int PackedForSubmesh(int submesh)
        {
            int offset = m_BlendInternal ? submesh : 0;
            int id = m_BlendId + offset;
            id = ((id % MeshBlendColoring.IdsPerClass) + MeshBlendColoring.IdsPerClass) % MeshBlendColoring.IdsPerClass;
            if (id == 0) id = 1; // class 0 + id 0 == reserved "no blendable"
            return MeshBlendColoring.Pack((int)m_SizeClass, id);
        }

        private Mesh? ResolveMesh()
        {
            if (Renderer is SkinnedMeshRenderer smr)
                return smr.sharedMesh;
            MeshFilter? mf = GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        // SPIKE: flip the whole feature between the MPB path (RendererList prepass) and the manual path
        // (registry + per-submesh draws, no MPB). Re-applies every live tag so the MPB state matches.
        public static void SetManualMode(bool on)
        {
            if (s_ManualMode == on)
                return;
            s_ManualMode = on;
            foreach (MeshBlendObject o in s_Active)
                o.Apply();
        }

        private void OnEnable()
        {
            if (!s_Active.Contains(this))
                s_Active.Add(this);
            Apply();
        }

        private void OnDisable()
        {
            s_Active.Remove(this);
        }

        private void OnValidate()
        {
            if (isActiveAndEnabled)
                Apply();
        }

        /// <summary>Pushes the packed ID to the renderer's property block. Safe to call repeatedly.</summary>
        public void Apply()
        {
            if (Renderer == null)
                return;

            // SPIKE: manual mode → no MPB on the renderer (keeps the main pass SRP-batchable). The
            // prepass reads the id from the registry instead. Generic props only — this removes the
            // WHOLE block, so it would clobber other per-renderer MPB props if any existed.
            if (s_ManualMode)
            {
                m_Renderer!.SetPropertyBlock(null);
                return;
            }

            m_Block ??= new MaterialPropertyBlock();
            m_Renderer!.GetPropertyBlock(m_Block);
            m_Block.SetFloat(s_BlendIdProp, Packed);
            m_Renderer.SetPropertyBlock(m_Block);
        }
    }
}
