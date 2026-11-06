using System;
using System.Globalization;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// THE MIXED STYLE (1.5.0; the author, 2026-10-05: "mixing the cloud style together, so at low
    /// overcast a mix of cumulus and classic clouds, and at higher intensity focus on classic layer
    /// ... the proportions can be randomized, but ... at full overcast the entire sky is covered").
    /// The Soft style's PUFFS (<see cref="CloudSoft"/>), with the CLASSIC LAYER coming in as the
    /// intensity rises and taking over: at 100% the layer is Classic's own 100%.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>THE PUFFS: Soft's, cut as at a share of the intensity (<see cref="PuffIntensity"/>: as
    /// cloudy a sky as Mixed had before).
    /// Until 2026-10-17 they were separate clouds -- one per ~1 km cell, three bubbles carved by the
    /// Cumulus noise, baked into a map per step of the intensity -- liked at the high end, "ugly in low
    /// intensity": similar-sized cut-outs with hard edges. The author then: Mixed's separate clouds go,
    /// and with them its Big clouds, Cloud shapes and Texture variety rows.</item>
    /// <item>THE LAYER: Classic's, its thickness following the map as under Cumulus, cut at
    /// <see cref="LayerCover"/>: none at low intensity, Classic's own cover from ~85% up.</item>
    /// <item>THE TYPE (random proportions; HZD's cloud-type channel): a slow pattern from the city's
    /// seed (<see cref="TypeAt"/>, periodic with the weather map, baked once per seed into the type map,
    /// CloudMixMap) tips each stretch of sky towards the layer (it comes in earlier, the puffs are cut
    /// as at <see cref="PuffsInLayerArea"/> x the intensity) or towards puffs (the layer later, the puffs
    /// as at <see cref="PuffsInCumulusArea"/> x); it drifts with the clouds and fades out by 100%.</item>
    /// <item>THE HEAPS (2026-11-06, the author: "a combination of all four"): a second pattern
    /// (<see cref="HeapAt"/>, made even by rank, so a share of it is that share of the sky in every city)
    /// gives <see cref="HeapSky"/> of the sky to the Cumulus style's heaps. Such a stretch leans to layer as
    /// well (<see cref="Lean"/>: the type times 1 - lean), so the sky's three corners are the three styles:
    /// the layer alone, puffs, heaps with the layer. They go with the type's strength: 100% is what it was.
    /// Each stretch has its own light: the heaps' level is 1, the sky's the puffs' (<see cref="LightBoost"/>).
    /// </item>
    /// </list>
    /// Rain, lightning's cloud test and the rainbow's use the SMALLEST layer cover
    /// (<see cref="LayerCoverInCumulusArea"/>), so rain always falls from drawn cloud (invariant 2).
    ///
    /// Pure: no engine calls (tools/test-mix.ps1). The type's numbers and <see cref="Hash"/> are part
    /// of what a city's seed reproduces (invariant 14): <see cref="Generator"/>.
    /// </remarks>
    public static class CloudMix
    {
        /// <summary>
        /// What these numbers, the hash and the type map are. Bump it with any change to what a seed draws.
        /// 2 (2026-10-17): the separate clouds became Soft's puffs. 3 (2026-11-06): the heaps' pattern; the
        /// type's pattern is the same for a seed.
        /// </summary>
        public const int Generator = 3;

        // ---- the heaps ---------------------------------------------------------------------------

        /// <summary>The share of Mixed's sky that leans to the Cumulus style's heaps, at every cover.</summary>
        public const float HeapSky = 1f / 3f;

        /// <summary>How wide a stretch of heaps' edge is, as a share of the pattern's (so of the sky).</summary>
        public const float LeanWidth = 0.12f;

        /// <summary>
        /// The heaps' coverage line's edge (CloudStyle.Line) where the sky does not lean to them: under the
        /// Cumulus table's lowest, so where it does is always the higher end.
        /// </summary>
        public const float NoHeapEdge = -0.4f;

        /// <summary>The value of the heaps' pattern (even, 0..1) a stretch starts to lean to heaps at, for this share of the sky.</summary>
        public static float LeanStart(float share)
        {
            return (1f - Clamp01(share)) * (1f + LeanWidth) - LeanWidth;
        }

        /// <summary>How far a place leans to heaps, 0..1, where the pattern reads <paramref name="k"/>. All of the sky at a share of 1, none at 0.</summary>
        public static float Lean(float k, float share)
        {
            return Clamp01((k - LeanStart(share)) / LeanWidth);
        }

        /// <summary>
        /// What the clouds' light is times, less 1, where the sky leans all the way to heaps, for the
        /// light level the rest of it has: the heaps' own is 1. Times the type's strength: at 100% one deck, one light.
        /// </summary>
        public static float LightBoost(float level, float strength)
        {
            return (1f / Math.Max(level, 1e-3f) - 1f) * Clamp01(strength);
        }

        // ---- the layer and the type ------------------------------------------------------------

        /// <summary>The layer's cover at this intensity: none at low intensity, Classic's own from ~85% up.</summary>
        public static float LayerCover(float intensity)
        {
            return intensity * LayerShare(intensity);
        }

        /// <summary>
        /// The layer's cover over the intensity (<see cref="LayerCover"/> = intensity x this), 0..1, where
        /// the type leans neither way: the puffs' light follows it (CloudSoft.LightScaleAt).
        /// </summary>
        public static float LayerShare(float intensity)
        {
            return SmoothStep(0.25f, 0.85f, intensity);
        }

        /// <summary>
        /// How much the layer carries Mixed's sky for its light, 0..1 (CloudSoft.LightScaleAt: the puffs'
        /// x0.5 to the layer's x0.8; times the "Classic layer" row by the caller): half of it as its layer
        /// comes in (<see cref="LightFrom"/> to 95%), the rest at the top (90% to 100%), as Soft's. Measured
        /// against Cumulus's brightness at the same Cloud brightness (render-detail.ps1 -Set mix-measure):
        /// x0.5 at 11%, x0.56 at 47%, x0.62 at 80%, x0.69 at 95% (this gives x0.5 / 0.56 / 0.64 / 0.73).
        /// Before its puffs Mixed had the Cumulus light at x1; Soft's puffs lit so were ~10% too bright.
        /// </summary>
        public static float LightShare(float intensity)
        {
            return 0.5f * SmoothStep(LightFrom, 0.95f, intensity) + 0.5f * SmoothStep(0.9f, 1f, intensity);
        }

        /// <summary>The intensity Mixed's light starts to rise from the puffs' level at.</summary>
        public const float LightFrom = 0.1f;

        /// <summary>The same in a stretch of sky the type tips towards layer: it comes in earlier. Never less than the other two.</summary>
        public static float LayerCoverInLayerArea(float intensity)
        {
            return intensity * SmoothStep(0.05f, 0.7f, intensity);
        }

        /// <summary>The same where the type tips towards the puffs: later. Never more than the other two.</summary>
        public static float LayerCoverInCumulusArea(float intensity)
        {
            return intensity * SmoothStep(0.45f, 0.95f, intensity);
        }

        /// <summary>How strongly the type tips the balance: fully up to 90%, not at all at 100%.</summary>
        public static float TypeStrength(float intensity)
        {
            return 1f - SmoothStep(0.9f, 1f, intensity);
        }

        /// <summary>
        /// The puffs are cut as at the intensity times this in a layer area, and in a puff area (the
        /// separate clouds' share was multiplied by the same, 2026-10-05): fewer puffs where the sky leans
        /// to layer, more where it leans to puffs.
        /// </summary>
        public const float PuffsInLayerArea = 0.4f, PuffsInCumulusArea = 1.6f;

        /// <summary>
        /// The puffs are cut as Soft's at the intensity times this (where the type leans neither way), one
        /// per 5% of the intensity (0%, 5% .. 100%), linear between: solved offline (render-detail.ps1
        /// -Set mix-grid on the seeds 12345/67890, 23456/78901, 34567/89012) so that Mixed's whole sky --
        /// puffs, layer and type -- is, over the three cities, as cloudy as it was with its separate clouds
        /// (2026-10-12 to 10-17, -Set mix-target; its high end is what the author likes). So Mixed's sky
        /// stays lighter than Soft's at low intensity (47%: about half). From 90% the puffs are at Soft's
        /// cap where the type leans neither way (the old amount there is just out of reach, under a point
        /// short; at 100% two points over, as Soft). The cities' own answers differ a lot (at 50%: 0.56,
        /// 0.93, 0.25): Soft's amount follows each map's shape more than the separate clouds, laid out on
        /// cells, did -- so the factor matching their MEAN amount is taken, not their mean factor.
        /// </summary>
        public static readonly float[] PuffTable =
        {
            0.70f, 0.70f, 0.43f, 0.37f, 0.35f, 0.34f, 0.34f, 0.38f, 0.39f, 0.42f,
            0.44f, 0.45f, 0.45f, 0.46f, 0.46f, 0.48f, 0.51f, 0.58f, 1.00f, 1.00f,
            1.00f,
        };

        /// <summary><see cref="PuffTable"/> at an intensity.</summary>
        public static float PuffFactor(float intensity)
        {
            float x = Clamp01(intensity) * (PuffTable.Length - 1);
            int i = Math.Min((int)x, PuffTable.Length - 2);
            float f = x - i;
            return PuffTable[i] + (PuffTable[i + 1] - PuffTable[i]) * f;
        }

        /// <summary>
        /// The intensity Soft's puffs are cut as at (CloudSoft.PuffCoverage) under Mixed: in a layer area
        /// (<paramref name="area"/> 0), between (1) or in a puff area (2).
        /// </summary>
        public static float PuffIntensity(float intensity, int area)
        {
            float k = area == 0 ? PuffsInLayerArea : area == 2 ? PuffsInCumulusArea : 1f;
            return Clamp01(Clamp01(intensity) * PuffFactor(intensity) * k);
        }

        /// <summary>The type pattern: value noise at these periods per weather repeat, with these weights.</summary>
        public const int TypePeriodA = 2, TypePeriodB = 3;
        public const float TypeWeightA = 0.65f, TypeWeightB = 0.35f;

        /// <summary>How hard the pattern is pushed towards its two ends (0 layer, 1 puffs).</summary>
        public const float TypeContrast = 2.5f;

        /// <summary>
        /// The type at (u, v) of the weather map's repeat (0..1, periodic), before the intensity's
        /// <see cref="TypeStrength"/>: 0 tips towards layer, 1 towards puffs.
        /// </summary>
        public static float TypeAt(float u, float v, float seedX, float seedZ)
        {
            return TypeAt(u, v, seedX, seedZ, 1);
        }

        /// <summary>
        /// The same over a map <paramref name="scale"/> pattern sizes wide (Larger weather pattern):
        /// its periods that many times more, so each stretch of sky keeps its size in metres.
        /// </summary>
        public static float TypeAt(float u, float v, float seedX, float seedZ, int scale)
        {
            int periodA = TypePeriodA * scale, periodB = TypePeriodB * scale;
            float a = ValueNoise(u * periodA, v * periodA, periodA, 40f, seedX, seedZ);
            float b = ValueNoise(u * periodB, v * periodB, periodB, 41f, seedX, seedZ);
            float t = Clamp01(0.5f + (a * TypeWeightA + b * TypeWeightB - 0.5f) * TypeContrast);
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// The heaps' pattern at (u, v) of the weather map's repeat, before it is made even: as the type's,
        /// from numbers of its own, so where the heaps are says nothing of where the layer is.
        /// </summary>
        public static float HeapAt(float u, float v, float seedX, float seedZ, int scale)
        {
            int periodA = TypePeriodA * scale, periodB = TypePeriodB * scale;
            float a = ValueNoise(u * periodA, v * periodA, periodA, 42f, seedX, seedZ);
            float b = ValueNoise(u * periodB, v * periodB, periodB, 43f, seedX, seedZ);
            return a * TypeWeightA + b * TypeWeightB;
        }

        /// <summary>
        /// The heaps' pattern as the map's red holds it: each texel's RANK in the map, 0..255, so a share of
        /// the values is that share of the sky, in every city (one repeat holds only a few lattice points,
        /// and its plain values lean a different way each time).
        /// </summary>
        public static byte[] BakeHeaps(int size, float seedX, float seedZ, int scale)
        {
            float[] values = new float[size * size];
            if (scale > 1)
                ParallelRows.Run(size, "VolumetricClouds mixed heaps", (first, end) => BakeHeapRows(values, size, seedX, seedZ, scale, first, end));
            else
                BakeHeapRows(values, size, seedX, seedZ, scale, 0, size);

            const int Bins = 4096;
            int[] count = new int[Bins];
            for (int i = 0; i < values.Length; i++)
                count[Bin(values[i], Bins)]++;

            // Each bin's rank: everything under it, and half of itself.
            byte[] rank = new byte[Bins];
            long under = 0;
            for (int b = 0; b < Bins; b++)
            {
                rank[b] = ToByte((float)((under + count[b] * 0.5) / values.Length));
                under += count[b];
            }

            byte[] bytes = new byte[values.Length];
            for (int i = 0; i < values.Length; i++)
                bytes[i] = rank[Bin(values[i], Bins)];
            return bytes;
        }

        private static int Bin(float value, int bins)
        {
            return Math.Max(0, Math.Min(bins - 1, (int)(value * bins)));
        }

        private static void BakeHeapRows(float[] values, int size, float seedX, float seedZ, int scale, int first, int end)
        {
            for (int j = first; j < end; j++)
                for (int i = 0; i < size; i++)
                    values[j * size + i] = HeapAt((i + 0.5f) / size, (j + 0.5f) / size, seedX, seedZ, scale);
        }

        // ---- the map ---------------------------------------------------------------------------

        /// <summary>Texels per side of the type map, over one weather repeat (26 m at 13.5 km).</summary>
        public const int MapSize = 512;

        /// <summary>
        /// The type map over a weather map <paramref name="scale"/> pattern sizes wide (Larger
        /// weather pattern): as many times the texels, each still 26 m at 13.5 km.
        /// </summary>
        public static int MapSizeFor(int scale)
        {
            return MapSize * scale;
        }

        /// <summary>
        /// The type pattern as the type map's alpha holds it: <paramref name="size"/>^2 bytes, texel
        /// centres at (i + 0.5) / size like the GPU's. It only changes with the seed.
        /// </summary>
        public static byte[] BakeType(int size, float seedX, float seedZ)
        {
            return BakeType(size, seedX, seedZ, 1);
        }

        /// <summary>The same over a map <paramref name="scale"/> pattern sizes wide (<see cref="TypeAt(float, float, float, float, int)"/>).</summary>
        public static byte[] BakeType(int size, float seedX, float seedZ, int scale)
        {
            byte[] bytes = new byte[size * size];

            // A larger map's is 16x the work (seconds on one core): its rows split over the cores.
            if (scale > 1)
                ParallelRows.Run(size, "VolumetricClouds mixed type", (first, end) => BakeTypeRows(bytes, size, seedX, seedZ, scale, first, end));
            else
                BakeTypeRows(bytes, size, seedX, seedZ, scale, 0, size);

            return bytes;
        }

        private static void BakeTypeRows(byte[] bytes, int size, float seedX, float seedZ, int scale, int first, int end)
        {
            for (int j = first; j < end; j++)
                for (int i = 0; i < size; i++)
                    bytes[j * size + i] = ToByte(TypeAt((i + 0.5f) / size, (j + 0.5f) / size, seedX, seedZ, scale));
        }

        // ---- the hash ---------------------------------------------------------------------------

        /// <summary>
        /// The lattice point's random number <paramref name="k"/>, 0..1: Hoskins' "hash without sine" on
        /// its whole-number coordinates plus the seed's offsets.
        /// </summary>
        public static float Hash(float cellX, float cellZ, float k, float seedX, float seedZ)
        {
            float px = Frac((cellX + seedX) * 0.1031f);
            float py = Frac((cellZ + seedZ) * 0.1031f);
            float pz = Frac(k * 0.1031f);
            float dot = px * (pz + 31.32f) + py * (py + 31.32f) + pz * (px + 31.32f);
            px += dot;
            py += dot;
            pz += dot;
            return Frac((px + py) * pz);
        }

        /// <summary>The seed's offsets into the hash (whole numbers, 17..1040 and 53..1076).</summary>
        public static void SeedOffsets(int seed, out float x, out float z)
        {
            unchecked
            {
                uint s = (uint)seed;
                x = (s & 1023u) + 17f;
                z = ((s >> 10) & 1023u) + 53f;
            }
        }

        // ---- for the log ----------------------------------------------------------------------

        /// <summary>The style's shape, once, for the log: the puffs, the layer, the type.</summary>
        public static string DescribeShape(float thickness, int scale)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            CloudSoft.Look look = CloudSoft.Default;
            return "the Soft style's puffs (up to " + CloudSoft.Tallest(look, thickness).ToString("F0", c) + " m tall, cut as Soft's at a share of the intensity: 47% -> " +
                   (CloudSoft.PuffCoverage(look, PuffIntensity(0.47f, 1)) * 100f).ToString("F0", c) + "%), the Classic layer from ~25% up, " +
                   "Classic's own cover from 85%; a type pattern tips the sky towards layer or puffs (puffs as at " +
                   PuffsInLayerArea.ToString("F1", c) + "x / " + PuffsInCumulusArea.ToString("F1", c) + "x the intensity), and " +
                   (HeapSky * 100f).ToString("F0", c) + "% of the sky leans to the Cumulus style's heaps, baked into a " +
                   MapSizeFor(scale) + "^2 map (generator " + Generator + ")";
        }

        /// <summary>The mix at this intensity, for the detailed log.</summary>
        public static string DescribeMix(float intensity)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            CloudSoft.Look look = CloudSoft.Default;
            return "puffs cut at " + (CloudSoft.PuffCoverage(look, PuffIntensity(intensity, 0)) * 100f).ToString("F0", c) + "/" +
                   (CloudSoft.PuffCoverage(look, PuffIntensity(intensity, 1)) * 100f).ToString("F0", c) + "/" +
                   (CloudSoft.PuffCoverage(look, PuffIntensity(intensity, 2)) * 100f).ToString("F0", c) + "%, layer cover " +
                   (LayerCoverInCumulusArea(intensity) * 100f).ToString("F0", c) + "/" + (LayerCover(intensity) * 100f).ToString("F0", c) + "/" +
                   (LayerCoverInLayerArea(intensity) * 100f).ToString("F0", c) + "%, type " + (TypeStrength(intensity) * 100f).ToString("F0", c) + "%";
        }

        // ---- helpers ----------------------------------------------------------------------------

        /// <summary>Smooth value noise over a lattice of <paramref name="period"/> cells, periodic, from the hash.</summary>
        private static float ValueNoise(float x, float y, int period, float k, float seedX, float seedZ)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            int xa = Wrap(x0, period), xb = Wrap(x0 + 1, period), ya = Wrap(y0, period), yb = Wrap(y0 + 1, period);
            float a = Hash(xa, ya, k, seedX, seedZ), b = Hash(xb, ya, k, seedX, seedZ);
            float c = Hash(xa, yb, k, seedX, seedZ), d = Hash(xb, yb, k, seedX, seedZ);
            float top = a + (b - a) * fx, bottom = c + (d - c) * fx;
            return top + (bottom - top) * fy;
        }

        private static float SmoothStep(float a, float b, float x)
        {
            float t = Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        private static float Clamp01(float v)
        {
            return Math.Max(0f, Math.Min(1f, v));
        }

        private static float Frac(float v)
        {
            return v - (float)Math.Floor(v);
        }

        private static int Wrap(int i, int n)
        {
            int r = i % n;
            return r < 0 ? r + n : r;
        }

        private static byte ToByte(float v)
        {
            return (byte)Math.Max(0, Math.Min(255, (int)(v * 255f + 0.5f)));
        }
    }
}
