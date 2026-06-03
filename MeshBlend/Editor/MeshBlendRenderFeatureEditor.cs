// ADR: Auto-binds the hidden serialized shader refs (blend / ID / debug / JFA) by
// Shader.Find so the feature is true drop-in (no manual slot wiring) and the refs
// ship in builds. LogShaderMessages() is a dev diagnostic for this shader-heavy module:
// Metal silently no-ops some shader faults (gradient-in-loop), so dump ShaderUtil errors.
#nullable enable
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Readymade.PostProcessing
{
    [CustomEditor(typeof(MeshBlendRenderFeature))]
    internal sealed class MeshBlendRenderFeatureEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            AutoBindShaders();
            DrawDefaultInspector();
        }

        void AutoBindShaders()
        {
            bool changed = false;
            changed |= Bind("m_BlendShader", "Hidden/Readymade/MeshBlend");
            changed |= Bind("m_IdShader", "Hidden/Readymade/MeshBlendId");
            changed |= Bind("m_DebugShader", "Hidden/Readymade/MeshBlendDebug");
            changed |= Bind("m_JfaShader", "Hidden/Readymade/MeshBlendJFA");

            if (changed)
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        // SPIKE diagnostic: log the manual-prepass registry so we can tell "no draws" (empty registry)
        // from "draws wrote 0" (shader/precedence). Remove with the spike.
        [MenuItem("Tools/Readymade/Log MeshBlend Manual Spike State")]
        public static void LogManualSpikeState()
        {
            IReadOnlyList<MeshBlendObject> active = MeshBlendObject.Active;
            Debug.Log($"[MeshBlend] registry Active.Count = {active.Count}");
            for (int i = 0; i < active.Count; i++)
            {
                MeshBlendObject o = active[i];
                if (o == null) { Debug.Log($"  [{i}] <null>"); continue; }
                Renderer? r = o.Renderer;
                Debug.Log($"  [{i}] '{o.name}' renderer={(r != null ? r.GetType().Name : "null")} submeshes={o.SubmeshCount} id[0]={o.PackedForSubmesh(0)} id[1]={o.PackedForSubmesh(1)}");
            }
        }

        // Dump ShaderUtil compile messages for the MeshBlend shaders. Invoke via reflection
        // (run_method_in_unity) or Tools menu — surfaces include/syntax errors that Metal
        // otherwise swallows. Logs an explicit "OK" when a shader has no messages.
        [MenuItem("Tools/Readymade/Check MeshBlend Shaders")]
        public static void LogShaderMessages()
        {
            string[] names =
            {
                "Hidden/Readymade/MeshBlend",
                "Hidden/Readymade/MeshBlendJFA",
                "Hidden/Readymade/MeshBlendId",
                "Hidden/Readymade/MeshBlendDebug",
            };

            foreach (string name in names)
            {
                Shader? sh = Shader.Find(name);
                if (sh == null)
                {
                    Debug.LogError($"[MeshBlend] shader NOT FOUND: {name}");
                    continue;
                }

                int count = UnityEditor.ShaderUtil.GetShaderMessageCount(sh);
                if (count == 0)
                {
                    Debug.Log($"[MeshBlend] {name}: OK (0 messages)");
                    continue;
                }

                UnityEditor.ShaderMessage[] msgs = UnityEditor.ShaderUtil.GetShaderMessages(sh);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"[MeshBlend] {name}: {count} message(s)");
                foreach (UnityEditor.ShaderMessage m in msgs)
                    sb.AppendLine($"  [{m.severity}] {m.message} {m.messageDetails} (line {m.line}, {m.platform})");
                Debug.LogError(sb.ToString());
            }
        }

        bool Bind(string property, string shaderName)
        {
            SerializedProperty prop = serializedObject.FindProperty(property);
            if (prop.objectReferenceValue != null)
                return false;

            Shader? sh = Shader.Find(shaderName);
            if (sh == null)
                return false;

            prop.objectReferenceValue = sh;
            return true;
        }
    }
}
