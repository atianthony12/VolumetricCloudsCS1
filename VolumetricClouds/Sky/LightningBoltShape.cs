using System;
using System.Collections.Generic;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// The shapes a lightning channel can take (2026-11-15, the author: "most strikes look about the
    /// same ... different shapes for the bolts"). Pure: point lists, widths and a path position, no
    /// Mesh, so tools\test-lightning.ps1 can check every kind offline. LightningBoltMesh turns them
    /// into a mesh.
    /// </summary>
    /// <remarks>
    /// Every kind is grown by the same midpoint displacement as before (split each segment, push the
    /// new point sideways, halve the push, repeat). What differs is the skeleton it is grown on and
    /// how forks leave it:
    ///   Classic       one channel cloud to ground, short forks (1.5.3's bolt, the same numbers)
    ///   Forked        splits 40-70% of the way down into 2-3 limbs that reach the ground apart
    ///   Heavy         few long kinks, thick, one bright stroke
    ///   Staccato      thin and bushy: many short fine branches, several return strokes
    ///   AirDischarge  leaves the base and dies in the air in a spray of forks (never lands)
    ///   Crawler       along the cloud base for kilometres, tendrils hanging from it
    ///   CloudToCloud  a bow between two points of the base, dipping a little under it
    /// The two that run sideways stay IN the base (2026-11-18, the author: horizontal ones "rendering under
    /// the clouds ... should only be within or right under but touching the clouds"): their level is a little
    /// above the altitude setting (the drawn base: the layer fades in over its bottom), and they run only
    /// where the weather map has cloud (the caller's test), shortened where it ends.
    /// PATH POSITION: 0 where the channel leaves the cloud, 1 at its furthest tip; a fork continues
    /// its parent's value, so a leader revealed up to a path position shows the forks it has passed
    /// (LightningRibbon.cginc RibbonShown).
    /// </remarks>
    public static class LightningBoltShape
    {
        public enum Kind
        {
            Classic = 0,
            Forked = 1,
            Heavy = 2,
            Staccato = 3,
            AirDischarge = 4,
            Crawler = 5,
            CloudToCloud = 6,
        }

        public const int KindCount = 7;

        public const int MainLevels = 6;
        public const int ForkLevels = 4;
        public const float MainWidth = 3f;
        public const float ForkWidth = 1.5f;

        /// <summary>One ribbon of the bolt: its points and how it is drawn.</summary>
        public sealed class Ribbon
        {
            public List<Vector3> Points;
            public float Width;
            /// <summary>Brightness at its start (1 = the main channel).</summary>
            public float Brightness;
            /// <summary>Its tip's brightness as a share of <see cref="Brightness"/>.</summary>
            public float Tip;
            /// <summary>Path position at its first and last point (see the class remarks).</summary>
            public float PathStart, PathEnd;
        }

        /// <summary>True for the kinds that end on the ground (thunder from the ground point, a ground flash).</summary>
        public static bool ReachesGround(Kind kind)
        {
            return kind != Kind.AirDischarge && kind != Kind.Crawler && kind != Kind.CloudToCloud;
        }

        /// <summary>
        /// Which kind a strike gets. <paramref name="variety"/> 0..2 (the row, 1 = 100%); 0 = always
        /// Classic, as before. A stroke to the ground picks among the ground kinds (and now and then
        /// dies in the air); one that stays in the cloud gets a visible channel only sometimes
        /// (crawlers mostly while the camera is under the base, where they are seen best).
        /// </summary>
        /// <param name="toGround">The strike was meant to reach the ground.</param>
        /// <param name="gameStrike">The game's own strike: must land on the game's point.</param>
        /// <param name="underBase">The camera is under the cloud base.</param>
        /// <returns>The kind, or -1 for a sheet with no channel at all.</returns>
        public static int Pick(double roll, float variety, bool toGround, bool gameStrike, bool underBase)
        {
            float v = Mathf.Clamp(variety, 0f, 2f);
            if (v <= 0f)
                return toGround ? (int)Kind.Classic : -1;

            double r = Clamp01(roll);
            if (toGround)
            {
                // At 100%: classic 34%, forked 20%, heavy 14%, staccato 18%, dies in the air 14%
                // (game strikes never: their fire lands where the game says). 200% halves the classic.
                float classic = Mathf.Lerp(1f, 0.34f, Mathf.Min(v, 1f)) * (v > 1f ? Mathf.Lerp(1f, 0.5f, v - 1f) : 1f);
                float rest = 1f - classic;
                float air = gameStrike ? 0f : 0.14f;
                float sum = 0.2f + 0.14f + 0.18f + air;
                double c0 = classic;
                double c1 = c0 + rest * 0.2f / sum;
                double c2 = c1 + rest * 0.14f / sum;
                double c3 = c2 + rest * 0.18f / sum;
                if (r < c0) return (int)Kind.Classic;
                if (r < c1) return (int)Kind.Forked;
                if (r < c2) return (int)Kind.Heavy;
                if (r < c3) return (int)Kind.Staccato;
                return (int)Kind.AirDischarge;
            }

            // In-cloud strikes: most stay sheets. A channel shows for up to 40% of them at 100%
            // (60% at 200%); crawlers take most of that under the base, cloud-to-cloud bows otherwise.
            float shown = Mathf.Min(0.6f, 0.4f * v);
            if (r >= shown)
                return -1;
            float crawlerShare = underBase ? 0.65f : 0.3f;
            return r < shown * crawlerShare ? (int)Kind.Crawler : (int)Kind.CloudToCloud;
        }

        /// <summary>
        /// Grows a bolt of <paramref name="kind"/> into <paramref name="ribbons"/> (cleared first).
        /// <paramref name="top"/> is where it leaves the cloud, <paramref name="ground"/> where a
        /// ground kind lands; <paramref name="cloudBase"/> the height of the cloud base (crawlers and
        /// bows run in it). <paramref name="cloudy"/>: whether there is cloud over a point (x, z), so the
        /// sideways kinds stay under it; null = cloud everywhere.
        /// </summary>
        public static void Build(Kind kind, Vector3 top, Vector3 ground, float cloudBase, System.Random random,
            List<Ribbon> ribbons, Func<Vector3, bool> cloudy)
        {
            ribbons.Clear();
            switch (kind)
            {
                case Kind.Forked: BuildForked(top, ground, random, ribbons); break;
                case Kind.Heavy: BuildHeavy(top, ground, random, ribbons); break;
                case Kind.Staccato: BuildStaccato(top, ground, random, ribbons); break;
                case Kind.AirDischarge: BuildAirDischarge(top, ground, random, ribbons); break;
                case Kind.Crawler: BuildCrawler(top, cloudBase, random, ribbons, cloudy); break;
                case Kind.CloudToCloud: BuildCloudToCloud(top, cloudBase, random, ribbons, cloudy); break;
                default: BuildClassic(top, ground, random, ribbons); break;
            }
        }

        // ---- the kinds ------------------------------------------------------------------------

        private static void BuildClassic(Vector3 top, Vector3 ground, System.Random random, List<Ribbon> ribbons)
        {
            float roughness = Mathf.Lerp(0.09f, 0.19f, Next(random));
            float weight = Mathf.Lerp(0.7f, 1.5f, Next(random));
            float forkChance = Mathf.Lerp(0.03f, 0.2f, Next(random));

            List<Vector3> main = Channel(top, ground, MainLevels, roughness, random);
            Add(ribbons, main, MainWidth * weight, 1f, 1f, 0f, 1f);
            AddForks(ribbons, main, ground, 0f, 1f, 0.1f, 0.75f, forkChance, weight, 0.2f, 0.5f, random);
        }

        private static void BuildForked(Vector3 top, Vector3 ground, System.Random random, List<Ribbon> ribbons)
        {
            float roughness = Mathf.Lerp(0.1f, 0.17f, Next(random));
            float weight = Mathf.Lerp(0.9f, 1.4f, Next(random));
            float split = Mathf.Lerp(0.4f, 0.7f, Next(random));

            // The trunk to the split, the first limb on to the ground point itself (a game strike's
            // fire is there), the others to ground points 100-600 m away.
            Vector3 splitPoint = Vector3.Lerp(top, ground, split);
            splitPoint += Horizontal(random) * ((ground - top).magnitude * 0.06f);
            List<Vector3> trunk = Channel(top, splitPoint, MainLevels - 1, roughness, random);
            Add(ribbons, trunk, MainWidth * weight, 1f, 1f, 0f, split);
            AddForks(ribbons, trunk, ground, 0f, split, 0.15f, 0.9f, 0.05f, weight, 0.15f, 0.35f, random);

            int limbs = random.NextDouble() < 0.6 ? 2 : 3;
            for (int k = 0; k < limbs; k++)
            {
                Vector3 end = ground;
                if (k > 0)
                {
                    Vector3 away = Horizontal(random) * Mathf.Lerp(100f, 600f, Next(random));
                    end = new Vector3(ground.x + away.x, ground.y, ground.z + away.z);
                }

                List<Vector3> limb = Channel(trunk[trunk.Count - 1], end, MainLevels - 1, roughness * 1.1f, random);
                // The landing limb is as bright as the trunk; the others a little less.
                Add(ribbons, limb, MainWidth * weight * (k == 0 ? 0.95f : 0.75f), k == 0 ? 1f : 0.8f, 1f, split, 1f);
            }
        }

        private static void BuildHeavy(Vector3 top, Vector3 ground, System.Random random, List<Ribbon> ribbons)
        {
            // Few levels of displacement but a large first push: long straight runs and sharp kinks.
            float roughness = Mathf.Lerp(0.14f, 0.24f, Next(random));
            float weight = Mathf.Lerp(1.8f, 2.6f, Next(random));
            List<Vector3> main = Channel(top, ground, 4, roughness, random);
            main = Refine(main, 2, 0.02f, random);
            Add(ribbons, main, MainWidth * weight, 1f, 1f, 0f, 1f);
            AddForks(ribbons, main, ground, 0f, 1f, 0.15f, 0.6f, 0.04f, weight * 0.7f, 0.1f, 0.25f, random);
        }

        private static void BuildStaccato(Vector3 top, Vector3 ground, System.Random random, List<Ribbon> ribbons)
        {
            float roughness = Mathf.Lerp(0.12f, 0.2f, Next(random));
            float weight = Mathf.Lerp(0.5f, 0.8f, Next(random));
            List<Vector3> main = Channel(top, ground, MainLevels, roughness, random);
            Add(ribbons, main, MainWidth * weight, 1f, 1f, 0f, 1f);
            // Bushy: many short fine branches all the way down to near the ground.
            AddForks(ribbons, main, ground, 0f, 1f, 0.05f, 0.92f, 0.3f, weight, 0.04f, 0.16f, random);
        }

        private static void BuildAirDischarge(Vector3 top, Vector3 ground, System.Random random, List<Ribbon> ribbons)
        {
            float reach = Mathf.Lerp(0.3f, 0.6f, Next(random));
            Vector3 end = Vector3.Lerp(top, ground, reach) + Horizontal(random) * ((ground - top).magnitude * 0.15f);
            float weight = Mathf.Lerp(0.7f, 1.2f, Next(random));
            List<Vector3> main = Channel(top, end, MainLevels - 1, 0.16f, random);
            Add(ribbons, main, MainWidth * weight, 1f, 0.35f, 0f, 1f);
            // It ends in a spray of short forks fanning out downwards.
            int spray = 3 + random.Next(0, 4);
            Vector3 down = (ground - top).normalized;
            for (int k = 0; k < spray; k++)
            {
                int from = main.Count / 2 + random.Next(0, main.Count / 2);
                from = Mathf.Min(from, main.Count - 1);
                float path = from / (float)(main.Count - 1);
                List<Vector3> fork = Fork(main[from], ground, down, 0.06f, 0.18f, random);
                Add(ribbons, fork, ForkWidth * weight * 0.8f, 0.4f, 0.05f, path, Mathf.Min(1f, path + 0.25f));
            }
        }

        /// <summary>How far into the base (above the altitude setting) the sideways kinds run: the drawn base.</summary>
        public const float InBaseLow = 20f, InBaseHigh = 90f;

        /// <summary>Their channel never strays further than this from the base: under it, or up into the cloud.</summary>
        public const float BelowBaseMost = 5f, AboveBaseMost = 160f;

        /// <summary>
        /// The longest run from <paramref name="start"/> along one of a few headings that stays under cloud
        /// (checked every <see cref="CloudStep"/> m), at most <paramref name="length"/>: the heading picked and how
        /// far it goes. Without a test, the first heading and the whole length.
        /// </summary>
        private static float CloudyRun(Vector3 start, float length, System.Random random, Func<Vector3, bool> cloudy,
            out Vector3 heading)
        {
            heading = Horizontal(random);
            if (cloudy == null)
                return length;

            float best = -1f;
            Vector3 bestHeading = heading;
            for (int tryHeading = 0; tryHeading < 6; tryHeading++)
            {
                Vector3 h = tryHeading == 0 ? heading : Horizontal(random);
                float run = 0f;
                while (run < length && cloudy(start + h * Mathf.Min(length, run + CloudStep)))
                    run += CloudStep;
                run = Mathf.Min(run, length);
                if (run > best)
                {
                    best = run;
                    bestHeading = h;
                }
                if (run >= length)
                    break;
            }
            heading = bestHeading;
            return best;
        }

        private static void ClampHeight(List<Vector3> points, float lowest, float highest)
        {
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 p = points[i];
                p.y = Mathf.Clamp(p.y, lowest, highest);
                points[i] = p;
            }
        }

        /// <summary>The spacing of the cloud test along a sideways channel, metres.</summary>
        private const float CloudStep = 200f;

        /// <summary><paramref name="to"/>, pulled back towards <paramref name="from"/> until it is under cloud (at most to a fifth of the way).</summary>
        private static Vector3 UnderCloud(Vector3 from, Vector3 to, Func<Vector3, bool> cloudy)
        {
            if (cloudy == null)
                return to;
            for (float t = 1f; t > 0.2f; t -= 0.2f)
            {
                Vector3 p = Vector3.Lerp(from, to, t);
                if (cloudy(p))
                    return p;
            }
            return Vector3.Lerp(from, to, 0.2f);
        }

        private static void BuildCrawler(Vector3 top, float cloudBase, System.Random random, List<Ribbon> ribbons,
            Func<Vector3, bool> cloudy)
        {
            // Along the cloud base, in its lowest part, for 2-6 km (less where the cloud ends), wandering sideways.
            float y = cloudBase + Mathf.Lerp(InBaseLow, InBaseHigh, Next(random));
            Vector3 start = new Vector3(top.x, y, top.z);
            float wanted = Mathf.Lerp(2000f, 6000f, Next(random));
            Vector3 heading;
            float length = Mathf.Max(600f, CloudyRun(start, wanted, random, cloudy, out heading));
            Vector3 end = start + heading * length;

            List<Vector3> main = Channel(start, end, MainLevels, 0.12f, random);
            // Keep it flat-ish and in the base: the displacement pushed it in 3D.
            for (int i = 0; i < main.Count; i++)
            {
                Vector3 p = main[i];
                p.y = y + (p.y - y) * 0.25f;
                main[i] = p;
            }
            ClampHeight(main, cloudBase - BelowBaseMost, cloudBase + AboveBaseMost);

            float weight = Mathf.Lerp(0.8f, 1.3f, Next(random));
            Add(ribbons, main, MainWidth * weight, 0.9f, 0.6f, 0f, 1f);

            // Branches crawl off sideways along the base, and tendrils hang down from it.
            int branches = 2 + random.Next(0, 4);
            for (int k = 0; k < branches; k++)
            {
                int from = random.Next(main.Count / 8, main.Count - 1);
                float path = from / (float)(main.Count - 1);
                Vector3 side = Vector3.Cross(heading, Vector3.up) * (random.NextDouble() < 0.5 ? -1f : 1f);
                Vector3 dir = (heading * Next(random) + side).normalized;
                Vector3 to = main[from] + dir * Mathf.Lerp(400f, 1500f, Next(random));
                to.y = Mathf.Max(y - Mathf.Lerp(0f, 60f, Next(random)), cloudBase - BelowBaseMost);
                to = UnderCloud(main[from], to, cloudy);
                List<Vector3> branch = Channel(main[from], to, ForkLevels, 0.15f, random);
                ClampHeight(branch, cloudBase - BelowBaseMost, cloudBase + AboveBaseMost);
                Add(ribbons, branch, ForkWidth * weight, 0.55f, 0.15f, path, Mathf.Min(1f, path + 0.3f));
            }

            int tendrils = 3 + random.Next(0, 6);
            for (int k = 0; k < tendrils; k++)
            {
                int from = random.Next(0, main.Count);
                float path = from / (float)Mathf.Max(1, main.Count - 1);
                Vector3 to = main[from] + Horizontal(random) * Mathf.Lerp(20f, 150f, Next(random))
                           + Vector3.down * Mathf.Lerp(100f, 450f, Next(random));
                List<Vector3> tendril = Channel(main[from], to, ForkLevels - 1, 0.2f, random);
                Add(ribbons, tendril, ForkWidth * weight * 0.7f, 0.35f, 0.05f, path, Mathf.Min(1f, path + 0.1f));
            }
        }

        private static void BuildCloudToCloud(Vector3 top, float cloudBase, System.Random random, List<Ribbon> ribbons,
            Func<Vector3, bool> cloudy)
        {
            // A bow between two points in the base 1-3 km apart (both under cloud), dipping 40-160 m under it.
            float wanted = Mathf.Lerp(1000f, 3000f, Next(random));
            float dip = Mathf.Lerp(40f, 160f, Next(random));
            float level = cloudBase + Mathf.Lerp(InBaseLow, InBaseHigh, Next(random));
            Vector3 a = new Vector3(top.x, level, top.z);
            Vector3 heading;
            float span = Mathf.Max(500f, CloudyRun(a, wanted, random, cloudy, out heading));
            Vector3 b = a + heading * span;
            Vector3 mid = (a + b) * 0.5f + Vector3.down * dip + Horizontal(random) * (span * 0.1f);

            List<Vector3> first = Channel(a, mid, MainLevels - 1, 0.14f, random);
            List<Vector3> second = Channel(mid, b, MainLevels - 1, 0.14f, random);
            // The displacement pushes in 3D: never deeper under the base than the dip and a little, nor high into the cloud.
            ClampHeight(first, cloudBase - dip - 60f, cloudBase + AboveBaseMost);
            ClampHeight(second, cloudBase - dip - 60f, cloudBase + AboveBaseMost);
            float weight = Mathf.Lerp(0.8f, 1.3f, Next(random));
            Add(ribbons, first, MainWidth * weight, 1f, 1f, 0f, 0.5f);
            Add(ribbons, second, MainWidth * weight, 1f, 1f, 0.5f, 1f);
            AddForks(ribbons, first, mid + Vector3.down * 200f, 0f, 0.5f, 0.2f, 0.95f, 0.06f, weight, 0.1f, 0.25f, random);
        }

        // ---- the parts ------------------------------------------------------------------------

        /// <summary>Forks leaving <paramref name="main"/> between the shares <paramref name="from"/> and <paramref name="to"/> of it.</summary>
        private static void AddForks(List<Ribbon> ribbons, List<Vector3> main, Vector3 ground, float pathStart, float pathEnd,
            float from, float to, float chance, float weight, float shortest, float longest, System.Random random)
        {
            Vector3 down = (ground - main[0]).normalized;
            if (down.sqrMagnitude < 1e-6f)
                down = Vector3.down;

            int first = Mathf.Max(1, (int)(main.Count * from));
            int last = Mathf.Min(main.Count - 1, (int)(main.Count * to));
            for (int i = first; i < last; i++)
            {
                if (random.NextDouble() > chance)
                    continue;

                float path = Mathf.Lerp(pathStart, pathEnd, i / (float)(main.Count - 1));
                List<Vector3> fork = Fork(main[i], ground, down, shortest, longest, random);
                float reach = (pathEnd - pathStart) * Mathf.Lerp(shortest, longest, 0.5f);
                Add(ribbons, fork, ForkWidth * weight, 0.45f, 0.1f, path, Mathf.Min(1f, path + reach));

                // And the occasional fork of a fork.
                if (random.NextDouble() < 0.5)
                {
                    Vector3 start = fork[fork.Count / 2];
                    float mid = Mathf.Lerp(path, Mathf.Min(1f, path + reach), 0.5f);
                    Add(ribbons, Fork(start, ground, down, shortest * 0.5f, longest * 0.5f, random),
                        ForkWidth * weight * 0.7f, 0.22f, 0.05f, mid, Mathf.Min(1f, mid + reach * 0.5f));
                }
            }
        }

        private static List<Vector3> Fork(Vector3 from, Vector3 ground, Vector3 down, float shortest, float longest,
            System.Random random)
        {
            Vector3 outward = Horizontal(random);
            Vector3 direction = (down * 0.75f + outward * 0.65f).normalized;

            float remaining = Mathf.Max(50f, (ground - from).magnitude);
            float length = remaining * Mathf.Lerp(shortest, longest, Next(random));

            return Channel(from, from + direction * length, ForkLevels, 0.17f, random);
        }

        /// <summary>Midpoint displacement from a to b: 2^levels segments.</summary>
        public static List<Vector3> Channel(Vector3 a, Vector3 b, int levels, float roughness, System.Random random)
        {
            List<Vector3> points = new List<Vector3> { a, b };
            return Displace(points, levels, (b - a).magnitude * roughness, random);
        }

        /// <summary>More levels on an existing channel, starting from a small push (Heavy's fine grain).</summary>
        private static List<Vector3> Refine(List<Vector3> points, int levels, float roughness, System.Random random)
        {
            float length = 0f;
            for (int i = 0; i < points.Count - 1; i++)
                length += (points[i + 1] - points[i]).magnitude;
            return Displace(points, levels, length / Mathf.Max(1, points.Count - 1) * roughness * 4f, random);
        }

        private static List<Vector3> Displace(List<Vector3> points, int levels, float amplitude, System.Random random)
        {
            for (int level = 0; level < levels; level++)
            {
                List<Vector3> next = new List<Vector3>(points.Count * 2);

                for (int i = 0; i < points.Count - 1; i++)
                {
                    Vector3 axis = (points[i + 1] - points[i]).normalized;

                    // A random push, with the part along the segment removed.
                    Vector3 push = new Vector3(Next(random) * 2f - 1f, Next(random) * 2f - 1f, Next(random) * 2f - 1f);
                    push -= axis * Vector3.Dot(push, axis);

                    next.Add(points[i]);
                    next.Add((points[i] + points[i + 1]) * 0.5f + push * amplitude);
                }

                next.Add(points[points.Count - 1]);
                points = next;
                amplitude *= 0.5f;
            }

            return points;
        }

        private static void Add(List<Ribbon> ribbons, List<Vector3> points, float width, float brightness, float tip,
            float pathStart, float pathEnd)
        {
            if (points.Count < 2)
                return;
            ribbons.Add(new Ribbon
            {
                Points = points,
                Width = width,
                Brightness = brightness,
                Tip = tip,
                PathStart = pathStart,
                PathEnd = Mathf.Max(pathStart, pathEnd),
            });
        }

        private static Vector3 Horizontal(System.Random random)
        {
            float angle = Next(random) * Mathf.PI * 2f;
            return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
        }

        private static float Next(System.Random random)
        {
            return (float)random.NextDouble();
        }

        private static double Clamp01(double x)
        {
            return x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x;
        }
    }
}
