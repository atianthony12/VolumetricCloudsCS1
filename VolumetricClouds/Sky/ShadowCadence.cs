using System;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// WHEN THE SHADOW MAP NEEDS DRAWING AGAIN (2026-11-17): how far the cloud shadows on the ground would be off if
    /// the map last drawn stayed up -- by the wind, by the sun's turn, by the cover -- so CloudShadowMap draws only when
    /// that reaches an eighth of one of its texels (and at most at its rate, and at least once a second). Pure (no
    /// Unity), so tools\test-shadowcadence.ps1 compiles this file.
    /// </summary>
    /// <remarks>
    /// The map is drawn in the sun's own frame: texel (x, y) holds the line through anchor + x right + y up along the
    /// sun's direction, and the game reads it back through the sun's frame of the moment (Unity's directional cookie).
    /// So a turned sun moves the shadows twice: the line through a point leans another way (height / sin^2 of the
    /// elevation, CloudShadowMap.SunMovedShadows' measure) AND the frame the map is read through turns about the
    /// anchor, which far from the map's middle is the larger (a few km x the turn). <see cref="SunShift"/> measures
    /// both, exactly, at the map's middle and corners, at the slab's bottom and top.
    /// </remarks>
    public static class ShadowCadence
    {
        /// <summary>The share of a texel the shadows may lag before the map is drawn again.</summary>
        public const double MovedShare = 0.125;

        /// <summary>The sun's three directions, world space (Unity's right, up, forward of the sun's transform).</summary>
        public struct Frame
        {
            public double Rx, Ry, Rz, Ux, Uy, Uz, Fx, Fy, Fz;

            public Frame(double rx, double ry, double rz, double ux, double uy, double uz, double fx, double fy, double fz)
            {
                Rx = rx; Ry = ry; Rz = rz;
                Ux = ux; Uy = uy; Uz = uz;
                Fx = fx; Fy = fy; Fz = fz;
            }
        }

        /// <summary>
        /// How far (m, horizontally) a cloud seen through the map drawn with <paramref name="drawn"/> is from where it
        /// stands when read through <paramref name="now"/>: the largest over the map's middle and its four corners
        /// (ground points <paramref name="reach"/> from the anchor each way, at height 0) at the clouds' bottom and top.
        /// 0 when the sun is under <paramref name="lowestSin"/> both times (nothing casts, the map is plain); a huge
        /// number when it crossed it.
        /// </summary>
        public static double SunShift(Frame drawn, Frame now, double reach, double bottom, double top, double lowestSin)
        {
            bool castDrawn = -drawn.Fy >= lowestSin;
            bool castNow = -now.Fy >= lowestSin;
            if (!castDrawn && !castNow)
                return 0.0;
            if (castDrawn != castNow)
                return double.MaxValue;

            double worst = 0.0;
            for (int k = 0; k < 5; k++)
            {
                double px = k == 0 ? 0.0 : ((k & 1) != 0 ? reach : -reach);
                double pz = k == 0 ? 0.0 : ((k & 2) != 0 ? reach : -reach);
                for (int level = 0; level < 2; level++)
                {
                    double h = level == 0 ? bottom : top;
                    double d = Shift(drawn, now, px, pz, h);
                    if (d > worst)
                        worst = d;
                }
            }

            return worst;
        }

        /// <summary>The same for one ground point (px, 0, pz) and one height.</summary>
        public static double Shift(Frame drawn, Frame now, double px, double pz, double h)
        {
            // Where the cookie of NOW reads the ground point: (x, y) in the sun's frame.
            double x = px * now.Rx + pz * now.Rz;
            double y = px * now.Ux + pz * now.Uz;

            // The map's line at (x, y), as drawn: through x right + y up along the drawn sun, at height h.
            double ox = x * drawn.Rx + y * drawn.Ux;
            double oy = x * drawn.Ry + y * drawn.Uy;
            double oz = x * drawn.Rz + y * drawn.Uz;
            double s0 = (h - oy) / drawn.Fy;
            double ax = ox + s0 * drawn.Fx;
            double az = oz + s0 * drawn.Fz;

            // The ground point's true line to the sun of now, at height h.
            double s1 = h / now.Fy;
            double bx = px + s1 * now.Fx;
            double bz = pz + s1 * now.Fz;

            double dx = ax - bx, dz = az - bz;
            return Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// How far (m) the clouds moved with the wind between two drifts: the weather map moves by the drift plus the
        /// cloud position shift, the 3D noise by <paramref name="noiseRate"/> x the drift plus the shift
        /// (CloudWind.NoiseDrift); the larger of the two.
        /// </summary>
        public static double DriftShift(double dx, double dz, double shiftX, double shiftZ, double noiseRate)
        {
            double wx = dx + shiftX, wz = dz + shiftZ;
            double nx = dx * noiseRate + shiftX, nz = dz * noiseRate + shiftZ;
            return Math.Max(Math.Sqrt(wx * wx + wz * wz), Math.Sqrt(nx * nx + nz * nz));
        }

        /// <summary>The lag allowed, in metres, for a map of <paramref name="resolution"/> texels over <paramref name="size"/> metres.</summary>
        public static double Limit(double size, int resolution)
        {
            return size / Math.Max(1, resolution) * MovedShare;
        }
    }
}
