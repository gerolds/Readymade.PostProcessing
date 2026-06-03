// ADR: Observability overlay — separate pass at AfterRenderingPostProcessing so it draws over the final image; reads this frame's per-camera buffers.
// ADR: Off by default (volume showDebugOverlay); never on the main image's critical path.
#nullable enable
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Readymade.PostProcessing
{
    internal sealed class AutoExposureDebugPass : ScriptableRenderPass
    {
        static class ShaderIDs
        {
            public static readonly int Histogram = Shader.PropertyToID("_DebugHistogram");
            public static readonly int Exposure = Shader.PropertyToID("_DebugExposure");
            public static readonly int OverlayRect = Shader.PropertyToID("_OverlayRect");
            public static readonly int EvRange = Shader.PropertyToID("_DebugEvRange");
        }

        Material? m_Material;
        PerCameraStateStore? m_Store;

        public void Setup(Material? material, PerCameraStateStore store)
        {
            m_Material = material;
            m_Store = store;
        }

        sealed class PassData
        {
            public Material material = null!;
            public GraphicsBuffer histogram = null!;
            public GraphicsBuffer exposure = null!;
            public Vector4 rect;
            public Vector2 evRange;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_Material == null || m_Store == null)
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            AutoExposureVolumeComponent settings = VolumeManager.instance.stack.GetComponent<AutoExposureVolumeComponent>();
            if (settings == null || !settings.IsActive() || !settings.showDebugOverlay.value)
                return;
            if (settings.mode.value != ExposureMode.Automatic)
                return; // histogram only exists in automatic mode

            TextureHandle target = resourceData.activeColorTexture;
            if (!target.IsValid())
                return;

            AutoExposureState state = m_Store.GetOrCreate(cameraData.camera);
            BufferHandle histogramHandle = renderGraph.ImportBuffer(state.Histogram);
            BufferHandle exposureHandle = renderGraph.ImportBuffer(state.Exposure);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("AutoExposure.Debug", out PassData data))
            {
                data.material = m_Material;
                data.histogram = state.Histogram;
                data.exposure = state.Exposure;
                data.rect = new Vector4(0.02f, 0.02f, 0.28f, 0.16f);
                data.evRange = new Vector2(settings.histogramMinEV.value, settings.histogramMaxEV.value);

                builder.UseBuffer(histogramHandle, AccessFlags.Read);
                builder.UseBuffer(exposureHandle, AccessFlags.Read);
                builder.SetRenderAttachment(target, 0, AccessFlags.ReadWrite);

                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                {
                    MaterialPropertyBlock mpb = ctx.renderGraphPool.GetTempMaterialPropertyBlock();
                    mpb.SetBuffer(ShaderIDs.Histogram, d.histogram);
                    mpb.SetBuffer(ShaderIDs.Exposure, d.exposure);
                    mpb.SetVector(ShaderIDs.OverlayRect, d.rect);
                    mpb.SetVector(ShaderIDs.EvRange, d.evRange);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, 0, MeshTopology.Triangles, 3, 1, mpb);
                });
            }
        }
    }
}
