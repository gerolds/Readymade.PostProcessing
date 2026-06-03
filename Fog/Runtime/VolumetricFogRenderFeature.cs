// ADR: Drop-in URP feature — no dependency on Orogen *System/Services. Owns FogPerCameraStore + the single fog pass + the apply material.
// ADR: Compute/shader refs are serialized (so they ship in builds) and editor auto-binds them; apply shader has a Shader.Find fallback. Inject before post so bloom/tonemap see the in-scatter.
#nullable enable
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Readymade.PostProcessing
{
    /// <summary>
    /// Froxel volumetric fog for URP. Add to a Universal Renderer and add a <see cref="VolumetricFogVolumeComponent"/>
    /// override to a Volume Profile to enable and tune it. Responds to scene depth and the main directional light.
    /// </summary>
    [DisallowMultipleRendererFeature("Volumetric Fog")]
    public sealed class VolumetricFogRenderFeature : ScriptableRendererFeature
    {
        [SerializeField, HideInInspector] ComputeShader? m_FroxelCompute;
        [SerializeField, HideInInspector] Shader? m_ApplyShader;

        [SerializeField]
        [Tooltip("When the fog passes run. BeforeRenderingPostProcessing keeps the in-scatter in the HDR color so bloom/tonemap see it.")]
        RenderPassEvent m_InjectionPoint = RenderPassEvent.BeforeRenderingPostProcessing;

        Material? m_ApplyMaterial;
        VolumetricFogPass? m_Pass;
        FogPerCameraStore? m_Store;

        public override void Create()
        {
            m_Store ??= new FogPerCameraStore();

            if (m_ApplyShader == null)
                m_ApplyShader = Shader.Find("Hidden/Readymade/FogApply");

            if (m_ApplyMaterial == null && m_ApplyShader != null)
                m_ApplyMaterial = CoreUtils.CreateEngineMaterial(m_ApplyShader);

            m_Pass ??= new VolumetricFogPass();
            m_Pass.renderPassEvent = m_InjectionPoint;
            m_Pass.Setup(m_FroxelCompute, m_ApplyMaterial, m_Store);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_Pass == null || m_ApplyMaterial == null || m_FroxelCompute == null || m_Store == null)
                return;

            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Reflection || cameraType == CameraType.Preview)
                return;

            m_Store.Prune();
            m_Pass.renderPassEvent = m_InjectionPoint;
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Store?.Dispose();
            m_Store = null;
            CoreUtils.Destroy(m_ApplyMaterial);
            m_ApplyMaterial = null;
            m_Pass = null;
        }
    }
}
