// ADR: One ScriptableRenderPass owns all mesh-blend render-graph work at a single injection point
// (AfterRenderingSkybox): an ID prepass (RendererList + override material -> R8 ID buffer) then a
// fullscreen composite that mirrors the lit cameraColor across seams. The ID TextureHandle stays a
// local handed between the using-blocks. Composite output replaces cameraColor.
// ADR: ID occlusion is hardware-tested — the prepass binds the live opaque depth attachment
// (activeDepthTexture) read-only with ZTest LEqual / ZWrite Off, so an object writes its ID only
// where frontmost. MSAA off in this project, so the R8 ID target needs no sample-count matching.
// ADR: Lit-colour mirror works in Forward+ AND Deferred (cameraColor exists in both). The shader
// reconstructs world position from UNITY_MATRIX_I_VP (no hand-rolled inverse-VP) and the break-up
// noise/jitter are world-locked + static (no per-frame phase). Perspective camera assumed.
// ADR: Volume toggle picks the seam-FINDING front-end: default per-pixel SEARCH (composite pass 0), or
// JFA — a seed + ~8 ping-pong flood passes build a screen-wide nearest-seam field, then composite pass 1
// reads it O(1). Same downstream blend (ComposeBlend). JFA cost is coverage-independent; kept as an
// educational A/B, not the default. Flood step caps below maxRadius so pass count is resolution-bounded.
#nullable enable
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Readymade.PostProcessing
{
    internal sealed class MeshBlendPass : ScriptableRenderPass
    {
        private static class ShaderIDs
        {
            public static readonly int SourceTex = Shader.PropertyToID("_SourceTex");
            public static readonly int MeshBlendId = Shader.PropertyToID("_MeshBlendId");
            public static readonly int DepthTex = Shader.PropertyToID("_MeshBlendDepth");
            public static readonly int Normals = Shader.PropertyToID("_MeshBlendNormals");
            public static readonly int Params = Shader.PropertyToID("_MeshBlendParams");
            public static readonly int SizeWidths = Shader.PropertyToID("_MeshBlendSizeWidths");
            public static readonly int Texel = Shader.PropertyToID("_MeshBlendTexel");
            public static readonly int Proj = Shader.PropertyToID("_MeshBlendProj");
            public static readonly int Fidelity = Shader.PropertyToID("_MeshBlendFidelity");
            public static readonly int Shape = Shader.PropertyToID("_MeshBlendShape");
            public static readonly int Seam = Shader.PropertyToID("_MeshBlendSeam");
            public static readonly int Jfa = Shader.PropertyToID("_MeshBlendJfa");
            public static readonly int BlendId = Shader.PropertyToID("_BlendId"); // SPIKE: manual prepass global
        }

        private static readonly List<ShaderTagId> s_ShaderTags = new List<ShaderTagId>
        {
            new ShaderTagId("SRPDefaultUnlit"),
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly"),
            new ShaderTagId("UniversalGBuffer"),
        };

        private Material? m_BlendMaterial;
        private Material? m_IdMaterial;
        private Material? m_DebugMaterial;
        private Material? m_JfaMaterial;
        private int m_LayerMask = -1;

        // SPIKE: when true, the ID prepass uses manual per-submesh DrawRenderer + a per-draw global id
        // (no renderer MPB) instead of the RendererList + MPB path. Validates the SRP-batch-friendly,
        // per-submesh architecture. Set by the render feature.
        public bool SpikeManualIdPrepass;

        // MeshBlend.shader pass indices (search composite vs JFA-field composite).
        private const int k_PassSearch = 0;
        private const int k_PassJfaComposite = 1;
        // MeshBlendJFA.shader pass indices.
        private const int k_JfaPassSeed = 0;
        private const int k_JfaPassFlood = 1;

        public MeshBlendPass()
        {
            // Depth: depth-stable radius + depth gate (+ the ID prepass uses the depth ATTACHMENT for
            // occlusion). Normal: the slope gate reads _CameraNormalsTexture. Forcing Normal triggers
            // URP's DepthNormals prepass — accepted; partly amortized when SSAO already requests it.
            ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
        }

        public void Setup(Material? blendMaterial, Material? idMaterial, Material? debugMaterial, Material? jfaMaterial, int layerMask)
        {
            m_BlendMaterial = blendMaterial;
            m_IdMaterial = idMaterial;
            m_DebugMaterial = debugMaterial;
            m_JfaMaterial = jfaMaterial;
            m_LayerMask = layerMask;
        }

        private sealed class IdPassData
        {
            public RendererListHandle rendererList;
        }

        // SPIKE: manual per-submesh draws read from this snapshot (no MPB, no RendererList).
        private struct ManualDraw
        {
            public Renderer renderer;
            public int submesh;
            public float packedId;
        }

        private sealed class ManualIdPassData
        {
            public Material material = null!;
            public readonly List<ManualDraw> draws = new List<ManualDraw>();
        }

        private sealed class CompositePassData
        {
            public Material material = null!;
            public int passIndex;
            public TextureHandle source;
            public TextureHandle id;
            public TextureHandle depth;
            public TextureHandle normals;
            public TextureHandle seam;     // JFA field (TextureHandle.nullHandle for the search path)
            public bool bindSeam;
            public Vector4 blendParams;
            public Vector4 sizeWidths;
            public Vector4 texel;
            public Vector4 proj;
            public Vector4 fidelity;
            public Vector4 shape;
        }

        private sealed class JfaSeedPassData
        {
            public Material material = null!;
            public TextureHandle id;
            public Vector4 texel;
        }

        private sealed class JfaFloodPassData
        {
            public Material material = null!;
            public TextureHandle src;
            public Vector4 texel;
            public Vector4 jfa; // x = step (px)
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData.isActiveTargetBackBuffer)
                return;

            TextureHandle source = resourceData.cameraColor;
            TextureHandle depth = resourceData.cameraDepthTexture;
            if (!source.IsValid() || !depth.IsValid() || !resourceData.activeDepthTexture.IsValid())
                return;
            if (m_IdMaterial == null)
                return;

            MeshBlendVolumeComponent settings = VolumeManager.instance.stack.GetComponent<MeshBlendVolumeComponent>();
            if (settings == null || !settings.IsActive())
                return;

            MeshBlendDebugMode debugMode = settings.debugMode.value;
            // Ids debug uses the dedicated false-colour material; Normal + BlendArea both use the blend
            // material (BlendArea just flips a flag in ComposeBlend → white overlay).
            bool idsDebug = debugMode == MeshBlendDebugMode.Ids && m_DebugMaterial != null;
            Material? composite = idsDebug ? m_DebugMaterial : m_BlendMaterial;
            if (composite == null)
                return;

            TextureHandle idTarget = CreateIdTarget(renderGraph, source);
            if (SpikeManualIdPrepass)
                RecordIdPrepassManual(renderGraph, resourceData, idTarget);
            else
                RecordIdPrepass(renderGraph, frameData, resourceData, idTarget);

            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            TextureDesc sourceDesc = renderGraph.GetTextureDesc(source);
            float w = sourceDesc.width;
            float h = sourceDesc.height;
            Matrix4x4 proj = cameraData.GetProjectionMatrix();
            float pxPerWorldAtUnitDepth = 0.5f * h * proj.m11;

            TextureHandle normals = resourceData.cameraNormalsTexture;
            bool hasNormals = normals.IsValid();
            TextureHandle normalsBind = hasNormals ? normals : idTarget;

            float slope = settings.slopeFactor.value;
            Vector4 blendParams = new Vector4(
                settings.glancingFilter.value, // x = band-asymmetry glancing filter (0 = off)
                settings.minScreenSize.value,
                settings.depthFalloff.value,
                k_MaxRadiusPx);
            Vector4 sizeWidths = settings.blendWidths.value; // x=Small y=Medium z=Large w=Huge (meters)
            Vector4 texel = new Vector4(1f / w, 1f / h, w, h);
            Vector4 projParams = new Vector4(pxPerWorldAtUnitDepth, settings.blendStrength.value, QualityGridK(settings.quality.value), hasNormals ? 1f : 0f);
            Vector4 fidelity = new Vector4(slope, settings.noiseScale.value, settings.noiseStrength.value, hasNormals && slope > 0f ? 1f : 0f);
            // x=falloff (edge power) y=bias (flat-core fraction) z=noiseFade (mask ramp-in) w=debugBlendArea
            Vector4 shapeParams = new Vector4(settings.blendFalloff.value, settings.blendBias.value, settings.noiseFade.value,
                debugMode == MeshBlendDebugMode.BlendArea ? 1f : 0f);

            TextureHandle dest = CreateColorTarget(renderGraph, source);

            // JFA path: precompute a screen-wide nearest-seam field (seed → log2(radius) flood passes),
            // then the composite reads it in O(1). Educational A/B against the per-pixel search; gated on
            // the Volume toggle. Falls back to search if the JFA material is missing or in IDs debug.
            bool useJfa = settings.useJumpFlood.value && m_JfaMaterial != null && !idsDebug;
            if (useJfa)
            {
                TextureHandle seam = BuildSeamField(renderGraph, m_JfaMaterial!, idTarget, texel, k_MaxRadiusPx);
                RecordComposite(renderGraph, composite, k_PassJfaComposite, source, idTarget, depth, normalsBind, hasNormals, seam, true, blendParams, sizeWidths, texel, projParams, fidelity, shapeParams, dest);
            }
            else
            {
                RecordComposite(renderGraph, composite, k_PassSearch, source, idTarget, depth, normalsBind, hasNormals, TextureHandle.nullHandle, false, blendParams, sizeWidths, texel, projParams, fidelity, shapeParams, dest);
            }
            resourceData.cameraColor = dest;
        }

        // Seed + ping-pong jump-flood. Step starts at the largest power of two below the max blend radius
        // (NOT screen/2) so the pass count is bounded (~8) regardless of resolution, then halves to 1 with
        // a final step-1 cleanup pass (JFA+1) to mop up the rare flood errors. Returns the final field.
        private TextureHandle BuildSeamField(RenderGraph renderGraph, Material jfaMaterial, TextureHandle idTarget, Vector4 texel, float maxRadiusPx)
        {
            TextureHandle a = CreateSeamField(renderGraph, idTarget, "_MeshBlendSeamA");
            TextureHandle b = CreateSeamField(renderGraph, idTarget, "_MeshBlendSeamB");

            RecordJfaSeed(renderGraph, jfaMaterial, idTarget, texel, a);

            int firstStep = 1;
            while (firstStep * 2 < maxRadiusPx)
                firstStep *= 2;

            TextureHandle src = a, dst = b;
            for (int step = firstStep; step >= 1; step >>= 1)
            {
                RecordJfaFlood(renderGraph, jfaMaterial, src, texel, step, dst);
                (src, dst) = (dst, src);
            }
            // JFA+1: one extra step-1 pass over the converged field.
            RecordJfaFlood(renderGraph, jfaMaterial, src, texel, 1, dst);
            return dst;
        }

        // Fixed perf ceiling on the on-screen search radius — quality-independent so the per-class
        // widths (not quality) set the blend WIDTH; the ceiling only bounds worst-case cost for close/large objects.
        const float k_MaxRadiusPx = 192f;

        // Quality drives the fine-grid half-extent (tap density). The shader's binary-search refines the
        // seam DISTANCE to sub-pixel, so the grid only needs to resolve the seam DIRECTION + a bracket —
        // it no longer carries distance precision. That lets each tier run a coarser (cheaper) grid:
        // ~169/289 taps (Med/High) vs the old ~289/529, a ~1.6-1.8x cut on band pixels. Keep <= MESHBLEND_MAX_K.
        private static float QualityGridK(MeshBlendQuality quality) => quality switch
        {
            MeshBlendQuality.Low => 4f,
            MeshBlendQuality.High => 8f,
            _ => 6f,
        };

        private static TextureHandle CreateColorTarget(RenderGraph renderGraph, TextureHandle source)
        {
            TextureDesc desc = renderGraph.GetTextureDesc(source);
            desc.name = "_MeshBlendColor";
            desc.clearBuffer = false;
            return renderGraph.CreateTexture(desc);
        }

        private static TextureHandle CreateIdTarget(RenderGraph renderGraph, TextureHandle source)
        {
            TextureDesc desc = renderGraph.GetTextureDesc(source);
            desc.name = "_MeshBlendId";
            desc.format = GraphicsFormat.R8_UNorm;
            desc.depthBufferBits = DepthBits.None;
            desc.clearBuffer = true;
            desc.clearColor = Color.clear; // packed 0 = no blendable
            return renderGraph.CreateTexture(desc);
        }

        private void RecordIdPrepass(RenderGraph renderGraph, ContextContainer frameData, UniversalResourceData resourceData, TextureHandle idTarget)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();

            using var builder = renderGraph.AddRasterRenderPass<IdPassData>("MeshBlend.Id", out IdPassData data);

            DrawingSettings ds = RenderingUtils.CreateDrawingSettings(s_ShaderTags, renderingData, cameraData, lightData, cameraData.defaultOpaqueSortFlags);
            ds.overrideMaterial = m_IdMaterial;
            ds.overrideMaterialPassIndex = 0;

            FilteringSettings fs = new FilteringSettings(RenderQueueRange.opaque, m_LayerMask);
            RenderStateBlock rsb = new RenderStateBlock(RenderStateMask.Depth)
            {
                depthState = new DepthState(false, CompareFunction.LessEqual),
            };

            RendererListHandle rl = CreateRendererList(renderGraph, ref renderingData.cullResults, ds, fs, rsb);
            data.rendererList = rl;

            builder.SetRenderAttachment(idTarget, 0, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Read);
            builder.UseRendererList(rl);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc(static (IdPassData d, RasterGraphContext ctx) =>
            {
                ctx.cmd.DrawRendererList(d.rendererList);
            });
        }

        // SPIKE: manual per-submesh ID prepass. Conveys the id via a per-draw GLOBAL (read by the id
        // override material) so NO MaterialPropertyBlock is needed on the renderer — the prop stays
        // SRP-batchable in its main pass. DrawRenderer(submesh) gives per-submesh ids natively. Occlusion
        // comes from the id shader's own ZTest LEqual against the bound depth attachment. Cost: one R8
        // draw per visible submesh (frustum-culled via Renderer.isVisible) instead of one batched list.
        private void RecordIdPrepassManual(RenderGraph renderGraph, UniversalResourceData resourceData, TextureHandle idTarget)
        {
            using var builder = renderGraph.AddRasterRenderPass<ManualIdPassData>("MeshBlend.Id.Manual", out ManualIdPassData data);
            data.material = m_IdMaterial!;
            data.draws.Clear(); // RenderGraph POOLS + reuses pass data without clearing — must reset or the list grows every frame.

            IReadOnlyList<MeshBlendObject> active = MeshBlendObject.Active;
            for (int i = 0; i < active.Count; i++)
            {
                MeshBlendObject obj = active[i];
                if (obj == null)
                    continue;
                Renderer? r = obj.Renderer;
                if (r == null)
                    continue;
                // SPIKE: no isVisible cull — Renderer.isVisible is unreliable in edit mode and was a
                // second suspect for the all-black result. Draw all registered; re-add frustum culling
                // when productizing. GPU clips off-screen draws anyway.
                int subs = obj.SubmeshCount;
                for (int s = 0; s < subs; s++)
                    data.draws.Add(new ManualDraw { renderer = r, submesh = s, packedId = obj.PackedForSubmesh(s) });
            }

            builder.SetRenderAttachment(idTarget, 0, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Read);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc(static (ManualIdPassData d, RasterGraphContext ctx) =>
            {
                for (int i = 0; i < d.draws.Count; i++)
                {
                    ManualDraw md = d.draws[i];
                    ctx.cmd.SetGlobalFloat(ShaderIDs.BlendId, md.packedId);
                    ctx.cmd.DrawRenderer(md.renderer, d.material, md.submesh, 0);
                }
            });
        }

        private static void RecordComposite(RenderGraph renderGraph, Material material, int passIndex, TextureHandle source, TextureHandle id, TextureHandle depth, TextureHandle normals, bool addNormalsDep, TextureHandle seam, bool bindSeam, Vector4 blendParams, Vector4 sizeWidths, Vector4 texel, Vector4 proj, Vector4 fidelity, Vector4 shape, TextureHandle dest)
        {
            using var builder = renderGraph.AddRasterRenderPass<CompositePassData>("MeshBlend.Composite", out CompositePassData data);

            data.material = material;
            data.passIndex = passIndex;
            data.source = source;
            data.id = id;
            data.depth = depth;
            data.normals = normals;
            data.seam = seam;
            data.bindSeam = bindSeam;
            data.blendParams = blendParams;
            data.sizeWidths = sizeWidths;
            data.texel = texel;
            data.proj = proj;
            data.fidelity = fidelity;
            data.shape = shape;

            builder.UseTexture(source, AccessFlags.Read);
            builder.UseTexture(id, AccessFlags.Read);
            builder.UseTexture(depth, AccessFlags.Read);
            if (addNormalsDep)
                builder.UseTexture(normals, AccessFlags.Read);
            if (bindSeam)
                builder.UseTexture(seam, AccessFlags.Read);
            builder.SetRenderAttachment(dest, 0, AccessFlags.Write);
            builder.SetRenderFunc(static (CompositePassData d, RasterGraphContext ctx) =>
            {
                MaterialPropertyBlock mpb = ctx.renderGraphPool.GetTempMaterialPropertyBlock();
                mpb.SetTexture(ShaderIDs.SourceTex, (Texture)d.source);
                mpb.SetTexture(ShaderIDs.MeshBlendId, (Texture)d.id);
                mpb.SetTexture(ShaderIDs.DepthTex, (Texture)d.depth);
                mpb.SetTexture(ShaderIDs.Normals, (Texture)d.normals);
                if (d.bindSeam)
                    mpb.SetTexture(ShaderIDs.Seam, (Texture)d.seam);
                mpb.SetVector(ShaderIDs.Params, d.blendParams);
                mpb.SetVector(ShaderIDs.SizeWidths, d.sizeWidths);
                mpb.SetVector(ShaderIDs.Texel, d.texel);
                mpb.SetVector(ShaderIDs.Proj, d.proj);
                mpb.SetVector(ShaderIDs.Fidelity, d.fidelity);
                mpb.SetVector(ShaderIDs.Shape, d.shape);
                ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, d.passIndex, MeshTopology.Triangles, 3, 1, mpb);
            });
        }

        // RG float field: .xy = nearest-seam point in pixels, .x < 0 = none. Float (not half) keeps the
        // coords exact above 2K, and the field is never cleared — the seed pass writes every pixel.
        private static TextureHandle CreateSeamField(RenderGraph renderGraph, TextureHandle source, string name)
        {
            TextureDesc desc = renderGraph.GetTextureDesc(source);
            desc.name = name;
            desc.format = GraphicsFormat.R32G32_SFloat;
            desc.depthBufferBits = DepthBits.None;
            desc.clearBuffer = false;
            return renderGraph.CreateTexture(desc);
        }

        private static void RecordJfaSeed(RenderGraph renderGraph, Material material, TextureHandle id, Vector4 texel, TextureHandle dest)
        {
            using var builder = renderGraph.AddRasterRenderPass<JfaSeedPassData>("MeshBlend.JFA.Seed", out JfaSeedPassData data);
            data.material = material;
            data.id = id;
            data.texel = texel;

            builder.UseTexture(id, AccessFlags.Read);
            builder.SetRenderAttachment(dest, 0, AccessFlags.Write);
            builder.SetRenderFunc(static (JfaSeedPassData d, RasterGraphContext ctx) =>
            {
                MaterialPropertyBlock mpb = ctx.renderGraphPool.GetTempMaterialPropertyBlock();
                mpb.SetTexture(ShaderIDs.MeshBlendId, (Texture)d.id);
                mpb.SetVector(ShaderIDs.Texel, d.texel);
                ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, k_JfaPassSeed, MeshTopology.Triangles, 3, 1, mpb);
            });
        }

        private static void RecordJfaFlood(RenderGraph renderGraph, Material material, TextureHandle src, Vector4 texel, int stepPx, TextureHandle dest)
        {
            using var builder = renderGraph.AddRasterRenderPass<JfaFloodPassData>("MeshBlend.JFA.Flood", out JfaFloodPassData data);
            data.material = material;
            data.src = src;
            data.texel = texel;
            data.jfa = new Vector4(stepPx, 0f, 0f, 0f);

            builder.UseTexture(src, AccessFlags.Read);
            builder.SetRenderAttachment(dest, 0, AccessFlags.Write);
            builder.SetRenderFunc(static (JfaFloodPassData d, RasterGraphContext ctx) =>
            {
                MaterialPropertyBlock mpb = ctx.renderGraphPool.GetTempMaterialPropertyBlock();
                mpb.SetTexture(ShaderIDs.Seam, (Texture)d.src);
                mpb.SetVector(ShaderIDs.Texel, d.texel);
                mpb.SetVector(ShaderIDs.Jfa, d.jfa);
                ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, k_JfaPassFlood, MeshTopology.Triangles, 3, 1, mpb);
            });
        }

        // Public-API replica of the internal RenderingUtils.CreateRendererListWithRenderStateBlock:
        // a single RenderStateBlock applied across all draws (overrides the override-material depth state).
        private static RendererListHandle CreateRendererList(RenderGraph renderGraph, ref CullingResults cullResults, DrawingSettings ds, FilteringSettings fs, RenderStateBlock rsb)
        {
            NativeArray<ShaderTagId> tagValues = new NativeArray<ShaderTagId>(1, Allocator.Temp);
            tagValues[0] = ShaderTagId.none;
            NativeArray<RenderStateBlock> stateBlocks = new NativeArray<RenderStateBlock>(1, Allocator.Temp);
            stateBlocks[0] = rsb;
            RendererListParams param = new RendererListParams(cullResults, ds, fs)
            {
                tagValues = tagValues,
                stateBlocks = stateBlocks,
                isPassTagName = false,
            };
            return renderGraph.CreateRendererList(param);
        }
    }
}
