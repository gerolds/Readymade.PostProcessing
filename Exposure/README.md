# Auto Exposure

A drop-in URP render feature that **adapts scene brightness to what the camera is looking at** — like an
eye or a camera's auto-exposure. It meters scene luminance with a **GPU histogram**, picks a target
exposure from a percentile window of that histogram, and eases toward it over time (fast when the scene
brightens, slower when it darkens — tunable). The result keeps a cavern readable when you walk in from
daylight, and stops a sunlit exterior from blowing out.

It is **GPU-only with zero CPU readback**, keeps **independent per-camera state** (game view and scene
view adapt separately), and injects **after transparents but before post** — so it multiplies the
HDR-linear color *ahead* of bloom and tonemapping, where exposure belongs.

A **Fixed** mode is also provided: bypass the meter and set an explicit EV100 (handy as a baseline or for
cutscenes), through the same single GPU multiply code path.

---

## Requirements

- **URP** (Universal Render Pipeline), Render Graph, Unity 6000.4+.
- Compute-shader support (the luminance histogram is built on a compute shader).
- An HDR camera/pipeline (exposure operates on linear HDR color before tonemapping).

---

## Quick start

1. **Add the feature.** On your *Universal Renderer* asset → *Add Renderer Feature* → **Auto Exposure**.
   It auto-binds its compute, apply, and debug shaders.
2. **Add a Volume override.** On any Volume (a Global volume is the usual choice), *Add Override →
   Post-processing → Auto Exposure*, and tick the override checkbox to the left of each parameter you
   want to change — URP ignores un-ticked parameters.
3. **Pick a mode.** Leave `Mode` on **Automatic** to meter the scene each frame, or set **Fixed** and
   dial `Fixed Exposure` (EV100) for a constant exposure.
4. **Set the range.** `Min Exposure` / `Max Exposure` (EV100) clamp how far adaptation can go — this is
   your safety net against pitch-black or fully-blown frames. `Exposure Compensation` (EV) nudges the
   metered target up or down to taste.
5. **Tune adaptation feel.** `Adaptation Speed Up` / `Adaptation Speed Down` (per second) control how
   quickly the eye reacts to brightening vs. darkening — typically up is faster than down.
6. **Choose what to meter.** `Metering` is Center-Weighted (soft favouring of the center), Spot (a tight
   region), or Average (the whole frame). `Meter Center` / `Meter Size` place and size the region;
   `Center Strength` controls how hard the center is favoured (Center-Weighted only).
7. **Verify with the overlay.** Tick `Show Debug Overlay` to draw the live histogram and current
   exposure on screen while you tune, then turn it off.

---

## How it works

Each frame, in **Automatic** mode, the pass runs three compute steps then a fullscreen multiply:

1. **Clear** the 256-bin histogram buffer.
2. **Build** — sample the metering-resolution color, convert each pixel to a luminance EV, map it into a
   bin, and accumulate it weighted by the metering shape (center/spot/average). Pixels with no luminance
   are skipped.
3. **Reduce** — walk the histogram, reject the darkest `Histogram Low Percent` and brightest
   `Histogram High Percent` tails, take the weighted mean EV of what's left, and derive a target EV
   (anchored to middle grey, offset by compensation, clamped to the min/max range). Then ease the
   stored exposure toward that target using the up/down speed for the direction of change.
4. **Apply** — a fullscreen pass multiplies the camera color by `exp2(EV)` and swaps it back. **Fixed**
   mode skips steps 1–3 and applies `Fixed Exposure` directly through the same multiply.

Exposure (the eased EV and its multiplier) is the only thing held per camera — in a small
`GraphicsBuffer` owned by the per-camera store, not on the Volume component.

---

## Good to know

- **Adapts before post.** Exposure multiplies HDR-linear color at `AfterRenderingTransparents`, so bloom
  and tonemapping react to the exposed image — the correct order. Don't expect it to compensate *for*
  tonemapping artefacts; it sits ahead of them.
- **Per-camera, independent.** Game view and scene view adapt separately and don't fight. New cameras
  start fresh; dead cameras are pruned.
- **The percentile window is your main tuning lever.** Widen the low/high reject to ignore dark corners
  and bright highlights; narrow it to meter more literally. Use the debug overlay to see where the mass
  of the histogram sits.
- **Min/Max EV is a hard clamp**, not a suggestion — set it to the darkest/brightest the scene should
  ever resolve to.
- **Histogram is 256 bins**, mapped between `Histogram Min EV` and `Histogram Max EV`; widen that EV span
  to cover scenes with extreme dynamic range.
- **Fixed mode is a valid shipping choice**, not just a debug toggle — same GPU path, just a constant EV.
- Reflection and preview cameras are skipped automatically.

---

## Files

| File | Role |
| --- | --- |
| `Runtime/AutoExposureRendererFeature.cs` | The Scriptable Renderer Feature; owns the per-camera store, the metering pass, and the debug pass. |
| `Runtime/AutoExposurePass.cs` | Clear → Build → Reduce (compute) → Apply (raster) in the render graph; resolves the metering weight. |
| `Runtime/AutoExposureDebugPass.cs` | Optional on-screen histogram + EV overlay (after post). |
| `Runtime/AutoExposureVolumeComponent.cs` | The Volume-framework config surface (the only designer-facing settings). |
| `Runtime/AutoExposureState.cs` | Per-camera `GraphicsBuffer` (eased EV + multiplier); `Reset()` for camera cuts. |
| `Runtime/PerCameraStateStore.cs` | `Camera → state` map; lifecycle, prune, dispose. |
| `Shaders/AutoExposureHistogram.compute` | Kernels `KClear`, `KBuild`, `KReduce` (histogram build + percentile reduce + temporal adapt). |
| `Shaders/AutoExposureApply.shader` | Fullscreen multiply by `exp2(EV)` (from buffer, or fixed). |
| `Shaders/AutoExposureDebug.shader` | The histogram-bars + EV-marker overlay. |
| `Editor/AutoExposureRendererFeatureEditor.cs` | Auto-binds the compute/apply/debug shader references. |
