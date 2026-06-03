// ADR: Drop-in URP feature — no dependency on Orogen *System/Services. Owns the
// mesh-blend pass + its materials (blend / ID-prepass / debug / JFA seed+flood).
// The blend shader carries both composite passes (search + JFA field lookup); the
// JFA material supplies the seed+flood passes behind the Volume's useJumpFlood toggle.
// ADR: Shader refs are serialized+hidden (ship in builds) and the editor auto-binds
// them by Shader.Find; Create() Shader.Find fallback covers fresh adds. Inject after
// skybox (opaque color+depth ready, before transparents) so opaque seams blend;
// transparents are intentionally excluded.
// ADR: Object selection is a serialized LayerMask (decision #1). Default Everything
// is correct drop-in (untagged objects pack ID 0 = skipped); restrict to a dedicated
// layer to avoid re-drawing the whole opaque set in the prepass.
#nullable enable
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Readymade.PostProcessing
{
    /// <summary>
    /// Screen-space mesh-blend for URP: softens hard intersection seams between separately-rendered
    /// meshes. Add to a Universal Renderer and add a <see cref="MeshBlendVolumeComponent"/> override
    /// to a Volume Profile to enable and tune it. Tag participating renderers with
    /// <see cref="MeshBlendObject"/>.
    /// </summary>
    [DisallowMultipleRendererFeature("Mesh Blend")]
    public sealed class MeshBlendRenderFeature : ScriptableRendererFeature
    {
        [SerializeField, HideInInspector] private Shader? m_BlendShader;
        [SerializeField, HideInInspector] private Shader? m_IdShader;
        [SerializeField, HideInInspector] private Shader? m_DebugShader;
        [SerializeField, HideInInspector] private Shader? m_JfaShader;

        [SerializeField]
        [Tooltip("Which renderers the ID prepass draws. Everything is correct (untagged objects are skipped) but re-draws the opaque set; restrict to a dedicated MeshBlend layer for performance.")]
        private LayerMask m_LayerMask = ~0;

        [SerializeField]
        [Tooltip("When the blend pass runs. AfterRenderingSkybox blends opaque seams before transparents.")]
        private RenderPassEvent m_InjectionPoint = RenderPassEvent.AfterRenderingSkybox;

        [SerializeField]
        [Tooltip("SPIKE: manual per-submesh ID prepass with NO MaterialPropertyBlock (props stay SRP-batched in their main pass) + per-submesh internal-boundary ids. Off = the MPB + RendererList path.")]
        private bool m_SpikeManualIdPrepass;

        private Material? m_BlendMaterial;
        private Material? m_IdMaterial;
        private Material? m_DebugMaterial;
        private Material? m_JfaMaterial;
        private MeshBlendPass? m_Pass;

        public override void Create()
        {
            if (m_BlendShader == null)
                m_BlendShader = Shader.Find("Hidden/Readymade/MeshBlend");
            if (m_IdShader == null)
                m_IdShader = Shader.Find("Hidden/Readymade/MeshBlendId");
            if (m_DebugShader == null)
                m_DebugShader = Shader.Find("Hidden/Readymade/MeshBlendDebug");
            if (m_JfaShader == null)
                m_JfaShader = Shader.Find("Hidden/Readymade/MeshBlendJFA");

            if (m_BlendMaterial == null && m_BlendShader != null)
                m_BlendMaterial = CoreUtils.CreateEngineMaterial(m_BlendShader);
            if (m_IdMaterial == null && m_IdShader != null)
                m_IdMaterial = CoreUtils.CreateEngineMaterial(m_IdShader);
            if (m_DebugMaterial == null && m_DebugShader != null)
                m_DebugMaterial = CoreUtils.CreateEngineMaterial(m_DebugShader);
            if (m_JfaMaterial == null && m_JfaShader != null)
                m_JfaMaterial = CoreUtils.CreateEngineMaterial(m_JfaShader);

            m_Pass ??= new MeshBlendPass();
            m_Pass.renderPassEvent = m_InjectionPoint;
            m_Pass.SpikeManualIdPrepass = m_SpikeManualIdPrepass;
            m_Pass.Setup(m_BlendMaterial, m_IdMaterial, m_DebugMaterial, m_JfaMaterial, m_LayerMask.value);
            MeshBlendObject.SetManualMode(m_SpikeManualIdPrepass);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_Pass == null || m_BlendMaterial == null)
                return;

            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Reflection || cameraType == CameraType.Preview)
                return;

            m_Pass.renderPassEvent = m_InjectionPoint;
            m_Pass.SpikeManualIdPrepass = m_SpikeManualIdPrepass;
            m_Pass.Setup(m_BlendMaterial, m_IdMaterial, m_DebugMaterial, m_JfaMaterial, m_LayerMask.value);
            MeshBlendObject.SetManualMode(m_SpikeManualIdPrepass);
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(m_BlendMaterial);
            CoreUtils.Destroy(m_IdMaterial);
            CoreUtils.Destroy(m_DebugMaterial);
            CoreUtils.Destroy(m_JfaMaterial);
            m_BlendMaterial = null;
            m_IdMaterial = null;
            m_DebugMaterial = null;
            m_JfaMaterial = null;
            m_Pass = null;
        }
    }
}
