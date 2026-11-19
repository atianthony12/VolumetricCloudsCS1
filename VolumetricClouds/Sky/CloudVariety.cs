using System;
using System.Globalization;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// CLOUD VARIETY (2026-11-19, the row "Cloud variety", CloudVariety): "same base, different tops". A slow pattern
    /// (<see cref="CloudMix.VarietyAt"/>, the mix map's GREEN, made even by rank, so a share of its values is that share
    /// of the sky in every city) says how VIGOROUS each stretch of sky is. Where it is calm the heaps are flatter and
    /// smaller, where it is vigorous they tower and spread; the bases stay on one level.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>THE HEIGHT: the heaps' coverage curve is stretched above its widest level by <see cref="Stretch"/>
    /// (<see cref="CurveHeight"/>): below that level nothing moves, so every base and underside stays where it was
    /// (<see cref="BaseHold"/> 1). Only the curve's height argument changes: the noise is read where it always was,
    /// at its own scale at every height (a tower is many billows stacked, never one billow stretched). A tower's
    /// stretch comes in with the heap's own coverage (<see cref="StretchAt"/>).</item>
    /// <item>THE FOOTPRINT: the heaps' coverage line moves with the pattern (<see cref="LineMove"/>): wider where
    /// taller, so a tower is a big heap, not a column; smaller where flatter.</item>
    /// <item>The pattern's even value is shaped into PLATEAUS here (<see cref="Kind"/>): calm, middle, towers, with
    /// transitions between, so the height is nearly the same across any one heap.</item>
    /// <item>NO PULL (the author's call after the pictures, 2026-11-19): the heaps stay where the style puts them; the
    /// row never brings a heap where it has none. A pull of heaps into a fair sky made spires or chimneys.</item>
    /// </list>
    /// Drawn by CumulusDensity's variety branch (CloudCommon.cginc, the extras' variant only); this class is its C#
    /// twin with CloudStyle.Density. Row 0 = exactly the heaps as they are. Static fields so the bench and the preview
    /// can sweep them. Pure, C# 5 (tools/test-variety.ps1 and the preview compile the source).
    /// </remarks>
    public static class CloudVariety
    {
        /// <summary>What these numbers draw for a seed (with CloudMix's variety pattern): a new number with any change.</summary>
        public const int Generator = 1;

        // ---- the plateaus, in the pattern's even value (0..1 = that share of the sky) ---------------------------

        /// <summary>Calm up to <see cref="CalmTo"/>, rising to the middle by <see cref="MiddleFrom"/>.</summary>
        public static float CalmTo = 0.32f, MiddleFrom = 0.52f;

        /// <summary>The middle up to <see cref="MiddleTo"/> (never under <see cref="MiddleFrom"/>), rising to the towers by <see cref="TowerFrom"/>.</summary>
        public static float MiddleTo = 0.64f, TowerFrom = 0.86f;

        // ---- the height ----------------------------------------------------------------------------------------

        /// <summary>The curve's stretch in a calm stretch at 100% (under 1: flatter heaps).</summary>
        public static float CalmStretch = 0.4f;

        /// <summary>The stretch in a vigorous stretch at 100%, before <see cref="TopBound"/>.</summary>
        public static float TowerStretch = 2.2f;

        /// <summary>
        /// Metres above the base the tallest tower may reach: the stretch is cut so it never goes higher (never under 1).
        /// It sets the march's slab, so its cost: benched at 4000 and 3500 (docs, cloud variety phase 2).
        /// </summary>
        public static float TopBound = 4000f;

        /// <summary>1: below the widest level the curve is never stretched (the bases stay); 0: the whole span is (= Cumulus height x the stretch).</summary>
        public static float BaseHold = 1f;

        /// <summary>
        /// A tower's stretch comes in with the heap's coverage here, from <see cref="GrowFrom"/> to <see cref="GrowTo"/>:
        /// a small heap (a lone peak of the map, low coverage) stays about as tall as it was, a full one towers, and a
        /// heap rises highest over its fullest middle. Without it the small heaps of a vigorous stretch became columns
        /// (height over twice the width in 42 of 43 small footprints, 2026-11-19). Calm flattening is never held back.
        /// </summary>
        public static float GrowFrom = 0.04f, GrowTo = 0.2f;

        // ---- the footprint -------------------------------------------------------------------------------------

        /// <summary>The coverage line's move in a calm stretch and in a vigorous one at 100% (the score's line, CloudStyle.Line).</summary>
        public static float CalmLine = -0.035f, TowerLine = 0.06f;

        /// <summary>A move everywhere at 100%, solved so the sky holds its cover.</summary>
        public static float LineTrim = 0.009f;

        /// <summary>
        /// Which plateau the pattern's even value <paramref name="v"/> is on: -1 calm, 0 middle, 1 towers, smooth
        /// between (smoothstep over each transition). As the shader's VarietyKind: the two steps added.
        /// </summary>
        public static float Kind(float v)
        {
            return SmoothStep(CalmTo, MiddleFrom, v) - 1f + SmoothStep(MiddleTo, TowerFrom, v);
        }

        /// <summary>
        /// The towers' stretch for a tallest heap of <paramref name="tallest"/> metres, <see cref="TowerStretch"/> cut by
        /// <see cref="TopBound"/>, never under 1. Pass the tallest heap the cover draws NOW in a vigorous stretch (the line
        /// at the map's top score plus <see cref="MostLineMove"/>, as CloudStyle.TypedHeapTop), not the curve's absolute
        /// tallest (coverage 0.45, which almost no heap reaches): against that the towers were cut to x1.33 for his file.
        /// </summary>
        public static float TowerStretchFor(float tallest)
        {
            if (tallest <= 0f)
                return TowerStretch;
            return Math.Max(1f, Math.Min(TowerStretch, TopBound / tallest));
        }

        /// <summary>The curve's stretch at <paramref name="v"/> with the row at <paramref name="row"/> (0..1); exactly 1 at row 0.</summary>
        public static float Stretch(float v, float row, float towerStretch)
        {
            if (row <= 0f)
                return 1f;
            float k = Kind(v);
            float r = Clamp01(row);
            return k < 0f ? 1f + k * (r * (1f - CalmStretch)) : 1f + k * (r * (towerStretch - 1f));
        }

        /// <summary>The coverage line's move at <paramref name="v"/>; exactly 0 at row 0.</summary>
        public static float LineMove(float v, float row)
        {
            if (row <= 0f)
                return 0f;
            float k = Kind(v);
            float r = Clamp01(row);
            return (k < 0f ? -k * (r * CalmLine) : k * (r * TowerLine)) + r * LineTrim;
        }

        /// <summary>
        /// The coverage curve's height argument (0 = base, 1 = the span up) at <paramref name="y"/> metres above the
        /// base, for a span of <paramref name="span"/> metres whose widest level is <paramref name="widest"/> of it
        /// (CloudStyle.ShapeNow.Time[1]), stretched by <paramref name="stretch"/>. Exactly y / span at a stretch of 1.
        /// </summary>
        public static float CurveHeight(float y, float span, float widest, float stretch)
        {
            float h = y / span;
            if (stretch == 1f)
                return h;
            float hold = widest * Clamp01(BaseHold);
            return h <= hold ? h : hold + (h - hold) / stretch;
        }

        /// <summary>Where a heap that ends <paramref name="top"/> metres up unstretched ends with the stretch (the same rule).</summary>
        public static float TopAt(float top, float span, float widest, float stretch)
        {
            float hold = widest * Clamp01(BaseHold) * span;
            return top <= hold ? top : hold + (top - hold) * stretch;
        }

        /// <summary>The stretch at a point whose heap coverage is <paramref name="coverage"/> (see <see cref="GrowFrom"/>); exactly <paramref name="stretch"/> at 1 or under.</summary>
        public static float StretchAt(float stretch, float coverage)
        {
            if (stretch <= 1f)
                return stretch;
            return 1f + (stretch - 1f) * SmoothStep(GrowFrom, GrowTo, coverage);
        }

        /// <summary>The most the stretch is anywhere at this row (the slab's top asks).</summary>
        public static float MostStretch(float row, float towerStretch)
        {
            return row <= 0f ? 1f : 1f + Clamp01(row) * (Math.Max(1f, towerStretch) - 1f);
        }

        /// <summary>The most the line moves anywhere at this row (the empty-sky skip tests against it, so it stays exact).</summary>
        public static float MostLineMove(float row)
        {
            float r = Clamp01(row);
            return row <= 0f ? 0f : Math.Max(Math.Max(r * TowerLine, r * CalmLine), 0f) + r * LineTrim;
        }

        /// <summary>For the log.</summary>
        public static string Describe(float row, float towerStretch, float slabTop)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return "on " + (row * 100f).ToString("F0", c) + "% -- calm stretches x" + Stretch(0f, row, towerStretch).ToString("F2", c) +
                   " tall, towering ones up to x" + Stretch(1f, row, towerStretch).ToString("F2", c) + " (bound " + TopBound.ToString("F0", c) +
                   " m over the base), the heaps' line " + LineMove(0f, row).ToString("+0.000;-0.000", c) + " / " +
                   LineMove(1f, row).ToString("+0.000;-0.000", c) + "; the march reaches " + slabTop.ToString("F0", c) + " m over the base";
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
