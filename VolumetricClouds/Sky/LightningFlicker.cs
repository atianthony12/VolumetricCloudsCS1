using ColossalFramework.Math;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// How bright a lightning strike is at each moment of its life.
    /// </summary>
    /// <remarks>
    /// Kept apart from CloudLightning, and free of anything that needs a running Unity, so it
    /// can be checked offline (tools/test-lightning.ps1 loads the mod DLL and calls it).
    ///
    /// Two kinds of strike use it:
    ///
    /// THE GAME'S strikes must pulse exactly as the game draws them, because its ground lights
    /// and thunder run on its own clock: <see cref="Intensity"/> is a port of the formula in
    /// WeatherManager.EndRenderingImpl. The IL:
    ///   r = new Randomizer(start ^ (frame / 3)); level = r.Int32(0, 100) * 0.01
    ///   if (2 &lt;= age &lt;= 4) level = max(0.75, level)
    ///   t = clamp01(age / 20); envelope = (6.75 * t * (t * (t - 2) + 1))^2
    ///   intensity = lerp(level, envelope, m_flashingReduction)
    /// A random level reseeded every three frames and held high at the start, blended towards
    /// a smooth envelope -- [6.75 t (1 - t)^2]^2, peaking at 1 a third of the way in and gone
    /// by frame 20 of 30 -- as the flashing reduction rises. For these only what the game
    /// does not show may vary: power, reach, tint, the shape of the bolt.
    ///
    /// OUR OWN, visual-only strikes have no such partner, and identical strikes are what
    /// makes a storm look canned, so everything about them is drawn from a
    /// <see cref="Profile"/>: how long they last, how nervously they flicker, how many return
    /// strokes they have, how bright and far-reaching they are, what colour. The flashing
    /// reduction means the same thing in both: it irons the random flicker out.
    /// </remarks>
    public static class LightningFlicker
    {
        /// <summary>The game draws a strike for this many simulation frames.</summary>
        public const uint StrikeFrames = 30;

        /// <summary>One strike's character. All of it comes from the strike's own seed.</summary>
        public struct Profile
        {
            /// <summary>True for the game's strikes: timing is the game's, to the frame.</summary>
            public bool Exact;

            /// <summary>How long the strike lasts, in 60 Hz frames.</summary>
            public uint Frames;

            /// <summary>Frames each random level is held: 2 is a nervous flutter, 6 slow pulses.</summary>
            public uint Reseed;

            /// <summary>Return strokes: separate peaks inside the one strike.</summary>
            public int Strokes;

            /// <summary>Peak brightness, relative to a standard strike.</summary>
            public float Power;

            /// <summary>Metres its light carries through the cloud.</summary>
            public float Reach;

            public Color Tint;

            /// <summary>
            /// (Lightning variety, 2026-11-15) Frames at the start of <see cref="Frames"/> in which a
            /// faint leader crawls down before the channel flashes: 0 = none (every strike before).
            /// </summary>
            public uint LeaderFrames;

            /// <summary>
            /// (Lightning variety) One of ours with a <see cref="LightningBoltShape.Kind"/> (or -1 for
            /// a sheet): the kind shapes its flash too -- a heavy stroke is brighter and short, a
            /// staccato one has several strokes, a crawler is long and rolling -- with a leader in
            /// front of most channels, and a wider palette. <paramref name="variety"/> 0..2.
            /// </summary>
            public static Profile Random(System.Random random, bool toGround, int kind, float variety)
            {
                Profile p = Random(random, toGround);
                float v = Mathf.Clamp(variety, 0f, 2f);
                if (v <= 0f)
                    return p;

                switch (kind)
                {
                    case (int)LightningBoltShape.Kind.Heavy:
                        p.Strokes = 1;
                        p.Frames = (uint)Mathf.Max(14, Mathf.Min((int)p.Frames, 40));
                        p.Power = Mathf.Max(p.Power, 1.25f) * 1.15f;
                        p.Reseed = 4u;
                        break;
                    case (int)LightningBoltShape.Kind.Staccato:
                        p.Strokes = 3 + random.Next(0, 2);
                        p.Frames = (uint)Mathf.Max((int)p.Frames, 50);
                        p.Reseed = 2u;
                        break;
                    case (int)LightningBoltShape.Kind.Crawler:
                        p.Frames = (uint)Mathf.Max((int)p.Frames, 60 + random.Next(0, 40));
                        p.Strokes = Mathf.Max(p.Strokes, 2);
                        p.Reach = Mathf.Max(p.Reach, 3500f);
                        break;
                    case (int)LightningBoltShape.Kind.AirDischarge:
                        p.Power *= 0.8f;
                        break;
                }

                // A leader for most channels (none for sheets): 6-18 frames, longer for long strokes.
                if (kind >= 0 && random.NextDouble() < Mathf.Min(1f, 0.75f * v))
                {
                    uint leader = (uint)(6 + random.Next(0, 13));
                    p.LeaderFrames = leader;
                    p.Frames += leader;
                }

                p.Tint = VarietyTint(random, v, p.Tint);
                return p;
            }

            /// <summary>
            /// The wider palette (Lightning variety): blue-white 45%, white 15%, violet 15%, pink-violet
            /// 8%, warm white 10%, a green-cyan cast 7% (seen through heavy rain); saturation stays
            /// under 0.35. Below 100% it blends from the old tint.
            /// </summary>
            public static Color VarietyTint(System.Random random, float variety, Color old)
            {
                double roll = random.NextDouble();
                float u = (float)random.NextDouble();
                Color c;
                if (roll < 0.45) c = new Color(Mathf.Lerp(0.7f, 0.82f, u), Mathf.Lerp(0.8f, 0.9f, u), 1f);
                else if (roll < 0.6) c = new Color(Mathf.Lerp(0.95f, 1f, u), Mathf.Lerp(0.96f, 1f, u), 1f);
                else if (roll < 0.75) c = new Color(Mathf.Lerp(0.74f, 0.84f, u), Mathf.Lerp(0.66f, 0.74f, u), 1f);
                else if (roll < 0.83) c = new Color(Mathf.Lerp(0.92f, 1f, u), Mathf.Lerp(0.68f, 0.76f, u), Mathf.Lerp(0.9f, 1f, u));
                else if (roll < 0.93) c = new Color(1f, Mathf.Lerp(0.88f, 0.95f, u), Mathf.Lerp(0.74f, 0.84f, u));
                else c = new Color(Mathf.Lerp(0.68f, 0.76f, u), 1f, Mathf.Lerp(0.88f, 0.96f, u));
                return Color.Lerp(old, c, Mathf.Clamp01(variety));
            }

            /// <summary>The game's strike: its timing, our choice of everything it does not show.</summary>
            public static Profile ForGameStrike(System.Random random)
            {
                return new Profile
                {
                    Exact = true,
                    Frames = StrikeFrames,
                    Reseed = 3,
                    Strokes = 1,
                    Power = Mathf.Lerp(0.8f, 1.35f, (float)random.NextDouble()),
                    Reach = Mathf.Lerp(1800f, 3000f, (float)random.NextDouble()),
                    Tint = RandomTint(random),
                };
            }

            /// <summary>One of ours: nothing about it is fixed.</summary>
            public static Profile Random(System.Random random, bool toGround)
            {
                // Mostly short, now and then a long rolling one: a quarter of a second to 1.6 s.
                float length = (float)random.NextDouble();
                uint frames = 14u + (uint)(82f * length * length);

                // One to four return strokes; a long strike always has more than one.
                double roll = random.NextDouble();
                int strokes = roll < 0.45 ? 1 : roll < 0.75 ? 2 : roll < 0.92 ? 3 : 4;
                if (frames > 55u && strokes < 2)
                    strokes = 2;

                float power = (float)random.NextDouble();

                return new Profile
                {
                    Exact = false,
                    Frames = frames,
                    Reseed = 2u + (uint)random.Next(0, 5),
                    Strokes = strokes,
                    Power = 0.55f + 0.95f * power * Mathf.Sqrt(power),
                    Reach = toGround
                        ? Mathf.Lerp(1600f, 3200f, (float)random.NextDouble())
                        : Mathf.Lerp(2200f, 4500f, (float)random.NextDouble()),
                    Tint = RandomTint(random),
                };
            }

            /// <summary>Mostly blue-white, sometimes violet, now and then a warm white.</summary>
            private static Color RandomTint(System.Random random)
            {
                double roll = random.NextDouble();
                float v = (float)random.NextDouble();

                if (roll < 0.7)
                    return new Color(Mathf.Lerp(0.72f, 0.84f, v), Mathf.Lerp(0.82f, 0.9f, v), 1f);
                if (roll < 0.9)
                    return new Color(Mathf.Lerp(0.8f, 0.9f, v), Mathf.Lerp(0.72f, 0.8f, v), 1f);
                return new Color(1f, Mathf.Lerp(0.9f, 0.96f, v), Mathf.Lerp(0.78f, 0.88f, v));
            }
        }

        /// <summary>0..1 before <see cref="Profile.Power"/>, for a strike of any profile.</summary>
        public static float Intensity(uint frame, uint start, float reduction, Profile profile)
        {
            if (profile.Exact)
                return Intensity(frame, start, reduction);

            uint age = frame - start;
            if (age >= profile.Frames)
                return 0f;

            // The leader: a faint glow in the cloud while it crawls down (the channel's own
            // brightness is the shader's, LeaderReveal), then the strike as before over what is left.
            if (profile.LeaderFrames > 0u)
            {
                if (age < profile.LeaderFrames)
                {
                    float crawl = new Randomizer(start ^ (frame / 2u)).Int32(0, 100) * 0.01f;
                    return 0.06f + 0.06f * crawl;
                }

                age -= profile.LeaderFrames;
                profile.Frames -= profile.LeaderFrames;
                start += profile.LeaderFrames;
                profile.LeaderFrames = 0u;
            }

            float u = age / (float)profile.Frames;

            // Each return stroke is the game's own pulse shape, squeezed into its share of the
            // strike. They overlap, start at slightly irregular moments, and weaken in turn.
            // begin + width stays below 1 for every stroke, so each one finishes inside the
            // strike: the last one fades out instead of being chopped off.
            float envelope = 0f;
            float width = profile.Strokes == 1 ? 1f : 1.15f / profile.Strokes;

            for (int k = 0; k < profile.Strokes; k++)
            {
                float jitter = new Randomizer(start * 31u + (uint)k).Int32(0, 100) * 0.01f;
                float begin = (k + jitter * 0.45f) / profile.Strokes * (1f - width);

                float t = (u - begin) / width;
                if (t <= 0f || t >= 1f)
                    continue;

                float pulse = 6.75f * t * (t * (t - 2f) + 1f);
                envelope = Mathf.Max(envelope, pulse * pulse * (1f - 0.2f * k));
            }

            // The flicker rides on the envelope instead of replacing it, so a strike dies away
            // rather than being cut off at full flutter as the game's is.
            uint reseed = profile.Reseed < 1u ? 1u : profile.Reseed;
            float level = new Randomizer(start ^ (frame / reseed)).Int32(0, 100) * 0.01f;
            float flickering = envelope * (0.45f + 0.55f * level);

            return Mathf.Clamp01(Mathf.Lerp(flickering, envelope, reduction));
        }

        /// <summary>
        /// How far down the leader has crawled, 0..1 of the channel's path (LightningRibbon.cginc
        /// RibbonShown), or -1 when there is no leader now (the whole channel shows). Stepped, as a
        /// real stepped leader jumps: a new step every frame, a little uneven.
        /// </summary>
        public static float LeaderReveal(uint frame, uint start, Profile profile)
        {
            if (profile.LeaderFrames == 0u)
                return -1f;

            uint age = frame - start;
            if (age >= profile.LeaderFrames)
                return -1f;

            float u = (age + 1f) / profile.LeaderFrames;
            float wobble = (new Randomizer(start * 7u + age).Int32(0, 100) * 0.01f - 0.5f) * 0.5f / profile.LeaderFrames;
            // Slow at first, then racing to the ground, as the leader's last steps are.
            return Mathf.Clamp01(u * u * (1.6f - 0.6f * u) + wobble);
        }

        /// <summary>
        /// 0..1: how much the forks have faded. They light with the first return stroke and the later
        /// strokes run down the main channel alone. 0 for a strike without a leader (as before).
        /// </summary>
        public static float ForkFade(uint frame, uint start, Profile profile)
        {
            if (profile.LeaderFrames == 0u || profile.Strokes < 2)
                return 0f;

            uint age = frame - start;
            if (age < profile.LeaderFrames)
                return 0f;

            float u = (age - profile.LeaderFrames) / (float)Mathf.Max(1, (int)(profile.Frames - profile.LeaderFrames));
            return Mathf.Clamp01((u - 1f / profile.Strokes) * 3f) * 0.8f;
        }

        /// <summary>The game's formula, to the frame.</summary>
        public static float Intensity(uint frame, uint start, float reduction)
        {
            uint age = frame - start;

            Randomizer randomizer = new Randomizer(start ^ (frame / 3u));
            float level = randomizer.Int32(0, 100) * 0.01f;
            if (age >= 2u && age <= 4u)
                level = Mathf.Max(0.75f, level);

            float t = Mathf.Clamp01(age / 20f);
            float envelope = 6.75f * t * (t * (t - 2f) + 1f);
            envelope *= envelope;

            return Mathf.Lerp(level, envelope, reduction);
        }
    }
}
