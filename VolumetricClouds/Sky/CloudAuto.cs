using System;
using System.Globalization;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// THE AUTO STYLE (<see cref="CloudStyle.Auto"/>): the Mixed style's sky (<see cref="CloudMix"/>: a type
    /// pattern gives each stretch of sky to the layer, the puffs or the heaps) with its proportions following
    /// the cover. Fair weather is mostly Soft's puffs with a few stretches of heaps, the heaps spread as the
    /// cover rises and from <see cref="PuffsTo"/> the sky IS the Cumulus style. The player can still pick
    /// any style by hand; this is one more choice on the same row.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>There is no timer and no state: everything here is a function of the cover, which the weather
    /// already moves slowly, so the sky changes as slowly as it closes up.</item>
    /// <item>It is not a cross-fade of two pictures. The heaps' stretches grow (<see cref="HeapSky"/>) and
    /// the puffs leave as Soft's do when its intensity falls, first where the map is lowest: clouds form and
    /// clouds go. Each place draws the denser of puffs, heaps and layer, the same max the styles already use.</item>
    /// <item>The puffs go LATER than the heaps come (<see cref="PuffsFrom"/>): together, the sky emptied half
    /// way through (render-detail.ps1 -Set auto-measure, 2026-11-06).</item>
    /// <item>From <see cref="PuffsTo"/> it IS Cumulus: the same uniforms and the same shader branch
    /// (CloudShaderParams.ApplyStyle), nothing of the rest read. tools/test-auto.ps1 holds it to that.
    /// (Until 2026-11-06, the author's call, it was exactly Soft up to 60% too.)</item>
    /// </list>
    /// Pure, and in C# 5 like CloudSoft: tools/test-auto.ps1 compiles the sources.
    /// </remarks>
    public static class CloudAuto
    {
        /// <summary>The cover the layer starts to go to Cumulus's at, and the cover it has at.</summary>
        public const float HeapsFrom = 0.60f, HeapsTo = 0.90f;

        /// <summary>The cover the puffs start to go at, and the cover the last of them are gone at.</summary>
        public const float PuffsFrom = 0.76f, PuffsTo = 0.92f;

        /// <summary>The cover the light starts to rise from the puffs' level at, and the cover it is the heaps' at.</summary>
        public const float LightFrom = 0.72f, LightTo = 0.86f;

        /// <summary>The share of a fair sky that leans to heaps, and the cover that share starts to grow from, to all of it at <see cref="HeapsTo"/>.</summary>
        public const float FairHeapSky = 0.1f, SkyFrom = 0.3f;

        /// <summary>
        /// The intensity the puffs are cut as at where the type leans to layer, as a share of the cover
        /// (elsewhere the cover itself): more than Mixed's, so a fair sky is about as cloudy as Soft's.
        /// </summary>
        public const float PuffsInLayerArea = 0.7f;

        /// <summary>How far the layer has gone to Cumulus's, 0..1.</summary>
        public static float HeapShare(float cover)
        {
            return SmoothStep(HeapsFrom, HeapsTo, cover);
        }

        /// <summary>The share of the sky that leans to heaps at this cover: a few stretches in fair weather, all of it from <see cref="HeapsTo"/>.</summary>
        public static float HeapSky(float cover)
        {
            return FairHeapSky + (1f - FairHeapSky) * SmoothStep(SkyFrom, HeapsTo, cover);
        }

        /// <summary>How far the puffs have gone, 0..1.</summary>
        public static float PuffFade(float cover)
        {
            return SmoothStep(PuffsFrom, PuffsTo, cover);
        }

        /// <summary>Any puffs left at this cover: below it the type is read, from it the sky is Cumulus.</summary>
        public static bool PuffsAt(float cover)
        {
            return cover < PuffsTo;
        }

        /// <summary>
        /// The intensity Soft's puffs are cut as at (CloudSoft.PuffCoverage) where the type leans to layer
        /// (<paramref name="area"/> 0), neither way (1) or to puffs (2): the cover itself there, so a stretch
        /// of puffs is Soft's own sky, going as the cover closes up.
        /// </summary>
        public static float PuffIntensity(float cover, int area)
        {
            return Clamp01(cover) * (area == 0 ? PuffsInLayerArea : 1f) * (1f - PuffFade(cover));
        }

        /// <summary>
        /// The cover the Classic layer is cut at where the type leans to layer (<paramref name="area"/> 0),
        /// neither way (1) or to puffs (2, Soft's own): Mixed's and Soft's going to Cumulus's (the cover times
        /// the "Classic layer" row, <paramref name="amount"/>) with <see cref="HeapShare"/>.
        /// </summary>
        public static float LayerCover(CloudSoft.Look look, float cover, float amount, int area)
        {
            float c = Clamp01(cover);
            float own = area == 0 ? CloudMix.LayerCoverInLayerArea(c) : area == 1 ? CloudMix.LayerCover(c) : CloudSoft.LayerCover(look, c, 1f);
            return Mix(own, c, HeapShare(c)) * Clamp01(amount);
        }

        /// <summary>
        /// The cover the rain, lightning's cloud test and the rainbow's are cut at (invariant 2: it falls from
        /// drawn cloud): the least any stretch of the sky draws, each drawing the larger of its puffs' and its
        /// layer's, never more than the cover -- and rising to the cover itself as the layer goes to Cumulus's.
        /// </summary>
        public static float DrawnCoverage(CloudSoft.Look look, float cover, float amount)
        {
            float c = Clamp01(cover);
            float least = 1f;
            float puffsL = CloudSoft.PuffCoverage(look, PuffIntensity(c, 0)), puffsM = CloudSoft.PuffCoverage(look, PuffIntensity(c, 1));
            float puffsP = CloudSoft.PuffCoverage(look, PuffIntensity(c, 2));
            float layerL = LayerCover(look, c, amount, 0), layerM = LayerCover(look, c, amount, 1), layerP = LayerCover(look, c, amount, 2);
            for (int i = 0; i <= 8; i++)
            {
                float t = i / 8f;
                least = Math.Min(least, Math.Max(Between(puffsL, puffsM, puffsP, t), Between(layerL, layerM, layerP, t)));
            }

            return Math.Min(c, Math.Max(least, c * HeapShare(c)));
        }

        /// <summary>
        /// The clouds' light level where the sky does not lean to heaps (CloudStyle.CloudLightScale): Soft's up
        /// to <see cref="LightFrom"/>, 1 (Cumulus's) from <see cref="LightTo"/>. Measured against Cumulus's
        /// brightness at the same Cloud brightness (render-detail.ps1 -Set auto-light on his rows, -default on
        /// the default ones, the same answer on both): x0.6 at 76%, x0.8 at 80%, x0.95 at 84%, x1 by 88%. The
        /// heaps' stretches are at 1 all along (CloudMix.LightBoost).
        /// </summary>
        public static float LightScale(CloudSoft.Look look, float cover, float amount)
        {
            float c = Clamp01(cover);
            float soft = CloudSoft.LightScaleAt(look, CloudSoft.LightShare(look, c, amount));
            return Mix(soft, 1f, SmoothStep(LightFrom, LightTo, c));
        }

        /// <summary>
        /// How much of the cloud fragments' share is drawn: Soft's (they shape its layer, which only comes in at
        /// high intensity) going to all of it, as under Cumulus.
        /// </summary>
        public static float FragmentFactor(CloudSoft.Look look, float cover, float amount)
        {
            float c = Clamp01(cover);
            return Mix(CloudSoft.LayerShare(look, c) * Clamp01(amount), 1f, HeapShare(c));
        }

        /// <summary>a at t = 0, b at t = 1: exactly, so outside the band nothing moves.</summary>
        public static float Mix(float a, float b, float t)
        {
            return t <= 0f ? a : t >= 1f ? b : a + (b - a) * t;
        }

        /// <summary>The curves, once, for the log.</summary>
        public static string Describe()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "Mixed's sky, its heaps' share following the cover: " + (FairHeapSky * 100f).ToString("F0", c) + "% of the sky up to " +
                   (SkyFrom * 100f).ToString("F0", c) + "% cover, all of it at " + (HeapsTo * 100f).ToString("F0", c) +
                   "%; puffs go from " + (PuffsFrom * 100f).ToString("F0", c) + "%; the Cumulus style from " + (PuffsTo * 100f).ToString("F0", c) + "%";
        }

        /// <summary>Where it is at this cover, for the log.</summary>
        public static string DescribeAt(float cover)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "heaps in " + (HeapSky(cover) * 100f).ToString("F0", c) + "% of the sky, puffs gone " + (PuffFade(cover) * 100f).ToString("F0", c) + "%";
        }

        // As the shader's MixThresholdAt.
        private static float Between(float a, float b, float c, float t)
        {
            return t < 0.5f ? a + (b - a) * t * 2f : b + (c - b) * (t * 2f - 1f);
        }

        private static float SmoothStep(float a, float b, float x)
        {
            float t = Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }
}
