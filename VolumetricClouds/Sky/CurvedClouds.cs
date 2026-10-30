using System;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// CURVED CLOUDS (trial, 2026-10-23, the author after github.com/kotivas/skygl): the cloud layer
    /// follows the curved Earth and the march carries on until it meets the horizon, so the clouds go
    /// down to the horizon at any cover instead of ending ~1.5 degrees above it. The strength is the
    /// slider: 1 = the real Earth, more = a tighter curve (R / strength). 0 = off, the old path.
    /// </summary>
    /// <remarks>
    /// The curve itself is invisible near the city at 1 (d^2 / 2R: 23 m at the map's edge, 71 m at 30
    /// km); what it changes is the far end: a base 850 m up meets the horizon ~104 km out. Centred on the
    /// world's origin (the map's centre), not the camera, so nothing moves as the camera does and the
    /// shadow map (the same density function) keeps matching. Pure maths, no Unity: the shader's twins
    /// are in CloudCommon.cginc (the layer, the far sky's patches and fronts), CloudMedia.cginc (the haze) and CloudRaymarch
    /// (where a ray is in the layer); tools\test-curved.ps1 compiles this source.
    /// </remarks>
    public static class CurvedClouds
    {
        public const double EarthRadius = 6371000.0;

        /// <summary>The slider's top, as a strength: 400%, where a base 850 m up meets the horizon ~52 km out.</summary>
        public const float MaxStrength = 4f;

        /// <summary>
        /// Far clouds fade into the haze: presence = 1 - Haze (1 - exp(-t / HazeLength)). Not to nothing
        /// at the reach, as the flat clouds do at 30 km: the curve ends them at the horizon.
        /// </summary>
        public const float Haze = 0.6f, HazeLength = 30000f;

        /// <summary>
        /// The weather map repeats every Weather pattern size (13.5 km, 54 km with Larger weather pattern):
        /// marched to the horizon it came back along every ray, as arcs (the far deck's first trial,
        /// 2026-10-12), then -- under a warp of sines that moved each copy at most 3.6 km -- as the same
        /// clouds again (the author, 2026-10-30: "pattern repetition ... so obvious"). Past PatchFrom metres
        /// from the map's centre, all of it PatchBlend further, the sky is read in PATCHES
        /// (CloudCommon FarLookAt; <see cref="Patches"/>, <see cref="PatchUV"/> are the twins): hexagons
        /// <see cref="PatchSpacing"/> repeats apart, each reading the map turned by its own quarter turn,
        /// mirrored or not, shifted to its own place on it. Past the corner of the map (12.2 km) plus the
        /// rain's slant: the C# twins of the rain, which only look on the map, never need it.
        /// </summary>
        public const float PatchFrom = 20000f, PatchBlend = 4000f, PatchSpacing = 0.5f;

        /// <summary>
        /// The patch lattice rides with the wind: its drift is sent as a phase over this many cells (a
        /// patch's look repeats that many cells on, 64 x 27 km with Larger weather pattern).
        /// </summary>
        public const int LatticeRepeat = 64;

        /// <summary>
        /// THE FRONTS: past PatchFrom, eased in over FrontBlend metres, the map is raised or lowered by
        /// <see cref="Front"/> (about -1.5..1.5, slow waves of 90-160 km) x FrontStrength of its standard
        /// deviations: stretches of clear sky and of overcast out to the horizon, whatever the city has.
        /// The city's own sky and its rain are inside the ring, untouched.
        /// </summary>
        public const float FrontStrength = 1f, FrontBlend = 40000f;

        /// <summary>The lattice's skew (Mikkelsen's hex tiling): cells = (x - z / sqrt 3, 2 z / sqrt 3) / spacing.</summary>
        public const double Skew = 0.57735027, Stretch = 1.15470054;

        /// <summary>
        /// Past this distance along a ray the noise's mip level stays what it is here: further out the
        /// fine noise averaged away and the heaps and puffs it carves went with it (Cumulus's clouds
        /// vanished ~10-13 km out at grazing angles).
        /// </summary>
        public const float LodHold = 30000f;

        /// <summary>The longest march: past it a ray from high up would only meet clouds a few pixels tall.</summary>
        public const float MaxReach = 250000f;

        /// <summary>The strength from the slider's stored value (1 = 100%), clamped; 0 is off.</summary>
        public static float Strength(float stored)
        {
            if (float.IsNaN(stored) || stored <= 0f)
                return 0f;
            return Math.Min(stored, MaxStrength);
        }

        /// <summary>1 / 2R' (per metre): the shader's _Curve.x. 0 when off.</summary>
        public static float InverseTwoRadius(float strength)
        {
            return strength > 0f ? (float)(strength / (2.0 * EarthRadius)) : 0f;
        }

        /// <summary>How far the layer has come down under a point x, z metres from the map's centre.</summary>
        public static float Drop(float strength, float x, float z)
        {
            return (float)((x * (double)x + z * (double)z) * InverseTwoRadius(strength));
        }

        /// <summary>
        /// How far from a point `height` metres up the curved ground falls away under the horizon (the
        /// tangent's length, sqrt(2 R' h)): from the ground, where a layer that high meets the horizon.
        /// </summary>
        public static float HorizonDistance(float strength, float height)
        {
            if (strength <= 0f || height <= 0f)
                return 0f;
            return (float)Math.Sqrt(2.0 * EarthRadius / strength * height);
        }

        /// <summary>
        /// How far the march goes from a camera `cameraHeight` up for a layer whose top is `top` up: the
        /// furthest a line of sight can still touch the top (the camera's horizon + the top's), at least
        /// `flatReach` (what the flat clouds reach from there), at most <see cref="MaxReach"/>.
        /// </summary>
        public static float Reach(float strength, float cameraHeight, float top, float flatReach)
        {
            if (strength <= 0f)
                return flatReach;
            float far = HorizonDistance(strength, Math.Max(cameraHeight, 1f)) + HorizonDistance(strength, top);
            return Math.Min(MaxReach, Math.Max(flatReach, far));
        }

        /// <summary>How much of a cloud t metres away is left after the haze in front of it (CloudPresence's curved fade).</summary>
        public static float Presence(float t)
        {
            return 1f - Haze * (1f - (float)Math.Exp(-Math.Max(t, 0f) / HazeLength));
        }

        /// <summary>
        /// Where along a ray the layer is, as the shader works it out (CloudRaymarch CurvedSlab): the ray's
        /// height over the curved Earth is a t^2 + b t + h0, with a = k |dir.xz|^2, b = dir.y + 2 k
        /// origin.xz . dir.xz, h0 = origin.y + k |origin.xz|^2. Returns false when it never is. Leaves
        /// where it first leaves it (out of the top, or under the base); stops at the ground.
        /// </summary>
        public static bool Slab(float k, float ox, float oy, float oz, float dx, float dy, float dz,
                                float bottom, float top, out float enter, out float exit)
        {
            double a = k * (dx * (double)dx + dz * (double)dz);
            double b = dy + 2.0 * k * (ox * (double)dx + oz * (double)dz);
            double h0 = oy + k * (ox * (double)ox + oz * (double)oz);

            double top1, top2, base1, base2, ground1, ground2;
            bool overTop = !Roots(a, b, h0 - top, out top1, out top2);
            bool underBase = Roots(a, b, h0 - bottom, out base1, out base2);
            bool toGround = Roots(a, b, h0, out ground1, out ground2);

            enter = 0f;
            exit = 0f;
            if (overTop || top2 <= 0.0)
                return false;

            double from;
            if (h0 < bottom)
                from = base2;              // under the base: where the ray comes up through it
            else if (h0 < top)
                from = 0.0;                // in the layer
            else
                from = top1;               // over it: where the ray comes down into it

            double to = top2;
            if (underBase && base1 > from)
                to = Math.Min(to, base1);  // out under the base
            if (toGround && ground1 > 0.0 && ground1 < from)
                return false;              // the ground first
            if (to <= from)
                return false;

            enter = (float)Math.Max(from, 0.0);
            exit = (float)to;
            return true;
        }

        /// <summary>
        /// The lattice's drift as the shader's _CurvePatch.xy: the wind's drift (doubles, metres) in skewed
        /// cells, over <see cref="LatticeRepeat"/> of them -- whole repeats of the lattice read the same
        /// patches (PatchHash takes the cell modulo it), so this is exact for a city's whole life.
        /// </summary>
        public static void LatticePhase(double driftX, double driftZ, double spacing, out float phaseX, out float phaseZ)
        {
            phaseX = Drift.Phase((driftX - driftZ * Skew) / spacing, LatticeRepeat) * LatticeRepeat;
            phaseZ = Drift.Phase(driftZ * Stretch / spacing, LatticeRepeat) * LatticeRepeat;
        }

        /// <summary>
        /// The three patches round x, z and their weights, as FarLookAt has them (before the ring): `cells`
        /// gets their places in the lattice (x, z pairs), `weights` their sharpened weights, summing to 1.
        /// </summary>
        public static void Patches(float x, float z, float phaseX, float phaseZ, float inverseSpacing,
                                   float[] cells, float[] weights)
        {
            float stX = x * inverseSpacing, stZ = z * inverseSpacing;
            float skX = stX - (float)Skew * stZ - phaseX;
            float skZ = (float)Stretch * stZ - phaseZ;
            float cellX = (float)Math.Floor(skX), cellZ = (float)Math.Floor(skZ);
            float tx = skX - cellX, tz = skZ - cellZ, tw = 1f - tx - tz;
            float s = tw < 0f ? 1f : 0f;
            float s2 = 2f * s - 1f;

            weights[0] = Math.Max(-tw * s2, 0f);
            weights[1] = Math.Max(s - tz * s2, 0f);
            weights[2] = Math.Max(s - tx * s2, 0f);
            cells[0] = cellX + s; cells[1] = cellZ + s;
            cells[2] = cellX + s; cells[3] = cellZ + 1f - s;
            cells[4] = cellX + 1f - s; cells[5] = cellZ + s;

            float sum = 0f;
            for (int i = 0; i < 3; i++)
            {
                float b = weights[i] * weights[i];
                b *= b;
                b *= b;
                weights[i] = b * b;
                sum += weights[i];
            }

            float kept = 0f;
            for (int i = 0; i < 3; i++)
            {
                // Less a little, not cut below it: a cut would be a step (a seam) where a weight passes it.
                weights[i] = Math.Max(weights[i] / sum - 0.05f, 0f);
                kept += weights[i];
            }

            for (int i = 0; i < 3; i++)
                weights[i] /= kept;
        }

        /// <summary>PatchHash: three numbers 0..1 for the patch at cell (x, z) (Hoskins' hash, no sines).</summary>
        public static void PatchHash(float x, float z, out float h0, out float h1, out float h2)
        {
            x -= LatticeRepeat * (float)Math.Floor(x / LatticeRepeat);
            z -= LatticeRepeat * (float)Math.Floor(z / LatticeRepeat);
            float p0 = Frac(x * 0.1031f + 0.37f), p1 = Frac(z * 0.1030f + 0.37f), p2 = Frac(x * 0.0973f + 0.37f);
            float d = p0 * (p1 + 33.33f) + p1 * (p0 + 33.33f) + p2 * (p2 + 33.33f);
            p0 += d; p1 += d; p2 += d;
            h0 = Frac((p0 + p1) * p2);
            h1 = Frac((p0 + p2) * p1);
            h2 = Frac((p1 + p2) * p0);
        }

        /// <summary>PatchUV: the map's uv (u, v) as the patch at cell (x, z) reads it: a quarter turn, maybe mirrored, then shifted.</summary>
        public static void PatchUV(float u, float v, float cellX, float cellZ, out float ru, out float rv)
        {
            float h0, h1, h2;
            PatchHash(cellX, cellZ, out h0, out h1, out h2);
            float a = h0 < 0.5f ? u : -u, b = v;
            float q = Frac(h0 * 2f) * 4f;
            if (q < 1f) { ru = a; rv = b; }
            else if (q < 2f) { ru = -b; rv = a; }
            else if (q < 3f) { ru = -a; rv = -b; }
            else { ru = b; rv = -a; }
            ru += h1;
            rv += h2;
        }

        /// <summary>FrontAt: the fronts at x, z (m), about -1.5..1.5.</summary>
        public static float Front(float x, float z)
        {
            return 0.5f * (float)(Math.Sin(x * 5.3e-5 + z * 4.1e-5 + 0.9)
                                + Math.Sin(x * -4.4e-5 + z * 2.6e-5 + 2.3)
                                + Math.Sin(x * 1.5e-5 + z * -6.1e-5 + 4.4));
        }

        /// <summary>FarBlend without the front: lookups `values` under `weights`, the map's contrast kept.</summary>
        public static float Blend(float[] weights, float[] values, float mean)
        {
            float sum = 0f, squares = 0f;
            for (int i = 0; i < weights.Length; i++)
            {
                sum += weights[i] * (values[i] - mean);
                squares += weights[i] * weights[i];
            }
            return mean + sum / (float)Math.Sqrt(squares);
        }

        private static float Frac(float v)
        {
            return v - (float)Math.Floor(v);
        }

        /// <summary>
        /// The roots of a t^2 + b t + c = 0, the smaller first (the stable form: a is tiny, so b +- the
        /// root of the discriminant would cancel). False when there are none (the line never reaches it).
        /// </summary>
        public static bool Roots(double a, double b, double c, out double t1, out double t2)
        {
            double disc = b * b - 4.0 * a * c;
            if (disc < 0.0)
            {
                t1 = t2 = 1e10;
                return false;
            }

            double q = -0.5 * (b + (b >= 0.0 ? 1.0 : -1.0) * Math.Sqrt(disc));
            double r1 = Math.Abs(q) > 1e-30 ? c / q : 0.0;
            double r2 = Math.Abs(a) > 1e-30 ? q / a : (q < 0.0 ? -1e10 : 1e10);
            t1 = Math.Min(r1, r2);
            t2 = Math.Max(r1, r2);
            return true;
        }
    }
}
