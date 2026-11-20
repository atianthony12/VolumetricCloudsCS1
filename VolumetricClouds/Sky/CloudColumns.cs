using System;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// THE DRAWN CLOUDS, COLUMN BY COLUMN (1.6.1; pure, tools\test-columns.ps1): what CloudColumnMap read back from the GPU
    /// -- per column of sky over the city and round it, how much cloud stands in it (vertical optical depth), where its drawn
    /// underside and its dense part begin and where its top is -- and the lookups lightning places its bolts by.
    /// </summary>
    /// <remarks>
    /// Why (his report 2026-11-20, measured on the CPU twin in docs/probe-2026-11-20): bolts were placed by the 2D weather
    /// map and the altitude setting, not by the clouds as drawn. The bottom of the slab is empty in most cloudy columns
    /// (the layer fades in, Natural Classic, the heaps' rounded bases), so the sideways bolts ran UNDER the drawn base,
    /// and a ground strike's channel started above its cloud's top 20-43% of the time. There is no C# twin of the drawn
    /// density (its style logic is shader-only), so the GPU marches the real SampleDensityLod straight up per texel
    /// (CloudColumnMapPass.cginc) and this reads the result.
    ///
    /// The encoding (ARGB32, linear; the shader writes it, <see cref="Decode"/> reads it):
    ///   r  optical depth straight up, t / (t + 4)                   (t 0.1 = 6/255, 3 = 109, 30 = 225)
    ///   g  where the optical depth from below first reaches 0.1, as a share of the slab (1 = never: no cloud)
    ///   b  the highest step with any cloud in it, a share of the slab
    ///   a  where it first reaches 1 (the cloud's dense part begins), a share of the slab (1 = never)
    /// No texel the shader draws is all zero (no cloud: g = 1; any cloud: r > 0), so an all-zero texel means the shader
    /// drew nothing there (a driver that refused the program): <see cref="Decode"/> refuses the map.
    ///
    /// The texels are kept as read and turned into metres only for the four a lookup reads (1.6.1, the performance pass:
    /// decoding all 147k of them into four float arrays took 3.7-5.5 ms of the main thread a read on Mono; the lookups are
    /// a few dozen per strike). Same numbers as before, to the bit: the same sums, once per byte value in a table.
    ///
    /// The clouds drift: a cloud over p when the map was drawn is over p + (drift now - drift then) now. Lookups take
    /// the drift now and shift back by the difference (in doubles: the drift grows for the city's whole life).
    /// </remarks>
    public sealed class CloudColumns
    {
        /// <summary>Optical depth the drawn underside is put at (the eye sees a cloud start about there).</summary>
        public const float UndersideDepth = 0.1f;

        /// <summary>Optical depth the dense part begins at.</summary>
        public const float DenseDepth = 1f;

        /// <summary>A column with this much cloud is real cloud a stroke can leave (not a veil); a little cloud: <see cref="TauSome"/>.</summary>
        public const float TauStrong = 3f, TauSome = 1f;

        public readonly int Size;
        public readonly float CentreX, CentreZ, Side;
        /// <summary>The slab the map was marched through (world heights).</summary>
        public readonly float Bottom, Top;
        /// <summary>The clouds' drift when it was drawn (CloudWind X + ShiftX, Z + ShiftZ).</summary>
        public readonly double DriftX, DriftZ;

        // The texels as read (row 0 = the map's -z edge), and each byte's height in world metres.
        private readonly Color32[] _texels;
        private readonly float[] _heightOf = new float[256];
        // For the log (Describe): cloudy and thick columns, and the underside's and top's bytes counted (their medians).
        private readonly int[] _underBytes = new int[256], _topBytes = new int[256];
        private int _cloudy, _strong;

        /// <summary><see cref="DecodeTau"/> of every byte.</summary>
        private static readonly float[] TauTable = MakeTauTable();

        private static float[] MakeTauTable()
        {
            var t = new float[256];
            for (int v = 0; v < 256; v++)
                t[v] = DecodeTau((byte)v);
            return t;
        }

        /// <summary>One column, heights in world metres (NaN: none there).</summary>
        public struct Column
        {
            /// <summary>Optical depth straight up.</summary>
            public float Tau;
            /// <summary>The share of the lookup's neighbours that have cloud (0..1): under 0.5 the lookup is at a cloud's edge.</summary>
            public float Cloud;
            public float Under, Dense, Top;

            public bool HasCloud
            {
                get { return !float.IsNaN(Under); }
            }
        }

        private CloudColumns(Color32[] texels, int size, float centreX, float centreZ, float side, float bottom, float top, double driftX, double driftZ)
        {
            _texels = texels;
            Size = size;
            CentreX = centreX;
            CentreZ = centreZ;
            Side = side;
            Bottom = bottom;
            Top = top;
            DriftX = driftX;
            DriftZ = driftZ;
            float slab = top - bottom;
            for (int v = 0; v < 256; v++)
                _heightOf[v] = bottom + v / 255f * slab;
        }

        /// <summary>The texel's world centre along one axis (texel i of <see cref="Size"/>).</summary>
        public float TexelX(int i)
        {
            return CentreX + ((i + 0.5f) / Size - 0.5f) * Side;
        }

        public float TexelZ(int j)
        {
            return CentreZ + ((j + 0.5f) / Size - 0.5f) * Side;
        }

        /// <summary>The optical depth a red byte stands for (the shader's t / (t + 4)).</summary>
        public static float DecodeTau(byte r)
        {
            float v = Math.Min(r, (byte)254) / 255f;
            return 4f * v / (1f - v);
        }

        /// <summary>The red byte for an optical depth (the shader's encoding; for the test).</summary>
        public static byte EncodeTau(float tau)
        {
            float v = Math.Max(0f, tau) / (Math.Max(0f, tau) + 4f);
            return (byte)Math.Min(255, (int)Math.Round(v * 255f));
        }

        /// <summary>
        /// The map from <paramref name="rgba"/> (4 bytes a texel, row 0 = the map's -z edge, as ReadPixels gives it).
        /// Null when any texel is all zero (the shader did not draw) or the size does not match.
        /// </summary>
        public static CloudColumns Decode(byte[] rgba, int size, float centreX, float centreZ, float side, float bottom, float top,
            double driftX, double driftZ)
        {
            if (rgba == null || rgba.Length < size * size * 4)
                return null;
            var texels = new Color32[size * size];
            for (int i = 0; i < texels.Length; i++)
                texels[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
            return DecodePixels(texels, size, centreX, centreZ, side, bottom, top, driftX, driftZ);
        }

        /// <summary>
        /// The same from the texels as Texture2D.GetPixels32 gives them, kept as they are (the map holds the array: the
        /// caller must not write it afterwards). Null as <see cref="Decode"/>.
        /// </summary>
        public static CloudColumns DecodePixels(Color32[] texels, int size, float centreX, float centreZ, float side, float bottom, float top,
            double driftX, double driftZ)
        {
            if (texels == null || texels.Length < size * size || top <= bottom || size < 2)
                return null;

            var map = new CloudColumns(texels, size, centreX, centreZ, side, bottom, top, driftX, driftZ);
            float least = UndersideDepth * 0.5f;
            float[] tauOf = TauTable;
            int[] unders = map._underBytes, tops = map._topBytes;
            int cloudy = 0, strong = 0;
            for (int i = 0; i < size * size; i++)
            {
                Color32 p = texels[i];
                if (p.r == 0 && p.g == 0 && p.b == 0 && p.a == 0)
                    return null;
                float tau = tauOf[p.r];
                if (p.g < 255 && tau >= least)
                {
                    cloudy++;
                    if (tau >= TauStrong)
                        strong++;
                    unders[p.g]++;
                    tops[p.b > p.g ? p.b : p.g]++;
                }
            }
            map._cloudy = cloudy;
            map._strong = strong;
            return map;
        }

        /// <summary>
        /// The column over (<paramref name="x"/>, <paramref name="z"/>) now (bilinear; the heights from the neighbours
        /// that have cloud only, so a cloud's edge does not drag its underside to the slab's top). False outside the map.
        /// </summary>
        public bool At(float x, float z, double driftXNow, double driftZNow, out Column column)
        {
            column = new Column { Tau = 0f, Cloud = 0f, Under = float.NaN, Dense = float.NaN, Top = float.NaN };

            // Where that cloud was when the map was drawn.
            float qx = (float)(x - (driftXNow - DriftX));
            float qz = (float)(z - (driftZNow - DriftZ));
            float fx = ((qx - CentreX) / Side + 0.5f) * Size - 0.5f;
            float fz = ((qz - CentreZ) / Side + 0.5f) * Size - 0.5f;
            if (float.IsNaN(fx) || float.IsNaN(fz) || fx < 0f || fz < 0f || fx > Size - 1 || fz > Size - 1)
                return false;

            int x0 = Math.Min((int)fx, Size - 2), z0 = Math.Min((int)fz, Size - 2);
            float tx = fx - x0, tz = fz - z0;
            float tau = 0f, cw = 0f, under = 0f, top = 0f, dw = 0f, dense = 0f;
            float least = UndersideDepth * 0.5f;
            for (int k = 0; k < 4; k++)
            {
                Color32 p = _texels[(z0 + (k >> 1)) * Size + x0 + (k & 1)];
                float w = ((k & 1) == 1 ? tx : 1f - tx) * ((k >> 1) == 1 ? tz : 1f - tz);
                float t = TauTable[p.r];
                tau += w * t;
                // A texel with cloud (the decode's rule): its underside, its top (the highest step, never under the
                // underside), its dense part where it has one.
                if (p.g < 255 && t >= least)
                {
                    cw += w;
                    under += w * _heightOf[p.g];
                    top += w * _heightOf[p.b > p.g ? p.b : p.g];
                    if (p.a < 255)
                    {
                        dw += w;
                        dense += w * _heightOf[p.a];
                    }
                }
            }

            column.Tau = tau;
            column.Cloud = cw;
            if (cw > 1e-4f)
            {
                column.Under = under / cw;
                column.Top = top / cw;
            }
            if (dw > 1e-4f)
                column.Dense = dense / dw;
            return true;
        }

        /// <summary>
        /// Where a stroke to the ground leaves its cloud (the channel's top): INSIDE the dense part, a little above where
        /// it begins (<paramref name="roll"/> 0..1 spreads it over <see cref="TopRise"/> m), never under the drawn
        /// underside + <see cref="TopFloor"/> nor over the cloud's top - <see cref="TopCeiling"/>. NaN without cloud.
        /// </summary>
        public static float StrikeTop(Column c, double roll)
        {
            if (!c.HasCloud)
                return float.NaN;
            float from = !float.IsNaN(c.Dense) ? c.Dense : c.Under + 0.3f * (c.Top - c.Under);
            float y = from + (float)roll * TopRise;
            float highest = Math.Max(c.Under + TopFloor, c.Top - TopCeiling);
            return Math.Min(Math.Max(y, c.Under + TopFloor), highest);
        }

        /// <summary>The channel's top spreads this many metres over the dense part's start.</summary>
        public static float TopRise = 150f;

        /// <summary>A top at least this far into the cloud, and this far under its top.</summary>
        public static float TopFloor = 40f, TopCeiling = 60f;

        /// <summary>
        /// Sheet lightning's source: anywhere in the cloud's body, <paramref name="share"/> 0..1 of the way from its
        /// underside to its top (NaN without cloud).
        /// </summary>
        public static float InCloud(Column c, float share)
        {
            if (!c.HasCloud)
                return float.NaN;
            float s = share < 0f ? 0f : share > 1f ? 1f : share;
            return c.Under + TopFloor + s * Math.Max(0f, c.Top - c.Under - TopFloor - TopCeiling);
        }

        /// <summary>Over the whole map: the share of columns with cloud and the median underside / top over the slab's bottom (for the log).</summary>
        public string Describe()
        {
            // From the bytes counted in the decode (1.6.1; it sorted two lists of every cloudy column before): the same
            // medians, a step of the slab's 1/255 being the map's own resolution in height.
            int n = Size * Size, cloudy = _cloudy, strong = _strong;
            float slab = Top - Bottom;
            return (100f * cloudy / n).ToString("F0") + "% of columns cloudy, " + (100f * strong / n).ToString("F0") + "% thick (optical depth " +
                   TauStrong.ToString("F0") + "+)" + (cloudy > 0
                       ? "; drawn underside " + (ByteAt(_underBytes, cloudy / 2) / 255f * slab).ToString("F0") + " m over the slab's bottom (median; 10% at " +
                         (ByteAt(_underBytes, cloudy / 10) / 255f * slab).ToString("F0") + "), tops " + (ByteAt(_topBytes, cloudy / 2) / 255f * slab).ToString("F0") + " m"
                       : "");
        }

        /// <summary>The byte at place <paramref name="index"/> (from 0) of the counted bytes in ascending order.</summary>
        private static int ByteAt(int[] counts, int index)
        {
            int seen = 0;
            for (int v = 0; v < counts.Length; v++)
            {
                seen += counts[v];
                if (seen > index)
                    return v;
            }
            return counts.Length - 1;
        }
    }
}
