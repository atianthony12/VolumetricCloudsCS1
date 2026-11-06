using System;
using System.Globalization;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// THE AUTO STYLE (<see cref="CloudStyle.Auto"/>): the style follows the cover. Fair weather is the Soft
    /// style, a heavy sky the Cumulus style, and between them, as the cover rises, the sky changes from one
    /// to the other. The player can still pick any style by hand; this is one more choice on the same row.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>There is no timer and no state: everything here is a function of the cover, which the weather
    /// already moves slowly, so the hand-over takes as long as the sky takes to close up.</item>
    /// <item>It is not a cross-fade of two pictures. The heaps come in as the Cumulus style's do when its
    /// intensity rises, first where the map is highest, and the puffs leave as Soft's do when its intensity
    /// falls, first where the map is lowest: clouds form and clouds go. In between the shader draws the
    /// denser of puffs, heaps and layer, the same max the styles already use.</item>
    /// <item>The puffs go LATER than the heaps come (<see cref="PuffsFrom"/>): together, over 68-85%, the sky
    /// emptied half way through, 14% of it cloud at 70% and 10% at 75% (render-detail.ps1 -Set auto-measure,
    /// at the default rows; 20% and 13% at the author's). With these numbers the amount of cloud rises
    /// all the way from Soft's to Cumulus's, on the default rows and on his.</item>
    /// <item>Up to <see cref="HeapsFrom"/> this IS Soft and from <see cref="PuffsTo"/> it IS Cumulus: the
    /// same uniforms and the same shader branch (CloudShaderParams.ApplyStyle), nothing of the other read.
    /// tools/test-auto.ps1 holds it to that.</item>
    /// </list>
    /// Pure, and in C# 5 like CloudSoft: tools/test-auto.ps1 compiles the two sources.
    /// </remarks>
    public static class CloudAuto
    {
        /// <summary>The cover the heaps start to come in at, and the cover they are all there at.</summary>
        public const float HeapsFrom = 0.60f, HeapsTo = 0.90f;

        /// <summary>The cover the puffs start to go at, and the cover the last of them are gone at.</summary>
        public const float PuffsFrom = 0.76f, PuffsTo = 0.92f;

        /// <summary>The cover the light starts to rise from the puffs' level at, and the cover it is the heaps' at.</summary>
        public const float LightFrom = 0.72f, LightTo = 0.86f;

        /// <summary>How far the heaps have come, 0..1: also the share of the way the layer has gone to Cumulus's.</summary>
        public static float HeapShare(float cover)
        {
            return SmoothStep(HeapsFrom, HeapsTo, cover);
        }

        /// <summary>How far the puffs have gone, 0..1.</summary>
        public static float PuffFade(float cover)
        {
            return SmoothStep(PuffsFrom, PuffsTo, cover);
        }

        /// <summary>Any puffs left at this cover.</summary>
        public static bool PuffsAt(float cover)
        {
            return cover < PuffsTo;
        }

        /// <summary>Any heaps at this cover.</summary>
        public static bool HeapsAt(float cover)
        {
            return cover > HeapsFrom;
        }

        /// <summary>The intensity Soft's puffs are cut as at (CloudSoft.PuffCoverage): the cover itself up to <see cref="PuffsFrom"/>.</summary>
        public static float PuffIntensity(float cover)
        {
            return Clamp01(cover) * (1f - PuffFade(cover));
        }

        /// <summary>
        /// The intensity the heaps are cut at (CloudStyle.HeapEdge): what Cumulus would use at this cover
        /// (<paramref name="heapCoverage"/>, CloudStyle's, with the Cumulus amount row in it), times how far they have come.
        /// </summary>
        public static float HeapIntensity(float cover, float heapCoverage)
        {
            return heapCoverage * HeapShare(cover);
        }

        /// <summary>
        /// The cover the Classic layer is cut at: Soft's going to Cumulus's (the cover times the "Classic
        /// layer" row, <paramref name="amount"/>) as the heaps come.
        /// </summary>
        public static float LayerCover(CloudSoft.Look look, float cover, float amount)
        {
            float c = Clamp01(cover);
            return Mix(CloudSoft.LayerCover(look, c, amount), c * Clamp01(amount), HeapShare(c));
        }

        /// <summary>
        /// The cover the rain, lightning's cloud test and the rainbow's are cut at (invariant 2: it falls from
        /// drawn cloud): as CloudSoft.DrawnCoverage, the larger of the puffs' and the layer's, never more than
        /// the cover -- and rising to the cover itself as the heaps come, as under Cumulus. Soft's up to
        /// <see cref="HeapsFrom"/>, the cover from <see cref="HeapsTo"/>.
        /// </summary>
        public static float DrawnCoverage(CloudSoft.Look look, float cover, float amount)
        {
            float c = Clamp01(cover);
            float puffs = CloudSoft.PuffCoverage(look, PuffIntensity(c));
            float layer = LayerCover(look, c, amount);
            return Math.Min(c, Math.Max(Math.Max(puffs, layer), c * HeapShare(c)));
        }

        /// <summary>
        /// The clouds' light level (CloudStyle.CloudLightScale): Soft's up to <see cref="LightFrom"/>, 1
        /// (Cumulus's) from <see cref="LightTo"/>. One level for the whole march. Measured against Cumulus's
        /// brightness at the same Cloud brightness (render-detail.ps1 -Set auto-light on his rows, -default on
        /// the default ones, the same answer on both): x0.6 at 76%, x0.8 at 80%, x0.95 at 84%, x1 by 88%.
        /// Following the puffs' going instead left the sky 8% too dark half way.
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
            return "the Soft style up to " + (HeapsFrom * 100f).ToString("F0", c) + "% cover, the Cumulus style from " +
                   (PuffsTo * 100f).ToString("F0", c) + "%; between them heaps come in (all there at " + (HeapsTo * 100f).ToString("F0", c) +
                   "%) and puffs go (from " + (PuffsFrom * 100f).ToString("F0", c) + "%)";
        }

        /// <summary>Where the hand-over is at this cover, for the log.</summary>
        public static string DescribeAt(float cover)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "heaps " + (HeapShare(cover) * 100f).ToString("F0", c) + "%, puffs gone " + (PuffFade(cover) * 100f).ToString("F0", c) + "%";
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
