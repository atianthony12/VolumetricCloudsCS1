using System;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// THE SKY LAYER's pictures (1.5.0, <see cref="SkyLayer"/>): which picture each kind shows, and how much
    /// of it. Pure: tools/test-skylayer.ps1 compiles this file.
    /// </summary>
    /// <remarks>
    /// The author picked several photographed skies per kind (2026-10-12: cirrus C6, C2, C3; sheep clouds
    /// S1, S7; far clouds F2, F6; one sky of several layers, M2).
    /// <para>ONE PICTURE AT A TIME (2026-11-25, docs/DISTANT-CLOUDS-PLAN.md Phase 1; the author: "morphing them I
    /// think is what looked bad ... one cubemap at a time would be better"). Until then a kind's amount walked
    /// through its pictures (0-33% the first, 33-67% the second...), so moving a slider swapped one photograph
    /// for another, and every value in between laid two over each other. Now each LIFE of a travelling copy
    /// shows one picture per kind (<see cref="PictureFor"/>, by the city and the life), and the amount is a
    /// COVER CUT of that picture (<see cref="SolveCut"/>): the thin parts go first, the cores last, the same
    /// clouds at every value; 100% = the picture as cut. The amount is a share of the picture's own cloud
    /// (<see cref="Histogram"/>: its mean opacity 0-45 degrees up, where a city's camera sees distant
    /// clouds), so two lives of one kind at one value can differ (a thin sky, a full one).</para>
    /// <para>The copies no longer overlap half a life (they averaged into a haze, every frame): they take
    /// turns (<see cref="Presence"/>), and a swap fades one out by raising its cut (thin parts evaporate first)
    /// while the next condenses, cores first. The old rows (SkyCirrus...) meant the walk: their values are
    /// carried over by <see cref="ConvertOld"/>.</para>
    /// </remarks>
    public static class SkyLayerMix
    {
        public const int Kinds = 4;
        public const int Cirrus = 0;
        public const int Sheep = 1;
        public const int Far = 2;
        public const int Layers = 3;

        /// <summary>
        /// The pictures, by their embedded names (Resources/SkyLayer/&lt;name&gt;.jpg, made by
        /// tools/make-skylayer.ps1), in the order they are packed: picture p goes into texture p / 4,
        /// channel p % 4 (r, g, b, a).
        /// </summary>
        public static readonly string[] Pictures =
        {
            "cirrus-1", "cirrus-2", "cirrus-3", "layers-1",
            "sheep-1", "sheep-2", "far-1", "far-2",
        };

        /// <summary>Each kind's pictures (indices into <see cref="Pictures"/>), thinnest first.</summary>
        private static readonly int[][] KindPictures =
        {
            new[] { 0, 1, 2 },
            new[] { 4, 5 },
            new[] { 6, 7 },
            new[] { 3 },
        };

        /// <summary>What the log calls each kind.</summary>
        public static readonly string[] KindNames = { "cirrus", "sheep clouds", "far clouds", "cloud layers" };

        /// <summary>The pictures a kind may show, thinnest first (indices into <see cref="Pictures"/>).</summary>
        public static int[] PicturesOf(int kind)
        {
            return KindPictures[kind];
        }

        /// <summary>Which kind a picture belongs to, or -1.</summary>
        public static int KindOf(int picture)
        {
            for (int k = 0; k < Kinds; k++)
                if (Array.IndexOf(KindPictures[k], picture) >= 0)
                    return k;
            return -1;
        }

        // ---- One picture at a time (2026-11-25) ------------------------------------------------------------

        /// <summary>
        /// The cut's soft edge, in the picture's own units (0..1 of cloud): what is within this of the cut
        /// fades in. The shader's SKY_CUT_SOFT (SkyLayer.shader): change both.
        /// </summary>
        public const float CutSoft = 0.15f;

        /// <summary>
        /// The cut is made on the picture read this much coarser (a 4x4 box: the shader reads the mip two above
        /// the view's own, SKY_CUT_BLUR 4): a cut made on the sharp texels turned the cores' edges binary and
        /// pixel-stepped at low amounts (Phase 0). The cloud drawn is still the sharp texels, times the cut.
        /// </summary>
        public const int CutBlur = 4;

        /// <summary>Bins of <see cref="Histogram"/>.</summary>
        public const int Bins = 256;

        /// <summary>How high the band the amounts are measured in reaches (degrees): what a city's camera sees.</summary>
        public const float BandTop = 45f;

        /// <summary>
        /// Each picture's mean opacity in the band (<see cref="Histogram"/>), measured from the embedded
        /// JPEGs by tools/test-skylayer.ps1, which checks these against them. Needed before any picture is
        /// decoded: <see cref="ConvertOld"/> runs when the settings are read.
        /// </summary>
        public static readonly float[] PictureMean =
        {
            0.3288f, 0.1285f, 0.4226f, 0.3493f,
            0.2117f, 0.4179f, 0.3008f, 0.5190f,
        };

        /// <summary>
        /// A picture's histogram for the cut: its texels (<paramref name="grey"/> 0..1, row 0 the horizon,
        /// <paramref name="height"/> rows for 0-90 degrees) in 4x4 boxes (<see cref="CutBlur"/>), each box
        /// weighted by its share of the sky (cos of its elevation) up to <see cref="BandTop"/>; normalised to
        /// a sum of 1. <see cref="MeanAt"/> reads it.
        /// </summary>
        public static void Histogram(float[] grey, int width, int height, float[] hist)
        {
            Array.Clear(hist, 0, hist.Length);
            int b = CutBlur;
            int top = Math.Min(height, (int)Math.Ceiling(height * BandTop / 90f));
            double total = 0;
            var sums = new double[Bins];
            for (int y = 0; y + b <= top; y += b)
            {
                double weight = Math.Cos((y + 0.5 * b) / height * Math.PI * 0.5);
                for (int x = 0; x + b <= width; x += b)
                {
                    float s = 0f;
                    for (int dy = 0; dy < b; dy++)
                    {
                        int row = (y + dy) * width + x;
                        for (int dx = 0; dx < b; dx++)
                            s += grey[row + dx];
                    }
                    float v = Clamp01(s / (b * b));
                    int bin = Math.Min(Bins - 1, (int)(v * Bins));
                    sums[bin] += weight;
                    total += weight;
                }
            }
            if (total <= 0)
                return;
            for (int i = 0; i < Bins; i++)
                hist[i] = (float)(sums[i] / total);
        }

        /// <summary>
        /// How much cloud a picture shows with the cut at <paramref name="cut"/>: its mean opacity in the band,
        /// each box's cloud times saturate((box - cut) / <see cref="CutSoft"/>). <paramref name="cut"/> at
        /// -<see cref="CutSoft"/> or lower = the whole picture; 1 = nothing.
        /// </summary>
        public static float MeanAt(float[] hist, float cut)
        {
            double m = 0;
            for (int i = 0; i < Bins; i++)
            {
                if (hist[i] == 0f)
                    continue;
                float v = (i + 0.5f) / Bins;
                m += hist[i] * v * Clamp01((v - cut) / CutSoft);
            }
            return (float)m;
        }

        /// <summary>
        /// The cut that leaves <paramref name="share"/> (0..1) of a picture's own cloud: 1 = the whole picture
        /// (-<see cref="CutSoft"/>), 0 = nothing (1). Bisected (the mean falls steadily as the cut rises).
        /// </summary>
        public static float SolveCut(float[] hist, float share)
        {
            if (share >= 1f)
                return -CutSoft;
            if (share <= 0f)
                return 1f;
            float target = share * MeanAt(hist, -CutSoft);
            float lo = -CutSoft, hi = 1f;
            for (int i = 0; i < 24; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (MeanAt(hist, mid) > target)
                    lo = mid;
                else
                    hi = mid;
            }
            return 0.5f * (lo + hi);
        }

        /// <summary>
        /// The pictures a life may pick, per kind (a subset of <see cref="PicturesOf"/>). Phase 1's probe
        /// (2026-11-25, docs/distant-clouds-2026-11-25/phase1) cut every picture down to 15%: cirrus-1 shows the
        /// maker's filled sun as a dark round hole and a flat bottom edge, cirrus-3 a pale wedge (filled sun /
        /// opened rays). Both stay OUT of the picks until the pictures are cut again (plan Phase 2); at 100% they
        /// looked the same as before, which is why nobody saw it.
        /// </summary>
        private static readonly int[][] KindPicks =
        {
            new[] { 1 },
            new[] { 4, 5 },
            new[] { 6, 7 },
            new[] { 3 },
        };

        /// <summary>The pictures a life of <paramref name="kind"/> may pick.</summary>
        public static int[] PicksOf(int kind)
        {
            return KindPicks[kind];
        }

        /// <summary>
        /// The picture <paramref name="kind"/> shows in life <paramref name="life"/> of the travelling copies,
        /// for a city of <paramref name="seed"/>: one of its picks (<see cref="PicksOf"/>), by a hash, so lives
        /// in a row differ and cities do not share a sequence. Never by the amount.
        /// </summary>
        public static int PictureFor(int kind, long life, int seed)
        {
            int[] pictures = KindPicks[kind];
            if (pictures.Length == 1)
                return pictures[0];
            uint hash = unchecked((uint)seed * 2654435761u ^ (uint)life * 2246822519u ^ (uint)(life >> 32) * 3266489917u ^ (uint)kind * 668265263u);
            hash ^= hash >> 15;
            hash = unchecked(hash * 2246822519u);
            hash ^= hash >> 13;
            return pictures[(int)(hash % (uint)pictures.Length)];
        }

        /// <summary>
        /// How far a life's copy is carried, from first showing to gone, in lives: (1 + <see cref="SwapShare"/>).
        /// SkyLayer starts it half that much UPWIND of where it would be (carried -half .. +half): a copy carried
        /// along the flat layer is stretched into streaks fanning from the horizon from ~4 km on (Phase 1 probe,
        /// carry sheet), so the stretch is shared out each side of the picture as photographed.
        /// </summary>
        public static float CarrySpan
        {
            get { return 1f + Clamp01(SwapShare); }
        }

        /// <summary>
        /// The share of a life spent on a swap: in the last <see cref="SwapShare"/> of life n the next one
        /// comes in while n goes. Lives take turns in the two copies (life n in copy n mod 2).
        /// </summary>
        public static float SwapShare = 0.15f;

        /// <summary>
        /// How much the going and the coming life overlap in a swap: 1 = both over the whole swap; 0 = the old
        /// one gone in the first half, the new one in over the second (one at a time, a brief thinner sky in
        /// the middle). A static field so the probe can show both (his call, docs/DISTANT-CLOUDS-PLAN.md).
        /// 1 (2026-11-25): the probe's swap at 0 emptied the sky for a moment (mean cloud 0.30 -> 0.003), at 0.5
        /// thinned it by half; at 1 the cloud held (0.26-0.30) and each picture keeps its own shapes (both cut,
        /// never averaged).
        /// </summary>
        public static float SwapOverlap = 1f;

        /// <summary>
        /// Copy <paramref name="slot"/> (0, 1) at <paramref name="age"/> (lives travelled): the life it holds and
        /// how present it is (0..1: the share of its picture's cloud it shows; a swap's fade). Presence 0 =
        /// nothing to draw (the copy's carry starts when it first shows: SkyLayer holds its anchor till then).
        /// </summary>
        public static void Presence(double age, int slot, out long life, out float presence)
        {
            long n = (long)Math.Floor(age);
            float f = (float)(age - n);
            float x = Clamp01(SwapShare);
            float window = x * (1f + Clamp01(SwapOverlap)) * 0.5f;   // each fade's length, in lives
            if (((n % 2) + 2) % 2 == slot)
            {
                // life n: whole until the swap starts, then out over the first fade
                life = n;
                float swapStart = 1f - x;
                if (f < swapStart)
                    presence = 1f;
                else
                    presence = window > 0f ? 1f - Smooth((f - swapStart) / window) : 0f;
            }
            else
            {
                // life n + 1: in over the last fade of life n
                life = n + 1;
                presence = window > 0f ? Smooth((f - (1f - window)) / window) : 0f;
            }
        }

        /// <summary>
        /// A value of the OLD rows (SkyCirrus, SkySheepClouds, SkyFarClouds, SkyCloudLayers, 1.5.0 to 1.6.1:
        /// the amount walked through the kind's pictures) as the new amount with the same mean cloud in the band,
        /// on average over the pictures a life may pick (<see cref="PicksOf"/>). 0..1 in, 0..1 out.
        /// </summary>
        public static float ConvertOld(int kind, float oldAmount)
        {
            // The old walk's mean cloud (Weights, without its array: Dynamic calls this every frame).
            int[] walk = KindPictures[kind];
            int n = walk.Length;
            float s = Clamp01(oldAmount) * n;
            double old;
            if (s <= 1f)
            {
                old = s * PictureMean[walk[0]];
            }
            else
            {
                float q = s - 1f;
                int a = (int)Math.Floor(q);
                if (a >= n - 1)
                    old = PictureMean[walk[n - 1]];
                else
                    old = (1f - (q - a)) * PictureMean[walk[a]] + (q - a) * PictureMean[walk[a + 1]];
            }
            double mean = 0;
            int[] pictures = KindPicks[kind];
            for (int i = 0; i < pictures.Length; i++)
                mean += PictureMean[pictures[i]];
            mean /= pictures.Length;
            return mean > 0 ? Clamp01((float)(old / mean)) : 0f;
        }

        /// <summary>
        /// THE OLD WALK (1.5.0 to 1.6.1), kept for <see cref="ConvertOld"/> only: how much of each picture
        /// (<paramref name="weights"/>, one per <see cref="Pictures"/> entry) <paramref name="kind"/> showed at
        /// <paramref name="amount"/> 0..1. Two pictures at most, their weights adding up to the share of the
        /// first that has faded in.
        /// </summary>
        public static void Weights(int kind, float amount, float[] weights)
        {
            Array.Clear(weights, 0, weights.Length);
            int[] pictures = KindPictures[kind];
            int n = pictures.Length;
            float s = Clamp01(amount) * n;
            if (s <= 1f)
            {
                weights[pictures[0]] = s;
                return;
            }

            float q = s - 1f;
            int a = (int)Math.Floor(q);
            if (a >= n - 1)
            {
                weights[pictures[n - 1]] = 1f;
                return;
            }

            float f = q - a;
            weights[pictures[a]] = 1f - f;
            weights[pictures[a + 1]] = f;
        }

        // "Dynamic distant clouds" (1.5.0). The author, 2026-10-14: tied to the weather, "but I still need
        // randomness ... for the most part it should be without distant clouds, so the chances for them
        // should be less than 50%". So two parts:
        //   a SPELL -- two slow waves of the city's own deciding whether there are distant clouds at all;
        //     they are there less than half the time (test-skylayer measures it), fading in and out;
        //   the KINDS -- the cloud cover picks them: cirrus and sheep clouds under a clear sky, cumulus on
        //     the horizon in a partly cloudy one, layered skies towards overcast; each kind's own slow wave
        //     makes it more or less of that, so two clear days don't look alike.
        // Periods in lives of the travelling copies (SkyLayer.Lifetime); none shares a measure with another,
        // so nothing comes round again, and all are several lives long: slower than the pictures' fades.
        private const double SpellSlow = 13.1;
        private const double SpellFast = 5.3;

        /// <summary>The spell's waves (0..1) must be above this for distant clouds to show at all.</summary>
        private const float SpellStart = 0.58f;

        /// <summary>... and are whole this far above it: between, they fade in (over a life or so).</summary>
        private const float SpellFade = 0.25f;

        private static readonly float[] DynamicMost = { 0.75f, 0.6f, 0.6f, 0.55f };
        private static readonly double[] DynamicSlow = { 7.3, 9.1, 8.2, 11.7 };

        /// <summary>The most the four may add up to, so the mixed sky never turns into one grey sheet.</summary>
        private const float DynamicTotal = 1.3f;

        /// <summary>
        /// "Dynamic distant clouds" (1.5.0): the four kinds' amounts (<paramref name="amounts"/>, 0..1 each)
        /// at <paramref name="lives"/> -- how far the sky has travelled, in lives of its copies -- under a
        /// cloud cover of <paramref name="cover"/> (0..1), for a city of <paramref name="seed"/>. All 0
        /// outside a spell (most of the time); never adding up to more than <see cref="DynamicTotal"/>.
        /// </summary>
        public static void Dynamic(double lives, int seed, float cover, float[] amounts)
        {
            double spellSlow, spellFast;
            Phases(seed, Kinds, out spellSlow, out spellFast);
            double spellWave = 0.6 * Math.Sin(2.0 * Math.PI * (lives / SpellSlow + spellSlow))
                             + 0.4 * Math.Sin(2.0 * Math.PI * (lives / SpellFast + spellFast));
            float spell = Smooth(((float)(0.5 + 0.5 * spellWave) - SpellStart) / SpellFade);

            cover = Clamp01(cover);
            float total = 0f;
            for (int k = 0; k < Kinds; k++)
            {
                double phase, unused;
                Phases(seed, k, out phase, out unused);
                float wave = (float)(0.5 + 0.5 * Math.Sin(2.0 * Math.PI * (lives / DynamicSlow[k] + phase)));
                amounts[k] = spell * Fit(k, cover) * (0.35f + 0.65f * wave) * DynamicMost[k];
                total += amounts[k];
            }

            if (total > DynamicTotal)
            {
                for (int k = 0; k < Kinds; k++)
                    amounts[k] *= DynamicTotal / total;
            }
        }

        /// <summary>How well a kind suits a cloud cover, 0..1.</summary>
        private static float Fit(int kind, float cover)
        {
            switch (kind)
            {
                case Cirrus: return 1f - Smooth((cover - 0.35f) / 0.35f);                        // clear, gone by 70%
                case Sheep: return Smooth(cover / 0.2f) * (1f - Smooth((cover - 0.45f) / 0.25f)); // fair weather
                case Far: return Smooth((cover - 0.15f) / 0.25f) * (1f - Smooth((cover - 0.75f) / 0.2f)); // partly cloudy
                default: return Smooth((cover - 0.45f) / 0.3f);                                   // towards overcast
            }
        }

        /// <summary>Two phases, 0..1, for a city and a slot (a kind, or <see cref="Kinds"/> for the spell).</summary>
        private static void Phases(int seed, int slot, out double a, out double b)
        {
            uint hash = unchecked((uint)seed * 2654435761u + (uint)slot * 40503u);
            hash ^= hash >> 15;
            hash = unchecked(hash * 2246822519u);
            hash ^= hash >> 13;
            a = (hash & 0xFFFF) / 65536.0;
            b = (hash >> 16) / 65536.0;
        }

        private static float Smooth(float t)
        {
            t = Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }
    }
}
