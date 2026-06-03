# Volumetric Fog

![preview](preview.jpg)

A drop-in URP render feature that fills the camera frustum with a **froxel-based participating medium** —
a near-field fog for **enclosed, sunless spaces** (caves, interiors, dust). It is built so that **local
scene lights carve shafts through the fog**, and — the headline guarantee — **geometry blocks scatter
propagation**: a lamp behind a wall does *not* illuminate the fog in front of the wall (no glow through
walls).

It works by populating a 3D froxel grid (a frustum-aligned voxel volume) with density and per-froxel
in-scatter, integrating each column front-to-back into `(in-scatter, transmittance)`, then compositing
that against scene depth. In-scatter comes from scene **point/spot lights**, each gated by its shadow
map, plus an ambient floor and an optional main-light term. Away from light the medium reads as
thickening dark haze that hides distance — gameplay occlusion ("can't see past here") rather than glow.

It injects **before post-processing**, so the in-scatter lives in the HDR color and bloom/tonemapping
see it. Per-camera temporal reprojection stabilises and amortises the volume.

---

## Requirements

- **URP** (Universal Render Pipeline), Render Graph, Unity 6000.4+.
- **Lights must cast shadows** to be occluded. A point/spot light only lights the fog it can actually
  reach via its shadow map; a light with shadows disabled will glow through walls. Enable **Additional
  Light Shadows** in the URP asset.
- Compute-shader support (the froxel populate/integrate runs on a compute shader).
- A depth buffer (URP provides this; fog composites against scene depth).

---

## Quick start

1. **Add the feature.** On your *Universal Renderer* asset → *Add Renderer Feature* → **Volumetric Fog**.
   It auto-binds its compute + apply shaders.
2. **Enable Additional Light Shadows** in the URP asset (and on the lights you want to light the fog) —
   this is what makes geometry block the scatter.
3. **Add a Volume override.** On any Volume (a Global volume is the usual choice), *Add Override →
   Post-processing → Volumetric Fog*, tick **Enabled**, and remember to also tick the override checkbox
   to the left of each parameter you want to change — URP ignores un-ticked parameters.
4. **Set the base look.** `Density` is the master thickness; `Albedo` tints the medium; `Ambient Tint`
   keeps unlit fog from going pure black. Keep `Max Distance` modest — this is a near-field volume.
5. **Tune the local-light look.** Leave `Local Lights` on. Use `Local Light Intensity` for shaft
   brightness and `Local Anisotropy` for the look (low/0 = a stable dust cone visible from any angle;
   high = forward god-ray streaks that track the view). If the fog right at a lamp aliases into a hot
   spike, raise `Local Light Radius` to soften the core.

In a **sunless interior**, keep `Light Intensity Scale` (the main directional/sun term) low or zero —
the local lights carry the look. The fog is safe to leave on globally; with no lights reaching a region
it just reads as dark haze.

---

## How it works

The pass runs each frame on a per-camera froxel grid (resolution set by `Quality`):

1. **Populate** evaluates density and in-scatter at each froxel. Lighting is evaluated *here*, at a
   blue-noise z-jittered slice, and reprojected against the previous frame's history — so the temporal
   accumulator dissolves slice/shadow banding into fine noise instead of coherent shimmering slabs.
2. **Integrate** marches each froxel column front-to-back, accumulating in-scatter and transmittance
   with an energy-correct slice integral.
3. **Apply** composites the integrated volume against scene depth (with dither) into the HDR camera
   color, before post.

The local-light loop sums, per froxel, `phase × attenuation × visibility(froxel, light) × lightColor`
over the nearby point/spot lights. `visibility` is the geometry gate — sampled from each light's shadow
map, so occluded froxels stay dark. This is the non-negotiable that earns the volume; without it the
effect would be a screen-space tint.

---

## Good to know

- **No shadows = glow through walls.** This is the most common setup mistake. If a light leaks through
  geometry, confirm Additional Light Shadows are enabled in the URP asset *and* on that light.
- **Near-field by design.** Keep `Max Distance` modest. `Slice Distribution Exponent` packs more froxel
  slices near the camera (>1) for detail where it matters.
- **Temporal reprojection is on by default** and smooths the volume; disable `Temporal` to debug raw
  per-frame output. `History Frames` trades smoothness for lag.
- **Quality tiers** are froxel resolutions: Low 128×72×48, Medium 160×90×64, High 240×135×96. Cost
  scales with froxel count, not light count — caves have few near lights, so the grid is the budget
  driver. Cull/cap is handled internally.
- **Voxel field (P3) is an inert door.** `Voxel Field Weight` and `Dust Intensity` do nothing until an
  external density source is registered (e.g. dig-driven dust). Leave at 0.
- **Sky/skybox is unaffected** — the volume composites against scene depth and fades at the far bound.
- Reflection and preview cameras are skipped automatically.

---

## Files

| File | Role |
| --- | --- |
| `Runtime/VolumetricFogRenderFeature.cs` | The Scriptable Renderer Feature; owns the per-camera store, the pass, and the apply material. |
| `Runtime/Passes/VolumetricFogPass.cs` | Populate → Integrate (compute) → Apply (raster) in the render graph; binds light + shadow-atlas globals. |
| `Runtime/VolumetricFogVolumeComponent.cs` | The Volume-framework config surface (the only designer-facing settings). |
| `Runtime/VolumetricFogState.cs` | Per-camera froxel textures: ping-pong + temporal history. |
| `Runtime/FogPerCameraStore.cs` | `Camera → state` map; lifecycle, prune, dispose. |
| `Runtime/FogGrid.cs` | Froxel grid resolution / layout helpers. |
| `Shaders/FogFroxel.compute` | Two kernels: `Populate` (density + lighting + reprojection) and `Integrate` (front-to-back march). |
| `Shaders/FogApply.shader` | Depth-correct fullscreen composite into the HDR color, with dither. |
| `Editor/VolumetricFogRenderFeatureEditor.cs` | Auto-binds the compute/apply shader references. |
