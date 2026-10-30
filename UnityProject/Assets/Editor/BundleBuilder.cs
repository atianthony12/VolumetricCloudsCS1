using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Builds the shader bundles the mod embeds: one per platform, because a bundle only holds
/// code for the graphics APIs of its build target (and the Mac's Metal can only come out of
/// a Mac-target build). Run headless (build-bundle.ps1 does):
///   Unity.exe -batchmode -nographics -quit -projectPath . -executeMethod BundleBuilder.Build -vcBundles win,linux,mac
/// -vcBundles names the bundles to build; without it, Windows alone (day to day, the author's call
/// 2026-10-30: Mac and Linux only for a release). Windows is always built last, so the editor is left
/// on the Windows target. Needs the Mac and Linux Build Support modules of this exact editor version.
/// </summary>
public static class BundleBuilder
{
    private const string OutputDir = "Bundles";
    private const string BundleName = "volumetricclouds";

    /// <summary>
    /// Every shader the mod ships. Add new ones here and nowhere else: build-bundle.ps1 reads
    /// this list out of this file and makes tools/bundle-apis.ps1 check every name in it.
    /// </summary>
    private static readonly string[] Shaders =
    {
        "Assets/VolumetricClouds/CloudRaymarch.shader",
        "Assets/VolumetricClouds/CloudShadowMap.shader",
        "Assets/VolumetricClouds/LightHalo.shader",
        "Assets/VolumetricClouds/LightHaloDynamic.shader",
        "Assets/VolumetricClouds/RainDrops.shader",
        "Assets/VolumetricClouds/LightningBolt.shader",
        "Assets/VolumetricClouds/LightningBoltMap.shader",
        "Assets/VolumetricClouds/Invisible.shader",
        "Assets/VolumetricClouds/FogLamps.shader",
        "Assets/VolumetricClouds/SkyLayer.shader",
        "Assets/VolumetricClouds/FunnelDebris.shader",
        "Assets/VolumetricClouds/TreesDefault.shader",
        "Assets/VolumetricClouds/TreesGroup.shader",
        "Assets/VolumetricClouds/WindDebris.shader",
    };

    private struct Target
    {
        public string Name;
        public BuildTarget BuildTarget;
        public GraphicsDeviceType[] Apis;
    }

    /// <summary>
    /// The three bundles and the graphics APIs each carries, in the order they are built (Windows
    /// last: each build switches the editor's target, and it should end on the one he runs). The names are the resource
    /// names ("volumetricclouds-win.bundle"...) ShaderBundle picks from by Application.platform,
    /// and build-bundle.ps1 checks each bundle against the same platform lists.
    /// - Windows: no D3D9 any more. The target-3.5 raymarch never had D3D9 code, so the mod
    ///   stands down there anyway; OpenGL Core is for a game started with -force-glcore.
    /// - Linux: the game's Linux player runs OpenGL Core. No Vulkan: the player was not
    ///   built with it, and a launch option can only pick an API the player has.
    /// - Mac: the game runs Metal on every Mac seen (read from a Mac's Player.log); OpenGL
    ///   Core beside it is cheap.
    /// </summary>
    private static readonly Target[] Targets =
    {
        new Target
        {
            Name = "linux",
            BuildTarget = BuildTarget.StandaloneLinux64,
            Apis = new[] { GraphicsDeviceType.OpenGLCore },
        },
        new Target
        {
            Name = "mac",
            BuildTarget = BuildTarget.StandaloneOSXUniversal,
            Apis = new[] { GraphicsDeviceType.Metal, GraphicsDeviceType.OpenGLCore },
        },
        new Target
        {
            Name = "win",
            BuildTarget = BuildTarget.StandaloneWindows64,
            Apis = new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.OpenGLCore },
        },
    };

    /// <summary>The bundles -vcBundles names (comma-separated), or "win" without it.</summary>
    private static string[] Wanted()
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-vcBundles")
                return args[i + 1].Split(',');
        }
        return new[] { "win" };
    }

    /// <summary>
    /// One UNCOMPRESSED bundle per target in Bundles/[name]/volumetricclouds, and that exact
    /// file is what ships. Uncompressed on purpose: tools/bundle-apis.ps1 can only read an
    /// uncompressed bundle, and what it verifies must be the bytes that are embedded, not a
    /// twin from a second compile (the LZMA-compressed shipped bundle of 1.1.0 and the
    /// uncompressed debug copy were two builds). ~115 KB instead of ~45 KB per bundle inside
    /// a 400 KB DLL is nothing; AssetBundle.LoadFromMemory takes either.
    /// </summary>
    public static void Build()
    {
        string[] wanted = Wanted();
        foreach (string name in wanted)
        {
            if (System.Array.FindIndex(Targets, t => t.Name == name) < 0)
            {
                Debug.LogError("BUNDLE BUILD: no bundle named '" + name + "' (win, linux, mac)");
                EditorApplication.Exit(1);
                return;
            }
        }

        foreach (Target target in Targets)
        {
            if (System.Array.IndexOf(wanted, target.Name) < 0)
            {
                Debug.Log("BUNDLE BUILD SKIPPED: " + target.Name + " (not asked for; left as it was)");
                continue;
            }

            if (!BuildOne(target))
            {
                EditorApplication.Exit(1);
                return;
            }
        }

        Debug.Log("BUNDLE BUILD ALL OK: " + string.Join(",", wanted));
    }

    /// <summary>The project settings a build changes to the game's, as found, to put back.</summary>
    public class GameRendering
    {
        public UnityEditor.Rendering.TierSettings[] Tiers;
        public ColorSpace ColourSpace;
    }

    private static readonly GraphicsTier[] AllTiers = { GraphicsTier.Tier1, GraphicsTier.Tier2, GraphicsTier.Tier3 };

    /// <summary>
    /// The game's rendering for the build, and what was there (put back by <see cref="Restore"/>):
    /// - every standalone tier on legacy deferred (light pre-pass): a build strips the passes of
    ///   rendering paths no tier uses, and the tree shaders (Trees*.shader) live in PrePassBase/
    ///   PrePassFinal -- without this the bundle held only their shadow casters, with no error;
    /// - LINEAR colour space, the game's: the tree shaders' generated code takes the light probe's
    ///   colour through UnityCG's ShadeSH9, which converts it to gamma when UNITY_COLORSPACE_GAMMA is
    ///   defined (the tree bench caught it against the game's own programs).
    /// No other shader here has a LightMode pass or any colour-space dependent code (checked 2026-10-30).
    /// </summary>
    public static GameRendering UseGameRendering()
    {
        GameRendering found = new GameRendering
        {
            Tiers = new UnityEditor.Rendering.TierSettings[AllTiers.Length],
            ColourSpace = PlayerSettings.colorSpace,
        };
        for (int i = 0; i < AllTiers.Length; i++)
        {
            UnityEditor.Rendering.TierSettings s = UnityEditor.Rendering.EditorGraphicsSettings.GetTierSettings(BuildTargetGroup.Standalone, AllTiers[i]);
            found.Tiers[i] = s;
            s.renderingPath = RenderingPath.DeferredLighting;
            UnityEditor.Rendering.EditorGraphicsSettings.SetTierSettings(BuildTargetGroup.Standalone, AllTiers[i], s);
        }

        PlayerSettings.colorSpace = ColorSpace.Linear;
        return found;
    }

    public static void Restore(GameRendering found)
    {
        for (int i = 0; i < AllTiers.Length; i++)
            UnityEditor.Rendering.EditorGraphicsSettings.SetTierSettings(BuildTargetGroup.Standalone, AllTiers[i], found.Tiers[i]);
        PlayerSettings.colorSpace = found.ColourSpace;
    }

    private static bool BuildOne(Target target)
    {
        GameRendering found = UseGameRendering();
        try
        {
            return BuildOneAsTheGame(target);
        }
        finally
        {
            Restore(found);
        }
    }

    private static bool BuildOneAsTheGame(Target target)
    {
        // Set from the script every time, never left as hidden state in the binary
        // ProjectSettings.asset: what each bundle holds must be readable here.
        PlayerSettings.SetUseDefaultGraphicsAPIs(target.BuildTarget, false);
        PlayerSettings.SetGraphicsAPIs(target.BuildTarget, target.Apis);

        GraphicsDeviceType[] readBack = PlayerSettings.GetGraphicsAPIs(target.BuildTarget);
        string list = "";
        for (int i = 0; i < readBack.Length; i++)
            list += (i > 0 ? ", " : "") + readBack[i];

        string dir = Path.Combine(OutputDir, target.Name);
        Directory.CreateDirectory(dir);

        AssetBundleBuild build = new AssetBundleBuild
        {
            assetBundleName = BundleName,
            assetNames = Shaders,
        };

        // Always rebuild. The incremental check only hashes the .shader files themselves,
        // so an edit confined to CloudCommon.cginc would otherwise yield a byte-identical,
        // stale bundle -- with no error anywhere.
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            dir,
            new[] { build },
            BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.UncompressedAssetBundle,
            target.BuildTarget);

        string what = "BUNDLE BUILD " + target.Name + " (" + target.BuildTarget + ", apis=[" + list + "])";

        if (manifest == null)
        {
            Debug.LogError(what + " FAILED");
            return false;
        }

        Debug.Log("BUNDLE BUILD OK: " + target.Name + " -> " + Path.Combine(dir, BundleName) + "  [" + what + "]");
        return true;
    }
}
