// ADR: Clear→Build→Reduce→Apply as discrete render-graph passes so the compiler inserts UAV barriers; GPU-only, no readback.
// ADR: Apply runs pre-post and swaps cameraColor so bloom/tonemap see the exposed image. Per-camera buffer bound via MPB (no shared-material race).
// ADR: Inject at AfterRenderingTransparents — color is HDR linear there and ordering vs URP's post pass is unambiguous.
#nullable enable
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Readymade.PostProcessing
{
    internal sealed class AutoExposurePass : ScriptableRenderPass
    {
        static class ShaderIDs
        {
            public static readonly int Source = Shader.PropertyToID("_Source");
            public static readonly int SourceTex = Shader.PropertyToID("_SourceTex");
            public static readonly int SourceSize = Shader.PropertyToID("_SourceSize");
            public static readonly int Histogram = Shader.PropertyToID("_Histogram");
            public static readonly int Exposure = Shader.PropertyToID("_Exposure");
            public static readonly int ExposureBuffer = Shader.PropertyToID("_ExposureBuffer");
            public static readonly int EvRange = Shader.PropertyToID("_EvRange");
            public static readonly int MeterParams = Shader.PropertyToID("_MeterParams");
            public static readonly int CenterBias = Shader.PropertyToID("_CenterBias");
            public static readonly int Percent = Shader.PropertyToID("_Percent");
            public static readonly int AdaptParams = Shader.PropertyToID("_AdaptParams");
            public static readonly int ExposureLimits = Shader.PropertyToID("_ExposureLimits");
            public static readonly int ExposureComp = Shader.PropertyToID("_ExposureComp");
            public static readonly int MiddleGrey = Shader.PropertyToID("_MiddleGrey");
            public static readonly int UseFixed = Shader.PropertyToID("_UseFixed");
            public static readonly int FixedMultiplier = Shader.PropertyToID("_FixedMultiplier");
        }

        const float kMiddleGrey = 0.18f;

        ComputeShader? m_Compute;
        Material? m_ApplyMaterial;
        PerCameraStateStore? m_Store;
        int m_KClear = -1;
        int m_KBuild = -1;
        int m_KReduce = -1;

        public AutoExposurePass()
        {
        }

        public void Setup(ComputeShader? compute, Material? applyMaterial, PerCameraStateStore store)
        {
            m_ApplyMaterial = applyMaterial;
            m_Store = store;

            if (compute != m_Compute)
            {
                m_Compute = compute;
                m_KClear = m_KBuild = m_KReduce = -1;
            }

            if (m_Compute != null && m_KClear < 0)
            {
                m_KClear = m_Compute.FindKernel("KClear");
                m_KBuild = m_Compute.FindKernel("KBuild");
                m_KReduce = m_Compute.FindKernel("KReduce");
            }
        }

        sealed class ComputePassData
        {
            public ComputeShader compute = null!;
            public int kernel;
            public TextureHandle source;
            public GraphicsBuffer histogram = null!;
            public GraphicsBuffer exposure = null!;
            public Vector4 sourceSize;
            public Vector2 evRange;
            public Vector4 meterParams;
            public float centerBias;
            public Vector2 percent;
            public Vector4 adaptParams;
            public Vector2 exposureLimits;
            public float exposureComp;
            public int groupsX;
            public int groupsY;
        }

        sealed class ApplyPassData
        {
            public Material material = null!;
            public TextureHandle source;
            public GraphicsBuffer exposure = null!;
            public bool useFixed;
            public float fixedMultiplier;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_ApplyMaterial == null || m_Store == null)
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            if (resourceData.isActiveTargetBackBuffer)
                return;

            TextureHandle source = resourceData.cameraColor;
            if (!source.IsValid())
                return;

            AutoExposureVolumeComponent settings = VolumeManager.instance.stack.GetComponent<AutoExposureVolumeComponent>();
            if (settings == null || !settings.IsActive())
                return;

            bool automatic = settings.mode.value == ExposureMode.Automatic
                             && m_Compute != null
                             && m_KBuild >= 0;

            AutoExposureState state = m_Store.GetOrCreate(cameraData.camera);
            TextureDesc srcDesc = renderGraph.GetTextureDesc(source);
            var sourceSize = new Vector4(srcDesc.width, srcDesc.height, 1f / srcDesc.width, 1f / srcDesc.height);

            BufferHandle exposureHandle = renderGraph.ImportBuffer(state.Exposure);

            if (automatic)
            {
                ResolveMetering(settings, out Vector4 meterParams, out float centerBias);
                float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f);
                var evRange = new Vector2(settings.histogramMinEV.value, settings.histogramMaxEV.value);
                var percent = new Vector2(settings.histogramLowPercent.value / 100f, settings.histogramHighPercent.value / 100f);
                var adaptParams = new Vector4(dt, settings.adaptationSpeedUp.value, settings.adaptationSpeedDown.value, state.NeedsReset ? 1f : 0f);
                var exposureLimits = new Vector2(settings.minExposure.value, settings.maxExposure.value);
                BufferHandle histogramHandle = renderGraph.ImportBuffer(state.Histogram);

                // Clear
                using (var builder = renderGraph.AddComputePass<ComputePassData>("AutoExposure.Clear", out ComputePassData data))
                {
                    data.compute = m_Compute!;
                    data.kernel = m_KClear;
                    data.histogram = state.Histogram;
                    builder.UseBuffer(histogramHandle, AccessFlags.Write);
                    builder.SetRenderFunc(static (ComputePassData d, ComputeGraphContext ctx) =>
                    {
                        ctx.cmd.SetComputeBufferParam(d.compute, d.kernel, ShaderIDs.Histogram, d.histogram);
                        ctx.cmd.DispatchCompute(d.compute, d.kernel, 4, 1, 1); // 256 / 64
                    });
                }

                // Build
                using (var builder = renderGraph.AddComputePass<ComputePassData>("AutoExposure.Build", out ComputePassData data))
                {
                    data.compute = m_Compute!;
                    data.kernel = m_KBuild;
                    data.source = source;
                    data.histogram = state.Histogram;
                    data.sourceSize = sourceSize;
                    data.evRange = evRange;
                    data.meterParams = meterParams;
                    data.centerBias = centerBias;
                    data.groupsX = Mathf.CeilToInt(srcDesc.width / 16f);
                    data.groupsY = Mathf.CeilToInt(srcDesc.height / 16f);
                    builder.UseTexture(source, AccessFlags.Read);
                    builder.UseBuffer(histogramHandle, AccessFlags.ReadWrite);
                    builder.SetRenderFunc(static (ComputePassData d, ComputeGraphContext ctx) =>
                    {
                        ComputeCommandBuffer cmd = ctx.cmd;
                        cmd.SetComputeTextureParam(d.compute, d.kernel, ShaderIDs.Source, d.source);
                        cmd.SetComputeBufferParam(d.compute, d.kernel, ShaderIDs.Histogram, d.histogram);
                        cmd.SetComputeVectorParam(d.compute, ShaderIDs.SourceSize, d.sourceSize);
                        cmd.SetComputeVectorParam(d.compute, ShaderIDs.EvRange, d.evRange);
                        cmd.SetComputeVectorParam(d.compute, ShaderIDs.MeterParams, d.meterParams);
                        cmd.SetComputeFloatParam(d.compute, ShaderIDs.CenterBias, d.centerBias);
                        cmd.DispatchCompute(d.compute, d.kernel, d.groupsX, d.groupsY, 1);
                    });
                }

                // Reduce
                using (var builder = renderGraph.AddComputePass<ComputePassData>("AutoExposure.Reduce", out ComputePassData data))
                {
                    data.compute = m_Compute!;
                    data.kernel = m_KReduce;
                    data.histogram = state.Histogram;
                    data.exposure = state.Exposure;
                    data.evRange = evRange;
                    data.percent = percent;
                    data.adaptParams = adaptParams;
                    data.exposureLimits = exposureLimits;
                    data.exposureComp = settings.exposureCompensation.value;
                    builder.UseBuffer(histogramHandle, AccessFlags.Read);
                    builder.UseBuffer(exposureHandle, AccessFlags.ReadWrite);
                    builder.SetRenderFunc(static (ComputePassData d, ComputeGraphContext ctx) =>
                    {
                        ComputeCommandBuffer cmd = ctx.cmd;
                        cmd.SetComputeBufferParam(d.compute, d.kernel, ShaderIDs.Histogram, d.histogram);
                        cmd.SetComputeBufferParam(d.compute, d.kernel, ShaderIDs.Exposure, d.exposure);
                        cmd.SetComputeVectorParam(d.compute, ShaderIDs.EvRange, d.evRange);
                        cmd.SetComputeVectorParam(d.compute, ShaderIDs.Percent, d.percent);
                        cmd.SetComputeVectorParam(d.compute, ShaderIDs.AdaptParams, d.adaptParams);
                        cmd.SetComputeVectorParam(d.compute, ShaderIDs.ExposureLimits, d.exposureLimits);
                        cmd.SetComputeFloatParam(d.compute, ShaderIDs.ExposureComp, d.exposureComp);
                        cmd.SetComputeFloatParam(d.compute, ShaderIDs.MiddleGrey, kMiddleGrey);
                        cmd.DispatchCompute(d.compute, d.kernel, 1, 1, 1);
                    });
                }

                state.NeedsReset = false;
            }

            // Apply (pre-post multiply) → new color target
            TextureDesc dstDesc = srcDesc;
            dstDesc.name = "_AutoExposureColor";
            dstDesc.clearBuffer = false;
            TextureHandle dest = renderGraph.CreateTexture(dstDesc);

            using (var builder = renderGraph.AddRasterRenderPass<ApplyPassData>("AutoExposure.Apply", out ApplyPassData data))
            {
                data.material = m_ApplyMaterial;
                data.source = source;
                data.exposure = state.Exposure;
                data.useFixed = !automatic;
                data.fixedMultiplier = Mathf.Pow(2f, settings.fixedExposure.value);

                builder.UseTexture(source, AccessFlags.Read);
                if (automatic)
                    builder.UseBuffer(exposureHandle, AccessFlags.Read);
                builder.SetRenderAttachment(dest, 0, AccessFlags.Write);

                builder.SetRenderFunc(static (ApplyPassData d, RasterGraphContext ctx) =>
                {
                    MaterialPropertyBlock mpb = ctx.renderGraphPool.GetTempMaterialPropertyBlock();
                    mpb.SetTexture(ShaderIDs.SourceTex, (Texture)d.source);
                    mpb.SetFloat(ShaderIDs.UseFixed, d.useFixed ? 1f : 0f);
                    if (d.useFixed)
                        mpb.SetFloat(ShaderIDs.FixedMultiplier, d.fixedMultiplier);
                    else
                        mpb.SetBuffer(ShaderIDs.ExposureBuffer, d.exposure);

                    ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, 0, MeshTopology.Triangles, 3, 1, mpb);
                });
            }

            resourceData.cameraColor = dest;
        }

        static void ResolveMetering(AutoExposureVolumeComponent s, out Vector4 meterParams, out float centerBias)
        {
            Vector2 c = s.meterCenter.value;
            float size = s.meterSize.value;
            switch (s.metering.value)
            {
                case MeteringMode.Average:
                    meterParams = new Vector4(c.x, c.y, 0f, 100f);
                    centerBias = 0f;
                    break;
                case MeteringMode.Spot:
                    meterParams = new Vector4(c.x, c.y, 0f, size * 0.3f);
                    centerBias = 1f;
                    break;
                default: // CenterWeighted
                    meterParams = new Vector4(c.x, c.y, 0f, size);
                    centerBias = s.centerStrength.value;
                    break;
            }
        }
    }
}
