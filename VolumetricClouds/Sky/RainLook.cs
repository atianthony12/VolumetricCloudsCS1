using System;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// The numbers behind the streaks' wind and their life (2026-11-19; the author: "'Wind on rain' is way
    /// too aggressive, like the rain is becoming horizontal ... make it more dynamic/random"). Pure: no
    /// Unity types, C# 5, so tools/test-rainlook.ps1 compiles this file alone. Sky/RainDrops.cs feeds
    /// RainDrops.shader from here and from nothing else.
    /// </summary>
    /// <remarks>
    /// THE LEAN. A lean is a tangent: metres a drop goes sideways per metre it falls. Until this day it was
    /// wind / 7.5 m/s with no ceiling, x the row, x up to 2.5 in a gust: a gale at 50% already leaned the
    /// rain 50 degrees and its gusts 70. Now:
    /// - the wind alone gives <see cref="NaturalLean"/>: gentle up to <see cref="Fresh"/> (the strongest
    ///   wind the weather itself makes is about there), steep only past it -- a gale set on the Now tab or
    ///   a waterspout about -- and never past <see cref="Steepest"/>;
    /// - the row blends from the curtains' own lean (0%: the streaks as they always fell) to the wind's
    ///   (100%), and past 100% reads the wind as stronger than it is;
    /// - a gust changes it by <see cref="GustShare"/> of itself and swings it <see cref="SwingShare"/> to
    ///   the side: no more.
    /// </remarks>
    public static class RainLook
    {
        /// <summary>Tangent per m/s of wind up to <see cref="Fresh"/>: 12 m/s leans the rain 23 degrees.</summary>
        public const float CalmSlope = 0.035f;

        /// <summary>m/s. Full rain under the default rows blows about 10, the author's 12.</summary>
        public const float Fresh = 12f;

        /// <summary>Tangent per m/s past <see cref="Fresh"/>: a gale of 24 m/s reaches <see cref="Steepest"/>.</summary>
        public const float GaleSlope = 0.065f;

        /// <summary>The steepest mean lean, as a tangent: 50 degrees from upright.</summary>
        public const float Steepest = 1.2f;

        /// <summary>The steepest any streak is drawn, gust and eddy included: 60 degrees.</summary>
        public const float SteepestDrawn = 1.75f;

        /// <summary>How much of the wind a row past 100% adds: 200% reads it 1.6 times as strong.</summary>
        public const float PastFull = 0.6f;

        /// <summary>The lean the wind alone gives, as a tangent.</summary>
        public static float NaturalLean(float windSpeed)
        {
            float v = Math.Max(0f, windSpeed);
            float lean = v <= Fresh ? CalmSlope * v : CalmSlope * Fresh + GaleSlope * (v - Fresh);
            return Math.Min(Steepest, lean);
        }

        /// <summary>
        /// How much of the wind's lean the row takes (the rest stays the curtains'): 0 at 0%, 1 from 100%.
        /// </summary>
        public static float WindShare(float row)
        {
            return Math.Min(1f, Math.Max(0f, row));
        }

        /// <summary>The wind as the row reads it: itself up to 100%, stronger past it.</summary>
        public static float WindRead(float row, float windSpeed)
        {
            return Math.Max(0f, windSpeed) * (1f + PastFull * Math.Max(0f, row - 1f));
        }

        /// <summary>
        /// The mean lean's size for a row whose wind blows along the curtains' slant (the usual case; the
        /// shader gets the two as vectors, blended the same way). <paramref name="slant"/>: the curtains'
        /// lean, CloudRain.Slant's length.
        /// </summary>
        public static float Lean(float row, float slant, float windSpeed)
        {
            float share = WindShare(row);
            return slant + (NaturalLean(WindRead(row, windSpeed)) - slant) * share;
        }

        /// <summary>A gust (and a lull) changes the lean by this share of it. <paramref name="gusts"/>: 0..0.9.</summary>
        public static float GustShare(float row, float gusts)
        {
            return 0.3f * Clamp01(gusts) * WindShare(row);
        }

        /// <summary>A gust swings the lean this share of its size to either side (0.2 = 11 degrees round the compass).</summary>
        public static float SwingShare(float row, float gusts)
        {
            return 0.25f * Clamp01(gusts) * WindShare(row);
        }

        /// <summary>How much heavier and lighter the rain falls in a gust and a lull.</summary>
        public static float Bands(float row, float gusts)
        {
            return Clamp01(gusts) * WindShare(row);
        }

        /// <summary>The steepest mean lean x gust, as degrees from upright: for the log.</summary>
        public static float Degrees(float lean)
        {
            return (float)(Math.Atan(lean) * 180.0 / Math.PI);
        }

        // ---------------------------------------------------------------------------------------------
        // The life (part of Finer rain): sheets of denser and thinner rain passing, eddies that turn the
        // streaks off parallel, and the drops' own spread.

        /// <summary>The sheets' drift wraps here: a whole number of both their patterns (14 m x 160, 70 m x 32).</summary>
        public const float SheetWrap = 2240f;

        /// <summary>Seconds one turn of the eddies takes.</summary>
        public const float EddyTurn = 9f;

        /// <summary>
        /// How deep the sheets cut: the share of the drops a thin stretch loses. Half in still air, more
        /// when the wind is up and gusty.
        /// </summary>
        public static float SheetDepth(float windSpeed, float gusts)
        {
            return Math.Min(0.85f, 0.5f + 0.35f * Clamp01(gusts) * Clamp01(windSpeed / 8f));
        }

        /// <summary>m/s the sheets ride downwind: with the rain the air carries, and never standing still.</summary>
        public static float SheetSpeed(float carrySpeed)
        {
            return 1.25f * Math.Max(3f, Math.Abs(carrySpeed));
        }

        /// <summary>Metres an eddy moves a drop to each side: its path winds this wide, more in wind.</summary>
        public static float EddyReach(float windSpeed)
        {
            // 0.3 m turns a streak 4.5 degrees at most, 0.6 m 9 (the path turns once in 24 m of fall).
            return 0.3f + 0.3f * Clamp01(windSpeed / 15f);
        }

        /// <summary>
        /// How much of the lamps' and the lightning's light the drops throw back, by the Streak brightness
        /// row: a drop in a lamp's light shows whatever the row says of the rain in the dark, down to half;
        /// only a row near 0 (the streaks hidden) takes it away.
        /// </summary>
        public static float LightShare(float opacityRow)
        {
            float row = Math.Max(0f, opacityRow);
            return Math.Min(1f, row * 20f) * (0.5f + 0.5f * (float)Math.Sqrt(Math.Min(1f, row)));
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }
}
