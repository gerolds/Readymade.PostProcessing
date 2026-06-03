// ADR: Validate the load-bearing GPU loop (KClear→KBuild→KReduce math) and per-camera state lifecycle without a full scene.
#nullable enable
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Readymade.PostProcessing.Tests
{
    public sealed class AutoExposureTests
    {
        const string kComputePath = "Assets/Readymade.PostProcessing/Exposure/Shaders/AutoExposureHistogram.compute";

        [Test]
        public void Reduce_MapsMeteredLuminanceToMiddleGrey()
        {
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(kComputePath);
            Assert.IsNotNull(compute, "AutoExposureHistogram.compute not found");

            int kClear = compute.FindKernel("KClear");
            int kBuild = compute.FindKernel("KBuild");
            int kReduce = compute.FindKernel("KReduce");

            const int size = 64;
            const float luminance = 0.09f; // constant grey; expect multiplier ~ 0.18 / 0.09 = 2
            var source = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true);
            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color(luminance, luminance, luminance, 1f);
            source.SetPixels(pixels);
            source.Apply(false, false);

            var histogram = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 256, sizeof(uint));
            var exposure = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 2);
            exposure.SetData(new[] { new Vector2(0f, 1f) });

            try
            {
                compute.SetBuffer(kClear, "_Histogram", histogram);
                compute.Dispatch(kClear, 4, 1, 1);

                compute.SetTexture(kBuild, "_Source", source);
                compute.SetBuffer(kBuild, "_Histogram", histogram);
                compute.SetVector("_SourceSize", new Vector4(size, size, 1f / size, 1f / size));
                compute.SetVector("_EvRange", new Vector4(-10f, 10f, 0f, 0f));
                compute.SetVector("_MeterParams", new Vector4(0.5f, 0.5f, 0f, 100f)); // average: flat weight
                compute.SetFloat("_CenterBias", 0f);
                compute.Dispatch(kBuild, size / 16, size / 16, 1);

                compute.SetBuffer(kReduce, "_Histogram", histogram);
                compute.SetBuffer(kReduce, "_Exposure", exposure);
                compute.SetVector("_EvRange", new Vector4(-10f, 10f, 0f, 0f));
                compute.SetVector("_Percent", new Vector4(0f, 1f, 0f, 0f)); // full window
                compute.SetVector("_AdaptParams", new Vector4(1f, 100f, 100f, 1f)); // reset = snap to target
                compute.SetVector("_ExposureLimits", new Vector4(-20f, 20f, 0f, 0f));
                compute.SetFloat("_ExposureComp", 0f);
                compute.SetFloat("_MiddleGrey", 0.18f);
                compute.Dispatch(kReduce, 1, 1, 1);

                var result = new Vector2[1];
                exposure.GetData(result);
                float multiplier = result[0].y;
                float expected = 0.18f / luminance;
                Assert.AreEqual(expected, multiplier, expected * 0.12f,
                    $"adapted multiplier {multiplier:F3} (EV {result[0].x:F3}); expected ~{expected:F3}");
            }
            finally
            {
                histogram.Dispose();
                exposure.Dispose();
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void Reduce_HoldsExposureWhenSourceIsBlack()
        {
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(kComputePath);
            Assert.IsNotNull(compute);

            int kClear = compute.FindKernel("KClear");
            int kReduce = compute.FindKernel("KReduce");

            var histogram = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 256, sizeof(uint));
            var exposure = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 2);
            const float seededEv = 1.5f;
            exposure.SetData(new[] { new Vector2(seededEv, Mathf.Pow(2f, seededEv)) });

            try
            {
                compute.SetBuffer(kClear, "_Histogram", histogram);
                compute.Dispatch(kClear, 4, 1, 1); // empty histogram (no Build)

                compute.SetBuffer(kReduce, "_Histogram", histogram);
                compute.SetBuffer(kReduce, "_Exposure", exposure);
                compute.SetVector("_EvRange", new Vector4(-10f, 10f, 0f, 0f));
                compute.SetVector("_Percent", new Vector4(0f, 1f, 0f, 0f));
                compute.SetVector("_AdaptParams", new Vector4(0.1f, 100f, 100f, 0f));
                compute.SetVector("_ExposureLimits", new Vector4(-20f, 20f, 0f, 0f));
                compute.SetFloat("_ExposureComp", 0f);
                compute.SetFloat("_MiddleGrey", 0.18f);
                compute.Dispatch(kReduce, 1, 1, 1);

                var result = new Vector2[1];
                exposure.GetData(result);
                Assert.AreEqual(seededEv, result[0].x, 1e-3f, "exposure must hold when nothing is metered");
            }
            finally
            {
                histogram.Dispose();
                exposure.Dispose();
            }
        }

        [Test]
        public void Store_ReturnsStableStatePerCamera()
        {
            var store = new PerCameraStateStore(256);
            var goA = new GameObject("camA");
            var goB = new GameObject("camB");
            try
            {
                Camera camA = goA.AddComponent<Camera>();
                Camera camB = goB.AddComponent<Camera>();

                AutoExposureState a1 = store.GetOrCreate(camA);
                AutoExposureState a2 = store.GetOrCreate(camA);
                AutoExposureState b1 = store.GetOrCreate(camB);

                Assert.AreSame(a1, a2, "same camera must reuse its state");
                Assert.AreNotSame(a1, b1, "different cameras must own distinct state");
                Assert.IsNotNull(a1.Exposure);
                Assert.IsNotNull(a1.Histogram);
                Assert.IsTrue(a1.NeedsReset, "fresh state should request a snap on first frame");
            }
            finally
            {
                store.Dispose();
                Object.DestroyImmediate(goA);
                Object.DestroyImmediate(goB);
            }
        }
    }
}
