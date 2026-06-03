// ADR: Drop-in URP feature — no dependency on Orogen *System/Services. Owns PerCameraStateStore + the single pass.
// ADR: Shader/compute refs are serialized (so they ship in builds) and auto-bound by the editor; apply shader has a Shader.Find fallback.
#nullable enable
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Readymade.PostProcessing
{
    /// <summary>
    /// Center-weighted, luminance-histogram auto exposure for URP. Add to a Universal Renderer and add an
    /// <see cref="AutoExposureSettings"/> override to a Volume Profile to configure it.
    /// </summary>
    [DisallowMultipleRendererFeature("Auto Exposure")]
    public sealed class AutoExposureRendererFeature : ScriptableRendererFeature
    {
        const int kHistogramBins = 256;

        [SerializeField, HideInInspector] ComputeShader? m_HistogramCompute;
        [SerializeField, HideInInspector] Shader? m_ApplyShader;
        [SerializeField, HideInInspector] Shader? m_DebugShader;

        [SerializeField]
        [Tooltip("When the metering and exposure-apply passes run. AfterRenderingTransparents keeps the color HDR-linear and ahead of post.")]
        RenderPassEvent m_InjectionPoint = RenderPassEvent.AfterRenderingTransparents;

        Material? m_ApplyMaterial;
        Material? m_DebugMaterial;
        AutoExposurePass? m_Pass;
        AutoExposureDebugPass? m_DebugPass;
        PerCameraStateStore? m_Store;

        public override void Create()
        {
            m_Store ??= new PerCameraStateStore(kHistogramBins);

            if (m_ApplyShader == null)
                m_ApplyShader = Shader.Find("Hidden/Readymade/AutoExposureApply");
            if (m_DebugShader == null)
                m_DebugShader = Shader.Find("Hidden/Readymade/AutoExposureDebug");

            if (m_ApplyMaterial == null && m_ApplyShader != null)
                m_ApplyMaterial = CoreUtils.CreateEngineMaterial(m_ApplyShader);
            if (m_DebugMaterial == null && m_DebugShader != null)
                m_DebugMaterial = CoreUtils.CreateEngineMaterial(m_DebugShader);

            m_Pass ??= new AutoExposurePass();
            m_Pass.renderPassEvent = m_InjectionPoint;
            m_Pass.Setup(m_HistogramCompute, m_ApplyMaterial, m_Store);

            m_DebugPass ??= new AutoExposureDebugPass();
            m_DebugPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
            m_DebugPass.Setup(m_DebugMaterial, m_Store);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_Pass == null || m_ApplyMaterial == null || m_Store == null)
                return;

            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Reflection || cameraType == CameraType.Preview)
                return;

            m_Store.Prune();
            m_Pass.renderPassEvent = m_InjectionPoint;
            renderer.EnqueuePass(m_Pass);

            if (m_DebugPass != null && m_DebugMaterial != null)
                renderer.EnqueuePass(m_DebugPass);
        }

        protected override void Dispose(bool disposing)
        {
            m_Store?.Dispose();
            m_Store = null;
            if (m_ApplyMaterial != null)
                CoreUtils.Destroy(m_ApplyMaterial);
            m_ApplyMaterial = null;
            if (m_DebugMaterial != null)
                CoreUtils.Destroy(m_DebugMaterial);
            m_DebugMaterial = null;
            m_Pass = null;
            m_DebugPass = null;
        }
    }
}
