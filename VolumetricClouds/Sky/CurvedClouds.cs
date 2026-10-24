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
    /// are in CloudCommon.cginc (the layer, the warp), CloudMedia.cginc (the haze) and CloudRaymarch
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
        /// The weather map repeats every Weather pattern size (13.5 km): marched to the horizon it came
        /// back ~15 times along a ray, as arcs fanning into the vanishing point (the far deck's first
        /// trial, 2026-10-12). From WarpFrom metres from the map's centre, all of it from WarpFull, its
        /// lookup is shifted by a smooth sum of sines (CloudCommon CurveWarp), WarpAmplitude metres each,
        /// so neighbouring repeats land a few kilometres apart. Past the corner of the map (12.2 km) plus
        /// the rain's slant: the C# twins, which only look on the map, never need it.
        /// </summary>
        public const float WarpFrom = 20000f, WarpFull = 35000f, WarpAmplitude = 1800f;

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
