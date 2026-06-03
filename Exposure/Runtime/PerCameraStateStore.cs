// ADR: Owns AutoExposureState lifetime keyed by Camera; prunes destroyed cameras via Unity's null-equality.
#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Readymade.PostProcessing
{
    /// <summary>
    /// Lazily allocates and disposes per-camera <see cref="AutoExposureState"/>. Keyed by <see cref="Camera"/>
    /// so destroyed cameras (which compare == null under Unity's overloaded equality) can be pruned and their
    /// GPU buffers released.
    /// </summary>
    internal sealed class PerCameraStateStore : IDisposable
    {
        readonly Dictionary<Camera, AutoExposureState> m_States = new();
        readonly List<Camera> m_Dead = new();
        readonly int m_Bins;

        public PerCameraStateStore(int bins) => m_Bins = bins;

        public AutoExposureState GetOrCreate(Camera camera)
        {
            if (!m_States.TryGetValue(camera, out AutoExposureState state))
            {
                state = new AutoExposureState(m_Bins);
                m_States.Add(camera, state);
            }

            return state;
        }

        /// <summary>Releases state for cameras that have been destroyed. Cheap; call once per frame.</summary>
        public void Prune()
        {
            m_Dead.Clear();
            foreach (KeyValuePair<Camera, AutoExposureState> kv in m_States)
            {
                if (kv.Key == null)
                    m_Dead.Add(kv.Key!);
            }

            foreach (Camera dead in m_Dead)
            {
                m_States[dead].Dispose();
                m_States.Remove(dead);
            }
        }

        public void Dispose()
        {
            foreach (KeyValuePair<Camera, AutoExposureState> kv in m_States)
                kv.Value.Dispose();

            m_States.Clear();
        }
    }
}
