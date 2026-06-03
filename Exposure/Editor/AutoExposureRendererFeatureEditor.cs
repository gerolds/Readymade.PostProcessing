// ADR: Auto-binds the hidden compute/shader refs by asset search so the feature is a true drop-in after import.
#nullable enable
using UnityEditor;
using UnityEngine;

namespace Readymade.PostProcessing.Editor
{
    [CustomEditor(typeof(AutoExposureRendererFeature))]
    internal sealed class AutoExposureRendererFeatureEditor : UnityEditor.Editor
    {
        SerializedProperty m_Compute = null!;
        SerializedProperty m_ApplyShader = null!;
        SerializedProperty m_DebugShader = null!;
        SerializedProperty m_InjectionPoint = null!;

        void OnEnable()
        {
            m_Compute = serializedObject.FindProperty("m_HistogramCompute");
            m_ApplyShader = serializedObject.FindProperty("m_ApplyShader");
            m_DebugShader = serializedObject.FindProperty("m_DebugShader");
            m_InjectionPoint = serializedObject.FindProperty("m_InjectionPoint");
            AutoAssign();
        }

        void AutoAssign()
        {
            bool dirty = false;

            if (m_Compute.objectReferenceValue == null)
            {
                ComputeShader? cs = FindAsset<ComputeShader>("AutoExposureHistogram t:ComputeShader");
                if (cs != null)
                {
                    m_Compute.objectReferenceValue = cs;
                    dirty = true;
                }
            }

            if (m_ApplyShader.objectReferenceValue == null)
            {
                Shader? sh = Shader.Find("Hidden/Readymade/AutoExposureApply");
                if (sh != null)
                {
                    m_ApplyShader.objectReferenceValue = sh;
                    dirty = true;
                }
            }

            if (m_DebugShader.objectReferenceValue == null)
            {
                Shader? sh = Shader.Find("Hidden/Readymade/AutoExposureDebug");
                if (sh != null)
                {
                    m_DebugShader.objectReferenceValue = sh;
                    dirty = true;
                }
            }

            if (dirty)
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        static T? FindAsset<T>(string filter) where T : Object
        {
            foreach (string guid in AssetDatabase.FindAssets(filter))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                T asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null)
                    return asset;
            }

            return null;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(m_InjectionPoint);

            if (m_Compute.objectReferenceValue == null)
                EditorGUILayout.HelpBox("Histogram compute shader not found — automatic exposure is disabled (fixed mode still works).", MessageType.Warning);
            if (m_ApplyShader.objectReferenceValue == null)
                EditorGUILayout.HelpBox("Apply shader not found — the feature cannot run.", MessageType.Error);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
