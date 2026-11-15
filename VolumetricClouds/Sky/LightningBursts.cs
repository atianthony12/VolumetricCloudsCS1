using System.Collections.Generic;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// (Lightning variety, 2026-11-15) Strikes in bursts, as a real storm's come: one strike
    /// sometimes brings one to three more a few seconds later nearby, and now and then a twin lands a
    /// split second after it, side by side. Busier storms burst more. Pure: tools\test-lightning.ps1
    /// checks that the slider's mean rate still holds.
    /// </summary>
    /// <remarks>
    /// The base rate is divided by <see cref="ExpectedSize"/>, so "one every N seconds" stays true on
    /// average: the storm only clusters its strikes. Follow-ups never burst again.
    /// </remarks>
    public static class LightningBursts
    {
        public struct FollowUp
        {
            /// <summary>Seconds after the first strike.</summary>
            public float Delay;
            /// <summary>Offset from the first strike's ground point, metres.</summary>
            public float Dx, Dz;
            /// <summary>A twin: a split second later, close beside it, always a channel to the ground.</summary>
            public bool Twin;
        }

        public const float MeanChain = 2f;

        /// <summary>Chance that a strike brings a chain of 1-3 more.</summary>
        public static float ChainChance(float activity, float variety)
        {
            float v = Mathf.Clamp(variety, 0f, 2f);
            if (v <= 0f)
                return 0f;
            return Mathf.Min(0.8f, (0.25f + 0.1f * Mathf.Clamp(activity, 0f, 4f)) * v);
        }

        /// <summary>Chance of a twin.</summary>
        public static float TwinChance(float variety)
        {
            return Mathf.Min(0.25f, 0.1f * Mathf.Clamp(variety, 0f, 2f));
        }

        /// <summary>Strikes per first strike, on average (1 = no bursts).</summary>
        public static float ExpectedSize(float activity, float variety)
        {
            return 1f + ChainChance(activity, variety) * MeanChain + TwinChance(variety);
        }

        /// <summary>The follow-ups of one strike, into <paramref name="into"/> (cleared first).</summary>
        public static void Draw(System.Random random, float activity, float variety, List<FollowUp> into)
        {
            into.Clear();
            if (variety <= 0f)
                return;

            if (random.NextDouble() < ChainChance(activity, variety))
            {
                int count = 1 + random.Next(0, 3);
                float at = 0f;
                for (int k = 0; k < count; k++)
                {
                    at += Mathf.Lerp(0.3f, 4f / count, (float)random.NextDouble());
                    into.Add(Offset(random, at, 100f, 1500f, false));
                }
            }

            if (random.NextDouble() < TwinChance(variety))
                into.Add(Offset(random, Mathf.Lerp(0.06f, 0.25f, (float)random.NextDouble()), 80f, 400f, true));
        }

        private static FollowUp Offset(System.Random random, float delay, float nearest, float furthest, bool twin)
        {
            float angle = (float)random.NextDouble() * Mathf.PI * 2f;
            float distance = Mathf.Lerp(nearest, furthest, (float)random.NextDouble());
            return new FollowUp
            {
                Delay = delay,
                Dx = Mathf.Cos(angle) * distance,
                Dz = Mathf.Sin(angle) * distance,
                Twin = twin,
            };
        }

        /// <summary>
        /// The bolt's colour as seen through this much air (variety only): close strikes blue-white,
        /// far ones dimmer and pinker. 7 km reads about (0.7, 0.55, 0.6) of the strike's own colour:
        /// green and blue scatter out of the line of sight first.
        /// </summary>
        public static Color DistanceTint(float distance, float variety)
        {
            float v = Mathf.Clamp01(variety);
            if (v <= 0f)
                return Color.white;

            float d = Mathf.Max(0f, distance);
            float dim = Mathf.Exp(-d * 5e-5f);
            Color tint = new Color(dim, dim * Mathf.Exp(-d * 3.55e-5f), dim * Mathf.Exp(-d * 2.15e-5f), 1f);
            return Color.Lerp(Color.white, tint, v);
        }
    }
}
