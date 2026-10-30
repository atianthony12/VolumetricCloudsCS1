using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The tree bench (tools/tree-bench.ps1). Build: the two tree shaders and their game-only twins
/// (TreeBenchGame*.shader, VW's wind compiled out) into one uncompressed Windows bundle, D3D11 only, so
/// their compiled programs can be set against the game's own. Run: TreeBenchRun (the renders).
/// </summary>
public static class TreeBench
{
    public const string Dir = "TreeBench/Bundle";

    private static readonly string[] Shaders =
    {
        "Assets/VolumetricClouds/TreesDefault.shader",
        "Assets/VolumetricClouds/TreesGroup.shader",
        "Assets/VolumetricClouds/TreeBenchGameDefault.shader",
        "Assets/VolumetricClouds/TreeBenchGameGroup.shader",
    };

    /// <summary>Each shader's compile messages (a surface shader's errors stay quiet in a build).</summary>
    public static void Check()
    {
        System.Reflection.MethodInfo errors = typeof(ShaderUtil).GetMethod("GetShaderErrors",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        foreach (string path in Shaders)
        {
            Shader s = AssetDatabase.LoadAssetAtPath<Shader>(path);
            Material m = s != null ? new Material(s) : null;
            Debug.Log("TREE BENCH SHADER " + path + ": " + (s == null ? "not loaded" : "supported " + s.isSupported + ", passes " + m.passCount));
            if (s == null || errors == null)
                continue;
            System.Array list = (System.Array)errors.Invoke(null, new object[] { s });
            foreach (object e in list)
            {
                System.Type t = e.GetType();
                Debug.Log("TREE BENCH MESSAGE " + path + ": " + t.GetField("message").GetValue(e) + " | " + t.GetField("messageDetails").GetValue(e) +
                          " line " + t.GetField("line").GetValue(e) + " warning " + t.GetField("warning").GetValue(e));
            }
        }
    }

    public static void Build()
    {
        GraphicsDeviceType[] before = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
        bool beforeDefault = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        BundleBuilder.GameRendering found = BundleBuilder.UseGameRendering();
        try
        {
            Directory.CreateDirectory(Dir);
            AssetBundleBuild build = new AssetBundleBuild { assetBundleName = "trees", assetNames = Shaders };
            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                Dir, new[] { build },
                BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.UncompressedAssetBundle,
                BuildTarget.StandaloneWindows64);
            Debug.Log(manifest != null ? "TREE BENCH BUILD OK: " + Path.Combine(Dir, "trees") : "TREE BENCH BUILD FAILED");
            if (manifest == null)
                EditorApplication.Exit(1);
        }
        finally
        {
            BundleBuilder.Restore(found);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, before);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, beforeDefault);
        }
    }
}
