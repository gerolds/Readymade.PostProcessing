// ADR: Per-camera, frame-persistent froxel history — the temporal coherence primitive. Transient render-graph textures cannot survive a frame, so the scatter history MUST be feature-owned (imported each frame). Never read back to the CPU.
// ADR: Ping-pong by frame parity; reprojection needs the previous frame's camera basis, so we cache view-proj + position + forward alongside the textures.
#nullable enable
using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Readymade.PostProcessing
{
    /// <summary>
    /// Owns the two 3D scatter buffers that survive frame-to-frame for one camera (ping-ponged so this frame
    /// reads last frame's result and writes the blended one), plus the previous-frame camera basis the froxel
    /// reprojection needs. Reallocated when the grid resolution changes; imported into the render graph each frame.
    /// </summary>
    internal sealed class VolumetricFogState : IDisposable
    {
        readonly RTHandle?[] m_Scatter = new RTHandle?[2];

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Depth { get; private set; }

        /// <summary>View-projection used to render the previous frame (GPU convention). Identity until the first frame completes.</summary>
        public Matrix4x4 PrevViewProj = Matrix4x4.identity;
        public Vector3 PrevCameraPos;
        public Vector3 PrevCameraForward = Vector3.forward;

        /// <summary>False on the first frame and after a reset — forces the blend to ignore (garbage) history.</summary>
        public bool HasHistory;

        public int LastFrame = -1;

        /// <summary>The buffer written this frame (becomes next frame's history).</summary>
        public RTHandle Current(int frame) => m_Scatter[frame & 1]!;

        /// <summary>The buffer written last frame (this frame's reprojection source).</summary>
        public RTHandle History(int frame) => m_Scatter[(frame & 1) ^ 1]!;

        /// <summary>(Re)allocates the ping-pong pair if the grid size changed. Returns true if a (re)allocation happened (history is then invalid).</summary>
        public bool EnsureAllocated(int width, int height, int depth)
        {
            if (m_Scatter[0] != null && Width == width && Height == height && Depth == depth)
                return false;

            ReleaseTextures();
            for (int i = 0; i < 2; i++)
            {
                m_Scatter[i] = RTHandles.Alloc(
                    width, height, depth,
                    colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                    filterMode: FilterMode.Bilinear,
                    wrapMode: TextureWrapMode.Clamp,
                    dimension: TextureDimension.Tex3D,
                    enableRandomWrite: true,
                    name: $"_FogScatterHistory{i}");
            }

            Width = width;
            Height = height;
            Depth = depth;
            HasHistory = false;
            return true;
        }

        public void Reset() => HasHistory = false;

        void ReleaseTextures()
        {
            for (int i = 0; i < m_Scatter.Length; i++)
            {
                m_Scatter[i]?.Release();
                m_Scatter[i] = null;
            }
        }

        public void Dispose() => ReleaseTextures();
    }
}
