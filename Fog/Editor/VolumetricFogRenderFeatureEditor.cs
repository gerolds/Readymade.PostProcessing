// ADR: Auto-binds the hidden serialized compute/apply-shader refs by asset lookup so the feature is true drop-in (no manual slot wiring) and the refs ship in builds.
#nullable enable
using UnityEditor;
using UnityEngine;

namespace Readymade.PostProcessing
{
    [CustomEditor(typeof(VolumetricFogRenderFeature))]
    internal sealed class VolumetricFogRenderFeatureEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            AutoBindShaders();
            DrawDefaultInspector();
        }

        void AutoBindShaders()
        {
            SerializedProperty compute = serializedObject.FindProperty("m_FroxelCompute");
            SerializedProperty shader = serializedObject.FindProperty("m_ApplyShader");
            bool changed = false;

            if (compute.objectReferenceValue == null)
            {
                ComputeShader? cs = FindAsset<ComputeShader>("FogFroxel t:ComputeShader");
                if (cs != null)
                {
                    compute.objectReferenceValue = cs;
                    changed = true;
                }
            }

            if (shader.objectReferenceValue == null)
            {
                Shader? sh = Shader.Find("Hidden/Readymade/FogApply");
                if (sh != null)
                {
                    shader.objectReferenceValue = sh;
                    changed = true;
                }
            }

            if (changed)
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        static T? FindAsset<T>(string filter) where T : Object
        {
            foreach (string guid in AssetDatabase.FindAssets(filter))
            {
                var asset = AssetDatabase.LoadAssetAtPath<T>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset != null)
                    return asset;
            }

            return null;
        }
    }
}
