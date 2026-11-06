using System;
using System.Collections.Generic;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// WHICH CLOUD SHADERS THIS MACHINE DRAWS (2026-11-02): the whole ones (CloudRaymarch, CloudShadowMap)
    /// or "Simpler clouds" (CloudRaymarchLite, CloudShadowMapLite: the same pass compiled with VC_LITE,
    /// about 1.4.0's size and its 11 textures; CloudRaymarchPass.cginc says what it leaves out). Chosen
    /// once per city, before the controller exists (Loader), and fixed for that city.
    /// </summary>
    /// <remarks>
    /// Why: a 2017 iMac (Radeon Pro 555, Metal) lost every cloud with 1.5.x and showed no message.
    /// Shader.isSupported only says the shader's source compiled; on Metal the GPU's own compile comes
    /// when a program is first DRAWN, and a program it refuses is skipped there, silently. So each shader
    /// is drawn once into a tiny target with _VCProbe on (the fragment writes one known colour; a uniform,
    /// so the whole program is built and run) and read back: the colour there = the driver runs it.
    /// The waterspouts' variant (VC_FUNNELS) too: if only that one fails, the waterspouts stay away.
    ///
    /// A CONTROL draw first, the same way with Unity's own copy: if even that does not read back (a
    /// readback this machine cannot do), nothing is concluded and the whole shaders are drawn, exactly as
    /// before this check existed. So a check that cannot work never moves anyone to the light shader.
    ///
    /// The order: the whole pair, then the light pair, then the stand-down (Loader's message). The
    /// clouds and their shadows always come from the same kind (both whole or both light), so a shadow
    /// never falls from a cloud that is not drawn. "Simpler clouds" ticked: the light pair first.
    ///
    /// While the light pair is drawn every feature it leaves out is switched off at its one source
    /// (<see cref="Lite"/>: CloudStyle.SoftDrawn / MixedWanted, CloudShaderParams.CurvedStrength,
    /// CloudVolume's Smoother clouds, CloudFog's valleys and water, Waterspouts.Chance,
    /// BoltMap), so the light shader is never handed what it cannot draw and the settings themselves
    /// stay as the player set them (invariant 15): the next city with the whole shaders has them all.
    /// </remarks>
    public static class CloudShaders
    {
        public const string RaymarchLite = "VolumetricClouds/CloudRaymarchLite";
        public const string ShadowMapLite = "VolumetricClouds/CloudShadowMapLite";

        /// <summary>The waterspouts' variant of the whole cloud shader.</summary>
        private const string FunnelsKeyword = "VC_FUNNELS";

        /// <summary>What the probe's fragment writes (CloudRaymarchPass PROBE_COLOUR, CloudShadowMapPass).</summary>
        private static readonly Color ProbeColour = new Color(0.25f, 0.5f, 0.75f, 1f);

        /// <summary>How far a read-back channel may be from it (8 bits: 1/255 = 0.004).</summary>
        private const float ProbeTolerance = 0.04f;

        private const int ProbeSize = 4;

        private static readonly int IdProbe = Shader.PropertyToID("_VCProbe");

        /// <summary>This city draws the light shaders: every feature they leave out is off.</summary>
        public static bool Lite { get; private set; }

        /// <summary>This city's cloud shader (null before a choice, or after a stand-down).</summary>
        public static Shader Raymarch { get; private set; }

        /// <summary>This city's shadow-map shader; null: no cloud shadows (CloudShadowMap.TryCreate).</summary>
        public static Shader ShadowMap { get; private set; }

        /// <summary>The whole cloud shader's waterspouts variant runs here (false under the light one: it has none).</summary>
        public static bool FunnelsDrawn { get; private set; }

        /// <summary>Why this city draws what it draws, for SystemReport's line.</summary>
        public static string Reason { get; private set; }

        /// <summary>Each check's answer, kept for the launch: the machine does not change between cities.</summary>
        private static readonly Dictionary<string, Verdict> _verdicts = new Dictionary<string, Verdict>();

        /// <summary>The control draw's answer: 0 not tried, 1 reads back, -1 cannot (every check then passes).</summary>
        private static int _control;

        private enum Verdict
        {
            Runs,
            Missing,        // not in the bundle, or isSupported false (its source did not compile here)
            Refused,        // compiled, but drawing it left the target as it was
        }

        /// <summary>
        /// Picks this city's shaders (Loader, before the controller). False: neither cloud shader runs
        /// here -- the stand-down. Never throws.
        /// </summary>
        public static bool Choose(bool simplerWanted)
        {
            Lite = false;
            Raymarch = null;
            ShadowMap = null;
            FunnelsDrawn = false;
            Reason = null;

            try
            {
                if (!simplerWanted)
                {
                    if (Runs(ShaderBundle.Raymarch))
                    {
                        if (Runs(ShaderBundle.ShadowMap))
                            return UseWhole(ShaderBundle.ShadowMap, "the whole shaders run here");

                        // The shadows' whole shader does not run: the light pair, so clouds and shadows agree.
                        if (Runs(RaymarchLite))
                            return UseLite("the cloud shadow shader does not run on this graphics driver (" + Describe(ShaderBundle.ShadowMap) + ")");

                        return UseWhole(null, "the cloud shadow shader does not run here (" + Describe(ShaderBundle.ShadowMap) +
                                              "), nor the light cloud shader: clouds without shadows");
                    }

                    if (Runs(RaymarchLite))
                        return UseLite("the whole cloud shader does not run on this graphics driver (" + Describe(ShaderBundle.Raymarch) + ")");

                    return false;
                }

                if (Runs(RaymarchLite))
                    return UseLite("\"Simpler clouds\" is ticked");

                // Ticked, but the light one does not run and the whole one does: the clouds rather than none.
                if (Runs(ShaderBundle.Raymarch))
                    return UseWhole(Runs(ShaderBundle.ShadowMap) ? ShaderBundle.ShadowMap : null,
                                    "\"Simpler clouds\" is ticked but its shader does not run here (" + Describe(RaymarchLite) + ")");

                return false;
            }
            catch (Exception e)
            {
                // Never cost the city its load: the old way, the whole shader if it compiled.
                Log.Error("cloud shaders: choosing threw; the whole shaders, unchecked.", e);
                Raymarch = ShaderBundle.Get(ShaderBundle.Raymarch);
                ShadowMap = Raymarch != null ? ShaderBundle.Get(ShaderBundle.ShadowMap) : null;
                FunnelsDrawn = Raymarch != null;
                Reason = "the check threw";
                return Raymarch != null;
            }
        }

        /// <summary>The city closed: nothing is drawn until the next choice (the rows it greys are live again).</summary>
        public static void Clear()
        {
            Lite = false;
            Raymarch = null;
            ShadowMap = null;
            FunnelsDrawn = false;
            Reason = null;
        }

        private static bool UseWhole(string shadowName, string reason)
        {
            Raymarch = ShaderBundle.Get(ShaderBundle.Raymarch);
            ShadowMap = shadowName != null ? ShaderBundle.Get(shadowName) : null;
            FunnelsDrawn = Runs(ShaderBundle.Raymarch, FunnelsKeyword);
            Reason = reason;
            Log.Msg("cloud shaders: the whole ones (" + reason + ")" +
                    (ShadowMap == null ? "; no cloud shadows" : "") +
                    (FunnelsDrawn ? "" : "; the waterspouts' variant does not run here (" + Describe(ShaderBundle.Raymarch, FunnelsKeyword) +
                                         "): no waterspouts") +
                    ProbeNote());
            return Raymarch != null;
        }

        private static bool UseLite(string reason)
        {
            Lite = true;
            Raymarch = ShaderBundle.Get(RaymarchLite);
            ShadowMap = Runs(ShadowMapLite) ? ShaderBundle.Get(ShadowMapLite) : null;
            Reason = reason;
            Log.Msg("cloud shaders: SIMPLER CLOUDS (" + reason + "): no Soft or Mixed style (drawn Classic), Curved clouds, " +
                    "Smoother clouds, Clouds to the horizon, fog in valleys or over water, waterspouts, painted sky or bolt map" +
                    (ShadowMap == null ? "; no cloud shadows (" + Describe(ShadowMapLite) + ")" : "") + ProbeNote());
            return Raymarch != null;
        }

        private static string ProbeNote()
        {
            return _control < 0 ? "; this machine could not read the check back, so it was trusted unchecked" : "";
        }

        private static bool Runs(string shaderName)
        {
            return Runs(shaderName, null);
        }

        private static bool Runs(string shaderName, string keyword)
        {
            return Check(shaderName, keyword) == Verdict.Runs;
        }

        private static string Describe(string shaderName)
        {
            return Describe(shaderName, null);
        }

        private static string Describe(string shaderName, string keyword)
        {
            Verdict verdict = Check(shaderName, keyword);
            string name = shaderName.Substring(shaderName.IndexOf('/') + 1) + (keyword != null ? " " + keyword : "");
            return name + (verdict == Verdict.Missing ? " not supported" : verdict == Verdict.Refused ? " drew nothing" : " runs");
        }

        private static Verdict Check(string shaderName, string keyword)
        {
            string key = keyword != null ? shaderName + "+" + keyword : shaderName;
            Verdict verdict;
            if (_verdicts.TryGetValue(key, out verdict))
                return verdict;

            Shader shader = ShaderBundle.Get(shaderName);
            if (shader == null)
                verdict = Verdict.Missing;
            else
                verdict = Draws(shader, keyword) ? Verdict.Runs : Verdict.Refused;

            _verdicts[key] = verdict;
            if (verdict == Verdict.Refused)
                Log.Warn("cloud shaders: " + key + " compiled but DREW NOTHING on this graphics driver (" +
                         SystemInfo.graphicsDeviceType + ", '" + SystemInfo.graphicsDeviceName + "', " + SystemInfo.graphicsDeviceVersion + ")");
            else if (Log.Detailed)
                Log.Detail("cloud shaders: " + key + " " + verdict);

            return verdict;
        }

        /// <summary>The probe: true when the shader's draw put the colour in the target -- or when this machine cannot say.</summary>
        private static bool Draws(Shader shader, string keyword)
        {
            if (!ControlReadsBack())
                return true;

            Material material = new Material(shader) { name = "VolumetricClouds probe" };
            Texture3D volume = null;
            try
            {
                material.SetFloat(IdProbe, 1f);
                if (keyword != null)
                    material.EnableKeyword(keyword);

                // Every 3D texture the pass declares gets a real one (ARGB32: never R8, invariant of the
                // noise textures) -- nothing is read with the probe on, but nothing is left unbound either.
                volume = new Texture3D(1, 1, 1, TextureFormat.ARGB32, false) { name = "VolumetricClouds probe volume" };
                volume.SetPixels32(new[] { new Color32(0, 0, 0, 0) });
                volume.Apply(false);
                material.SetTexture("_NoiseTex", volume);
                material.SetTexture("_CumulusTex", volume);
                material.SetTexture("_DetailTex", volume);

                return DrawAndRead(material, null);
            }
            finally
            {
                UnityEngine.Object.Destroy(material);
                if (volume != null)
                    UnityEngine.Object.Destroy(volume);
            }
        }

        /// <summary>
        /// Unity's own copy of a texture of the probe's colour, through the same targets and readback as
        /// every check: if that colour does not come back, no check can say anything (and none is believed).
        /// </summary>
        private static bool ControlReadsBack()
        {
            if (_control != 0)
                return _control > 0;

            Texture2D source = new Texture2D(ProbeSize, ProbeSize, TextureFormat.RGBA32, false, true) { name = "VolumetricClouds probe control" };
            try
            {
                var pixels = new Color[ProbeSize * ProbeSize];
                for (int i = 0; i < pixels.Length; i++)
                    pixels[i] = ProbeColour;
                source.SetPixels(pixels);
                source.Apply(false);

                bool ok = DrawAndRead(null, source);
                _control = ok ? 1 : -1;
                Log.Msg("cloud shaders: driver check " + (ok ? "ready" : "UNAVAILABLE (the control did not read back): every shader that compiles is trusted") +
                        " (" + SystemInfo.graphicsDeviceType + ", '" + SystemInfo.graphicsDeviceName + "', " + SystemInfo.graphicsDeviceVersion + ")");
            }
            catch (Exception e)
            {
                _control = -1;
                Log.Error("cloud shaders: the driver check's control threw; every shader that compiles is trusted.", e);
            }
            finally
            {
                UnityEngine.Object.Destroy(source);
            }

            return _control > 0;
        }

        /// <summary>
        /// Clears a small target to nothing, draws into it -- the material's pass 0 over the whole of it,
        /// or a plain copy of `copy` -- then reads its middle back through an 8-bit target. The first is
        /// the camera's format (half floats) where the machine has it, so the pipeline the check builds is
        /// the one the camera's draw needs.
        /// </summary>
        private static bool DrawAndRead(Material material, Texture copy)
        {
            RenderTextureFormat format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)
                ? RenderTextureFormat.ARGBHalf
                : RenderTextureFormat.ARGB32;
            RenderTexture target = RenderTexture.GetTemporary(ProbeSize, ProbeSize, 0, format, RenderTextureReadWrite.Linear);
            RenderTexture read = RenderTexture.GetTemporary(ProbeSize, ProbeSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Texture2D pixels = new Texture2D(ProbeSize, ProbeSize, TextureFormat.RGBA32, false, true);
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));

                if (material != null)
                    Graphics.Blit(Texture2D.blackTexture, target, material, 0);
                else
                    Graphics.Blit(copy, target);

                Graphics.Blit(target, read);
                RenderTexture.active = read;
                pixels.ReadPixels(new Rect(0, 0, ProbeSize, ProbeSize), 0, 0, false);

                Color c = pixels.GetPixel(ProbeSize / 2, ProbeSize / 2);
                return Mathf.Abs(c.r - ProbeColour.r) < ProbeTolerance && Mathf.Abs(c.g - ProbeColour.g) < ProbeTolerance &&
                       Mathf.Abs(c.b - ProbeColour.b) < ProbeTolerance && Mathf.Abs(c.a - ProbeColour.a) < ProbeTolerance;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                RenderTexture.ReleaseTemporary(read);
                UnityEngine.Object.Destroy(pixels);
            }
        }
    }
}
