using System;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// The ground's wetness BY PLACE (1.6.1; pure, tools\test-wetmap.ps1): the game's own rule for its one number,
    /// run per cell of the city with the rain that falls there.
    /// </summary>
    /// <remarks>
    /// The game (WeatherManager.SimulationStepImpl, read from the IL), once a simulation frame:
    ///   w = clamp01(w - (w x 0.00013 + 0.00001) + min(rain, 0.25) x 0.00061)
    /// with rain = m_currentRain, the same everywhere: wet roads came out 53-94% wet under a clear sky in his log
    /// (2026-11-20) while the rain fell kilometres away. Per cell here, rain = m_currentRain x CloudRain.LocalRain at
    /// the cell, so with "follow the clouds" off (LocalRain = 1 everywhere) every cell IS the game's number.
    ///
    /// N frames at once, exactly: the step is affine (w' = w (1 - a) + b, with a = 0.00013, b = min(rain, 0.25) x
    /// 0.00061 - 0.00001), so after N of them w = w q + b (1 - q) / a with q = (1 - a)^N. The clamp is the only
    /// thing that is not affine, and clamping once at the end is the same: the sequence runs straight towards the
    /// fixed point b / a, which lies outside 0..1 whenever the clamp would bite (under 0 with no rain, over 1 at the
    /// most rain), so once it reaches a bound it stays there step by step too.
    ///
    /// A cell is never wetter than the game's own number (<see cref="Follow"/>): with less rain it can only be drier,
    /// and another mod that dries the game's ground at once dries every cell with it. The map holds each cell's share
    /// of the game's number (<see cref="Ratio"/>), what the wet-ground pass multiplies by.
    /// </remarks>
    public static class WetMapRules
    {
        /// <summary>The game's drying per frame: a share of the wetness, and a little more.</summary>
        public const float DryShare = 0.00013f, DryLeak = 0.00001f;

        /// <summary>The game's wetting per frame, per unit of rain, and the rain it stops counting at.</summary>
        public const float Soak = 0.00061f, RainCap = 0.25f;

        /// <summary>The game's rule run <paramref name="frames"/> simulation frames with this much rain.</summary>
        public static float Step(float wetness, float rain, uint frames)
        {
            if (frames == 0u)
                return Clamp01(wetness);

            double a = DryShare;
            double b = Math.Min(Math.Max(rain, 0f), RainCap) * (double)Soak - DryLeak;
            double q = Math.Pow(1.0 - a, frames);
            double w = wetness * q + b * (1.0 - q) / a;
            return Clamp01((float)w);
        }

        /// <summary>The game's rule, one frame at a time (the reference the test holds <see cref="Step"/> to).</summary>
        public static float StepFrames(float wetness, float rain, uint frames)
        {
            float w = wetness;
            for (uint k = 0; k < frames; k++)
            {
                w -= w * DryShare + DryLeak;
                w += Math.Min(rain, RainCap) * Soak;
                w = Clamp01(w);
            }
            return w;
        }

        /// <summary>A cell is never wetter than the game's number (see the remarks).</summary>
        public static float Follow(float cell, float game)
        {
            return Math.Min(cell, Clamp01(game));
        }

        /// <summary>The cell's share of the game's wetness, 0..1; 1 where the game's ground is dry (nothing to share).</summary>
        public static float Ratio(float cell, float game)
        {
            if (game <= 1e-4f)
                return 1f;
            return Clamp01(cell / game);
        }

        /// <summary>
        /// Simulation frames gone by from <paramref name="last"/> to <paramref name="now"/> (the frame index wraps at
        /// 2^32: unsigned difference); a jump back or one of more than <paramref name="most"/> (a load, a reset) counts as none.
        /// </summary>
        public static uint FramesSince(uint last, uint now, uint most)
        {
            uint d = unchecked(now - last);
            return d > most ? 0u : d;
        }

        private static float Clamp01(float x)
        {
            return x < 0f ? 0f : x > 1f ? 1f : x;
        }
    }
}
