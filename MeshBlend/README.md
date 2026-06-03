# Mesh Blend

A drop-in URP render feature that **softens the hard intersection seam where two separately-rendered
opaque meshes meet** — a rock pushed into terrain, a boulder against a cliff, props melting into the
ground. It is a screen-space technique inspired by UE5's *MeshBlend*: it samples the already-lit
scene across the seam and blends each surface into its neighbour, so the crease reads as an organic
material transition instead of a hard line.

It works in both **Forward+** and **Deferred** (it operates on the lit camera colour, which exists in
both), needs no per-material setup, and gets each surface's lighting "for free" because it mirrors
already-shaded pixels.

---

## Requirements

- **URP** (Universal Render Pipeline), Render Graph.
- **A temporal accumulator is strongly recommended** — TAA, TSR, DLSS/DLAA, or FSR. The blend uses
  surface-locked noise that reads best when a temporal filter resolves it; without one the break-up
  is grainier. It does **not** require per-frame jitter, so it stays stable on the surface either way.
- Opaque meshes only (the seam blends the opaque pass; transparents are excluded).

---

## Quick start

1. **Add the feature.** On your *Universal Renderer* asset → *Add Renderer Feature* → **Mesh Blend**.
   It auto-binds its shaders.
2. **Enable temporal AA** on the camera (recommended — see above).
3. **Add a Volume override.** On any Volume (a Global volume is the usual choice), *Add Override →
   Post-processing → Mesh Blend*, and tick **Enabled** (remember to also tick the override checkbox
   to the left of each parameter you want to change — URP ignores un-ticked parameters).
4. **Give participating meshes IDs.** Two ways:
   - **Automatic (recommended):** tag the meshes with `MeshBlendObject`, drop a `MeshBlendActivator`
     in the scene, and run it (it has an inspector button). It scans, builds an adjacency graph, and
     assigns **collision-free IDs** (graph colouring) so touching objects never share an ID.
   - **Manual:** set each renderer's packed ID via a `MaterialPropertyBlock` (`_BlendId`).
5. **Tune** the per-class `Blend Widths` for the seam width, then the fidelity/noise knobs below.

*Internal material seams:* a multi-material prop can also blend across its **own** submesh boundaries —
tick `Blend Internal` on its `MeshBlendObject` (off by default, so intentional hard material lines stay
crisp). This needs the per-submesh ID prepass (currently the feature's experimental manual-prepass path).

Only tagged/ID'd meshes blend. Everything else (ID 0) is left untouched, so it's safe to leave the
feature on globally.

---

## Files

| File | Role |
| --- | --- |
| `MeshBlendRenderFeature.cs` | The Scriptable Renderer Feature; owns the materials + the pass. |
| `Passes/MeshBlendPass.cs` | ID prepass + fullscreen composite, plus the optional JFA seed/flood passes (render-graph). |
| `MeshBlendVolumeComponent.cs` | The Volume-framework config surface. |
| `Shaders/MeshBlend.shader` | The composite kernel — Pass 0 = search front-end, Pass 1 = JFA front-end. |
| `Shaders/MeshBlendCommon.hlsl` | Shared core: helpers + `ComposeBlend()` (the seam-*using* tail both composites call). |
| `Shaders/MeshBlendJFA.shader` | Jump-Flood seed + flood passes (build the nearest-seam field). |
| `Shaders/MeshBlendId.shader` | ID-prepass override material. |
| `Shaders/MeshBlendDebug.shader` | False-colour ID debug view. |
| `MeshBlendObject.cs` | Tag component for participating renderers. |
| `MeshBlendActivator.cs` | Scene scan → adjacency → collision-free ID assignment. |
| `MeshBlendColoring.cs` | ID packing + greedy graph colouring. |
