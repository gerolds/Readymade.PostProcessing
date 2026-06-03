// ADR: One ScriptableRenderPass issues all three render-graph passes (Populate compute -> Integrate compute -> Apply raster); ordering is enforced by resource read/write deps, not RenderPassEvent juggling.
// ADR: Reads the Volume stack inside RecordRenderGraph (not AddRenderPasses) for reliability; gates on IsActive. Forces the depth texture via ConfigureInput(Depth).
// ADR: GPU view-proj uses GL.GetGPUProjectionMatrix(proj, FALSE) — the froxel uv convention is GetFullScreenTriangleTexCoord (Apply); the render-target Y-flip (true) mirrored position-dependent shading vertically (spot cones), invisible on smooth height fog. Identical on GL.
// ADR: Populate consumes resourceData.additionalShadowsTexture (UseTexture Read + explicit bind) only when URP produced one this frame (lighting+occlusion live in Populate); gates the _FOG_LOCAL_SHADOWS keyword so fog never glows through walls.
#nullable enable
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Readymade.PostProcessing
{
    internal sealed class VolumetricFogPass : ScriptableRenderPass
    {
        static class ShaderIDs
        {
            public static readonly int InvViewProj = Shader.PropertyToID("_FogInvViewProj");
            public static readonly int PrevViewProj = Shader.PropertyToID("_FogPrevViewProj");
            public static readonly int CamPos = Shader.PropertyToID("_FogCamPosWS");
            public static readonly int CamFwd = Shader.PropertyToID("_FogCamFwdWS");
            public static readonly int PrevCamPos = Shader.PropertyToID("_FogPrevCamPosWS");
            public static readonly int PrevCamFwd = Shader.PropertyToID("_FogPrevCamFwdWS");
            public static readonly int GridSize = Shader.PropertyToID("_FogGridSize");
            public static readonly int DepthParams = Shader.PropertyToID("_FogDepthParams");
            public static readonly int Albedo = Shader.PropertyToID("_FogAlbedo");
            public static readonly int Ambient = Shader.PropertyToID("_FogAmbient");
            public static readonly int MainLightColor = Shader.PropertyToID("_FogMainLightColor");
            public static readonly int MainLightDir = Shader.PropertyToID("_FogMainLightDirWS");
            public static readonly int ScatterParams = Shader.PropertyToID("_FogScatterParams");
            public static readonly int LightParams = Shader.PropertyToID("_FogLightParams");
            public static readonly int LocalParams = Shader.PropertyToID("_FogLocalParams");
            public static readonly int ScatterWrite = Shader.PropertyToID("_FogScatterWrite");
            public static readonly int ScatterHistory = Shader.PropertyToID("_FogScatterHistory");
            public static readonly int ScatterRead = Shader.PropertyToID("_FogScatterRead");
            public static readonly int Integrated = Shader.PropertyToID("_FogIntegrated");
            public static readonly int SourceTex = Shader.PropertyToID("_SourceTex");
            public static readonly int DepthTex = Shader.PropertyToID("_FogDepthTex");
            public static readonly int ApplyParams = Shader.PropertyToID("_FogApplyParams");
            public static readonly int AdditionalShadowmap = Shader.PropertyToID("_AdditionalLightsShadowmapTexture");
        }

        ComputeShader? m_Compute;
        Material? m_ApplyMaterial;
        FogPerCameraStore? m_Store;
        int m_KPopulate = -1;
        int m_KIntegrate = -1;

        public VolumetricFogPass()
        {
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public void Setup(ComputeShader? compute, Material? applyMaterial, FogPerCameraStore store)
        {
            m_ApplyMaterial = applyMaterial;
            m_Store = store;

            if (compute != m_Compute)
            {
                m_Compute = compute;
                m_KPopulate = m_KIntegrate = -1;
            }

            if (m_Compute != null && m_KPopulate < 0)
            {
                m_KPopulate = m_Compute.FindKernel("Populate");
                m_KIntegrate = m_Compute.FindKernel("Integrate");
            }
        }

        sealed class ComputePassData
        {
            public ComputeShader compute = null!;
            public int kernel;
            public bool isPopulate;
            public TextureHandle scatterWrite;
            public TextureHandle scatterHistory;
            public TextureHandle scatterRead;
            public TextureHandle integrated;
            public TextureHandle additionalShadows;
            public bool bindShadows;
            public Vector3Int groups;
            public FogConstants k;
        }

        sealed class ApplyPassData
        {
            public Material material = null!;
            public TextureHandle source;
            public TextureHandle depth;
            public TextureHandle integrated;
            public Vector4 applyParams;
        }

        struct FogConstants
        {
            public Matrix4x4 invViewProj;
            public Matrix4x4 prevViewProj;
            public Vector4 camPos;
            public Vector4 camFwd;
            public Vector4 prevCamPos;
            public Vector4 prevCamFwd;
            public Vector4 gridSize;
            public Vector4 depthParams;
            public Vector4 albedo;
            public Vector4 ambient;
            public Vector4 mainLightColor;
            public Vector4 mainLightDir;
            public Vector4 scatterParams;
            public Vector4 lightParams;
            public Vector4 localParams;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_Compute == null || m_ApplyMaterial == null || m_Store == null || m_KPopulate < 0)
                return;

            ComputeShader compute = m_Compute;
            Material applyMaterial = m_ApplyMaterial;
            FogPerCameraStore store = m_Store;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData.isActiveTargetBackBuffer)
                return;

            TextureHandle source = resourceData.cameraColor;
            TextureHandle depth = resourceData.cameraDepthTexture;
            if (!source.IsValid() || !depth.IsValid())
                return;

            VolumetricFogVolumeComponent fog = VolumeManager.instance.stack.GetComponent<VolumetricFogVolumeComponent>();
            if (fog == null || !fog.IsActive())
                return;

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            Camera camera = cameraData.camera;

            Vector3Int dims = FogGrid.Resolve(fog.quality.value);
            VolumetricFogState state = store.GetOrCreate(camera);
            bool realloc = state.EnsureAllocated(dims.x, dims.y, dims.z);

            Vector3 camPos = camera.transform.position;
            Vector3 camFwd = camera.transform.forward;
            Matrix4x4 view = cameraData.GetViewMatrix();
            // renderIntoTexture:false — the froxel uv convention matches Apply's
            // GetFullScreenTriangleTexCoord; the render-target Y-flip (true) would mirror
            // position-dependent shading (e.g. spot cones) vertically. Identical on GL.
            Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), false);
            Matrix4x4 viewProj = gpuProj * view;
            Matrix4x4 invViewProj = viewProj.inverse;

            bool reset = realloc
                         || !state.HasHistory
                         || VolumetricFog.ConsumeCutThisFrame()
                         || Vector3.Distance(camPos, state.PrevCameraPos) > fog.maxDistance.value * 0.5f
                         || Vector3.Dot(camFwd, state.PrevCameraForward) < 0.5f;

            bool temporal = fog.temporal.value;
            float historyFrames = Mathf.Max(1f, fog.historyFrames.value);
            float blend = (temporal && !reset) ? 1f / historyFrames : 1f;
            // Golden-ratio (R1, plastic-constant) temporal phase added to the per-froxel blue
            // noise in the shader: a low-discrepancy sequence so the history accumulator converges
            // with minimal variance (no shimmer). 0 when temporal is off => static spatial dither.
            float jitterPhase = temporal ? (float)((Time.frameCount * 0.6180339887498949) % 1.0) : 0f;

            // P1' local-light in-scatter reads URP's global _AdditionalLights* arrays
            // from the compute dispatch. Keyword compiles the loop in/out.
            bool localLights = fog.localLights.value;
            float localLightW = localLights ? fog.localLightIntensity.value : 0f;
            CoreUtils.SetKeyword(compute, "_FOG_LOCAL_LIGHTS", localLights);

            // Geometry occlusion (non-negotiable): gate each local light by the
            // additional-light shadow atlas. Only valid when URP actually produced one
            // this frame (additional light shadows enabled + a shadow-caster present);
            // otherwise the global shadow arrays are unset and the keyword stays off.
            TextureHandle additionalShadows = resourceData.additionalShadowsTexture;
            bool localShadows = localLights && additionalShadows.IsValid();
            CoreUtils.SetKeyword(compute, "_FOG_LOCAL_SHADOWS", localShadows);

            ResolveMainLight(lightData, out Vector4 mainLightColor, out Vector4 mainLightDir);

            float near = camera.nearClipPlane;
            float far = fog.maxDistance.value;

            var k = new FogConstants
            {
                invViewProj = invViewProj,
                prevViewProj = state.PrevViewProj,
                camPos = camPos,
                camFwd = camFwd,
                prevCamPos = state.PrevCameraPos,
                prevCamFwd = state.PrevCameraForward,
                gridSize = new Vector4(dims.x, dims.y, dims.z, 0f),
                depthParams = new Vector4(near, far, fog.sliceDistributionExponent.value, 0f),
                albedo = fog.albedo.value,
                ambient = fog.ambientTint.value,
                mainLightColor = mainLightColor,
                mainLightDir = mainLightDir,
                scatterParams = new Vector4(fog.density.value, fog.heightStart.value, fog.heightFalloff.value, fog.anisotropy.value),
                lightParams = new Vector4(fog.lightIntensityScale.value, blend, jitterPhase, localLightW),
                // .y = source radius²: softens 1/d² near each local light (area-light model).
                localParams = new Vector4(fog.localAnisotropy.value, fog.localLightRadius.value * fog.localLightRadius.value, 0f, 0f),
            };

            TextureHandle scatterCurr = renderGraph.ImportTexture(state.Current(Time.frameCount));
            TextureHandle scatterHist = renderGraph.ImportTexture(state.History(Time.frameCount));

            var integratedDesc = new TextureDesc(dims.x, dims.y)
            {
                slices = dims.z,
                dimension = TextureDimension.Tex3D,
                format = GraphicsFormat.R16G16B16A16_SFloat,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                enableRandomWrite = true,
                clearBuffer = false,
                name = "_FogIntegratedRT",
            };
            TextureHandle integrated = renderGraph.CreateTexture(integratedDesc);

            // Populate: per-froxel density + temporal reprojection.
            using (var builder = renderGraph.AddComputePass<ComputePassData>("VolumetricFog.Populate", out ComputePassData data))
            {
                data.compute = compute;
                data.kernel = m_KPopulate;
                data.isPopulate = true;
                data.scatterWrite = scatterCurr;
                data.scatterHistory = scatterHist;
                data.additionalShadows = additionalShadows;
                data.bindShadows = localShadows;
                data.groups = new Vector3Int(CeilDiv(dims.x, 8), CeilDiv(dims.y, 8), dims.z);
                data.k = k;
                builder.UseTexture(scatterCurr, AccessFlags.Write);
                builder.UseTexture(scatterHist, AccessFlags.Read);
                // Lighting (and its shadow-atlas occlusion) is evaluated in Populate, so the
                // atlas read dependency lives here, not on Integrate.
                if (localShadows)
                    builder.UseTexture(additionalShadows, AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (ComputePassData d, ComputeGraphContext ctx) => ExecuteCompute(d, ctx));
            }

            // Integrate: front-to-back transmittance per column.
            using (var builder = renderGraph.AddComputePass<ComputePassData>("VolumetricFog.Integrate", out ComputePassData data))
            {
                data.compute = compute;
                data.kernel = m_KIntegrate;
                data.isPopulate = false;
                data.scatterRead = scatterCurr;
                data.integrated = integrated;
                data.groups = new Vector3Int(CeilDiv(dims.x, 8), CeilDiv(dims.y, 8), 1);
                data.k = k;
                builder.UseTexture(scatterCurr, AccessFlags.Read);
                builder.UseTexture(integrated, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (ComputePassData d, ComputeGraphContext ctx) => ExecuteCompute(d, ctx));
            }

            // Apply: composite the integrated volume onto a new color target (depth-correct).
            TextureDesc destDesc = renderGraph.GetTextureDesc(source);
            destDesc.name = "_FogColor";
            destDesc.clearBuffer = false;
            TextureHandle dest = renderGraph.CreateTexture(destDesc);

            using (var builder = renderGraph.AddRasterRenderPass<ApplyPassData>("VolumetricFog.Apply", out ApplyPassData data))
            {
                data.material = applyMaterial;
                data.source = source;
                data.depth = depth;
                data.integrated = integrated;
                data.applyParams = new Vector4(near, far, fog.sliceDistributionExponent.value, 1f / dims.z);

                builder.UseTexture(source, AccessFlags.Read);
                builder.UseTexture(depth, AccessFlags.Read);
                builder.UseTexture(integrated, AccessFlags.Read);
                builder.SetRenderAttachment(dest, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (ApplyPassData d, RasterGraphContext ctx) =>
                {
                    MaterialPropertyBlock mpb = ctx.renderGraphPool.GetTempMaterialPropertyBlock();
                    mpb.SetTexture(ShaderIDs.SourceTex, (Texture)d.source);
                    mpb.SetTexture(ShaderIDs.DepthTex, (Texture)d.depth);
                    mpb.SetTexture(ShaderIDs.Integrated, (Texture)d.integrated);
                    mpb.SetVector(ShaderIDs.ApplyParams, d.applyParams);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, 0, MeshTopology.Triangles, 3, 1, mpb);
                });
            }

            resourceData.cameraColor = dest;

            // Cache this frame's basis as next frame's reprojection source.
            state.PrevViewProj = viewProj;
            state.PrevCameraPos = camPos;
            state.PrevCameraForward = camFwd;
            state.HasHistory = true;
            state.LastFrame = Time.frameCount;
        }

        static void ExecuteCompute(ComputePassData d, ComputeGraphContext ctx)
        {
            ComputeCommandBuffer cmd = ctx.cmd;
            ComputeShader cs = d.compute;
            int kn = d.kernel;
            FogConstants k = d.k;

            cmd.SetComputeMatrixParam(cs, ShaderIDs.InvViewProj, k.invViewProj);
            cmd.SetComputeMatrixParam(cs, ShaderIDs.PrevViewProj, k.prevViewProj);
            cmd.SetComputeVectorParam(cs, ShaderIDs.CamPos, k.camPos);
            cmd.SetComputeVectorParam(cs, ShaderIDs.CamFwd, k.camFwd);
            cmd.SetComputeVectorParam(cs, ShaderIDs.PrevCamPos, k.prevCamPos);
            cmd.SetComputeVectorParam(cs, ShaderIDs.PrevCamFwd, k.prevCamFwd);
            cmd.SetComputeVectorParam(cs, ShaderIDs.GridSize, k.gridSize);
            cmd.SetComputeVectorParam(cs, ShaderIDs.DepthParams, k.depthParams);
            cmd.SetComputeVectorParam(cs, ShaderIDs.Albedo, k.albedo);
            cmd.SetComputeVectorParam(cs, ShaderIDs.Ambient, k.ambient);
            cmd.SetComputeVectorParam(cs, ShaderIDs.MainLightColor, k.mainLightColor);
            cmd.SetComputeVectorParam(cs, ShaderIDs.MainLightDir, k.mainLightDir);
            cmd.SetComputeVectorParam(cs, ShaderIDs.ScatterParams, k.scatterParams);
            cmd.SetComputeVectorParam(cs, ShaderIDs.LightParams, k.lightParams);
            cmd.SetComputeVectorParam(cs, ShaderIDs.LocalParams, k.localParams);

            if (d.isPopulate)
            {
                cmd.SetComputeTextureParam(cs, kn, ShaderIDs.ScatterWrite, d.scatterWrite);
                cmd.SetComputeTextureParam(cs, kn, ShaderIDs.ScatterHistory, d.scatterHistory);
                // Atlas bound explicitly; the slice transforms / params arrive as URP globals by name.
                // Lighting+occlusion run in Populate, so the atlas binds to this kernel.
                if (d.bindShadows)
                    cmd.SetComputeTextureParam(cs, kn, ShaderIDs.AdditionalShadowmap, d.additionalShadows);
            }
            else
            {
                cmd.SetComputeTextureParam(cs, kn, ShaderIDs.ScatterRead, d.scatterRead);
                cmd.SetComputeTextureParam(cs, kn, ShaderIDs.Integrated, d.integrated);
            }

            cmd.DispatchCompute(cs, kn, d.groups.x, d.groups.y, d.groups.z);
        }

        static void ResolveMainLight(UniversalLightData lightData, out Vector4 color, out Vector4 dir)
        {
            color = Vector4.zero;
            dir = new Vector4(0f, 1f, 0f, 0f);

            int idx = lightData.mainLightIndex;
            if (idx < 0 || idx >= lightData.visibleLights.Length)
                return;

            VisibleLight vl = lightData.visibleLights[idx];
            Vector3 forward = ((Vector3)(Vector4)vl.localToWorldMatrix.GetColumn(2)).normalized;
            dir = -forward; // direction TOWARD the light
            Color c = vl.finalColor;
            color = new Vector4(c.r, c.g, c.b, 0f);
        }

        static int CeilDiv(int a, int b) => (a + b - 1) / b;
    }
}
