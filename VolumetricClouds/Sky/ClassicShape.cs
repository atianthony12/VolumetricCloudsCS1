using System;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// NATURAL CLASSIC CLOUDS (2026-11-17, the row "Natural Classic clouds", ClassicShape): the Classic layer's clouds
    /// shaped as clouds rather than loaves. Before, every Classic cloud was the same slab (CloudCommon HeightShape: a
    /// rounded base, a tapering top) at its whole thickness wherever the weather map was a little over its cut, so a cloud
    /// kept that thickness out to its edge, where it ended in a hard wall. With the row on:
    /// <list type="bullet">
    /// <item>How far inside its cloud a point is (c: 0 at the cut, 1 <see cref="Span"/> x the old edge's width in) sets
    /// how tall the cloud stands there: thin at the edge, a dome over the middle, the underside rounding up towards the rim,
    /// widest at <see cref="Middle"/> of the layer (the Soft style's body). Never taller nor denser at any height than the
    /// old slab (it is the cap), so no cloud grows, no core is pushed up and the base stays where it was.</item>
    /// <item>The detail's edge (CloudDetail.Hardness: full density 0.3 of the way in) ramps in over (1 + <see cref="RimSoftness"/>
    /// x (1 - c)) times that near the rim, so a cloud thins to its edge instead of ending in a wall; its core keeps the old
    /// edge. Gone as the sky closes into a deck (<see cref="DeckFrom"/>-<see cref="DeckTo"/>): a closed deck let the sun
    /// through in patches with it.</item>
    /// </list>
    /// <see cref="Gain"/> holds the sky's amount: the cloud from straight above and its shadow on the ground stay within 0.6 and
    /// 2.4 points of the row at 0 (measured 2026-11-17 at the mod's defaults, 30-90% cover; 98%: +1.7 and -1.5). The 64^3 noise and the
    /// detail's erosion then work on it exactly as before. The row lerps from the old shape (0 = the layer as it always was,
    /// exactly). Drawn by the cloud shader's extras variant only (CloudShaders.PlusOn). Pure: tools\test-classicshape.ps1
    /// compiles this file. The shader's LayerDensity is its twin: change one, change the other.
    /// </summary>
    public static class ClassicShape
    {
        /// <summary>How far in from the edge c reaches 1, in units of the old edge (the map's _Softness): the dome's and the soft rim's width.</summary>
        public const float Span = 2f;

        /// <summary>c's gain (k = Gain / Span = 1.5 x the old edge's slope at the widest level): what holds the sky's amount (1.25 without the soft rim, 1.5 with it).</summary>
        public const float Gain = 3f;

        /// <summary>The widest level, a share of the layer's height (Soft's body: 20% up).</summary>
        public const float Middle = 0.25f;

        /// <summary>How much wider the detail's edge is right at the rim (x (1 - c) inward, 0 in the core): 3 = four times as wide.</summary>
        public const float RimSoftness = 3f;

        /// <summary>The soft rim goes over this stretch of cover (DeepLight's DeckFrom / DeckTo): none over a closed deck.</summary>
        public const float DeckFrom = 0.85f, DeckTo = 0.95f;

        /// <summary>The old shape: the slab every Classic cloud had (CloudCommon HeightShape) at height share <paramref name="h"/>.</summary>
        public static float HeightShape(float h)
        {
            return Clamp01(h * 5f) * Clamp01((1f - h) * 2.5f);
        }

        /// <summary>c: how far inside its cloud a point is, where the map is <paramref name="excess"/> over its cut and the old edge is <paramref name="softness"/> wide.</summary>
        public static float Inside(float excess, float softness)
        {
            return Clamp01(excess / (softness * Span));
        }

        /// <summary>
        /// The layer's shape at height share <paramref name="h"/> (0 base, 1 top) where the map is <paramref name="excess"/>
        /// over its cut (weather - threshold), with the old edge <paramref name="softness"/>, at the row's <paramref name="row"/>
        /// (0..1): CloudCommon LayerDensity's `shaped` before the fragments.
        /// </summary>
        public static float Shaped(float excess, float softness, float h, float row)
        {
            float coverage = Clamp01(excess / softness);
            float old = coverage * HeightShape(h);
            if (row <= 0f)
                return old;
            float r = Math.Min(row, 1f);
            return old + (Natural(excess, softness, h) - old) * r;
        }

        /// <summary>The new shape alone (the row at 100%).</summary>
        public static float Natural(float excess, float softness, float h)
        {
            float c = Inside(excess, softness);
            float off = h < Middle ? (Middle - h) / Middle : (h - Middle) / (1f - Middle);
            if (h < Middle)
                off *= off;
            return Math.Min(Clamp01(c * Gain - off), HeightShape(h));
        }

        /// <summary>The soft rim's strength at a cover (the shader's _ClassicRim, before c): <see cref="RimSoftness"/> x the row, none over a closed deck.</summary>
        public static float RimAt(float row, float cover)
        {
            float t = Clamp01((cover - DeckFrom) / Math.Max(0.001f, DeckTo - DeckFrom));
            return RimSoftness * Clamp01(row) * (1f - t * t * (3f - 2f * t));
        }

        /// <summary>What the detail's edge is times at a point: 1 + rim x (1 - c) (1 exactly with the row at 0).</summary>
        public static float EdgeWiden(float excess, float softness, float rim)
        {
            return rim > 0f ? 1f + rim * (1f - Inside(excess, softness)) : 1f;
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }
    }
}
