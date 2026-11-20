using System;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// WHEN THE CLOUD COLUMN MAP NEEDS READING AGAIN (1.6.1; pure, tools\test-columncadence.ps1). Its readback waits for the
    /// GPU's queued frames -- 3.5-86 ms of the main thread, median 69, in the author's first game with it (read once a second
    /// then: a hitch every second in a storm). Unity 5.6 has no asynchronous readback, so the map is read again only when it
    /// is no longer true, and never more often than every <see cref="MinInterval"/> s.
    /// </summary>
    /// <remarks>
    /// What keeps it true: its lookups follow the clouds' drift (CloudColumns.At), so a map stays right while the clouds only
    /// slide. What the drift does NOT cover, each as a share of what it may do before a read (the shares add, as
    /// ShadowCadence's, so several small changes together count):
    ///   the noises   the 3D noise, the Cumulus noise and cloud detail all drift at CloudWind.NoiseDrift (1.25) x the weather
    ///                map's speed, so the shapes slide through the map's clouds by 0.25 x the drift: <see cref="NoiseLimit"/> m
    ///   the cover    the cut moves every cloud's edge: <see cref="CoverLimit"/>
    ///   the density  the clouds' density now (clock, cloud button): the optical depths scale with it: <see cref="DensityLimit"/>
    ///                as |ln(now / then)|
    /// and on its own, the camera: the map covers 12 km each way round where it was drawn (moving with the clouds); strikes
    /// land up to 7 km from the camera (plus 0.5 km for a top's search), so once the camera is <see cref="CameraLimit"/> m from
    /// the map's middle the far side of the strikes' ring would leave it.
    ///
    /// Anything that changes the drawn clouds at once -- a setting (Setting.Revision), a new field or seed, the noise / Cumulus /
    /// mix / detail textures, the extras' variant switching -- makes the map untrue at once ("input"): the caller stops using
    /// it until the read. No map yet (a storm starting): at once, whatever the floor.
    ///
    /// Measured (2026-11-20, the real shader in the bench, his file, maps drawn after a drift / a cover change against the
    /// first, looked up with the drift followed): noise 47 / 94 / 188 / 375 m off the map: storm tops still inside cloud
    /// 100 / 99.7-99.8 / 98.6-98.9 / 95.0%, sideways channels inside or within 50 m under the underside 97-98 / 95 / 90 / 81-82%;
    /// cover +0.02: tops 100%, sideways 99.4-99.8%.
    ///
    /// <see cref="Ceiling"/>: once in so many seconds of UNPAUSED time a read anyway, for anything changing the clouds that is
    /// not tracked here (none known). Paused with nothing changing: never.
    /// </remarks>
    public static class ColumnCadence
    {
        /// <summary>Never two reads closer than this (s), but for the first one.</summary>
        public static double MinInterval = 6.0;

        /// <summary>How far (m) the noises may slide through the map's clouds: two of its 94 m texels.</summary>
        public static double NoiseLimit = 188.0;

        /// <summary>How far the cover may move (0..1).</summary>
        public static double CoverLimit = 0.02;

        /// <summary>|ln(density now / then)| allowed (0.1: about 10%).</summary>
        public static double DensityLimit = 0.1;

        /// <summary>How far (m) the camera may be from the map's middle (as the clouds carried it).</summary>
        public static double CameraLimit = 3000.0;

        /// <summary>Unpaused seconds after which it is read again whatever happened (0: never).</summary>
        public static double Ceiling = 300.0;

        /// <summary>How far (m) the noises slid through the weather map's clouds for a drift of (dx, dz) m (shift excluded: it moves all alike).</summary>
        public static double NoiseShift(double dx, double dz, double noiseRate)
        {
            return Math.Sqrt(dx * dx + dz * dz) * Math.Abs(noiseRate - 1.0);
        }

        /// <summary>The shares of their limits the noise, cover and density changes have used (their sum: 1 = read).</summary>
        public static double Used(double noiseShift, double coverChange, double densityThen, double densityNow)
        {
            double density = densityThen > 1e-6 && densityNow > 1e-6 ? Math.Abs(Math.Log(densityNow / densityThen)) : (densityThen == densityNow ? 0.0 : double.MaxValue);
            return noiseShift / NoiseLimit + Math.Abs(coverChange) / CoverLimit + density / DensityLimit;
        }

        /// <summary>
        /// Why the map must be read now, in a word (null: not now). <paramref name="sinceRead"/>: real seconds since the last
        /// read; <paramref name="awakeSinceRead"/>: unpaused ones.
        /// </summary>
        public static string Why(bool haveMap, bool inputChanged, double sinceRead, double awakeSinceRead,
            double noiseShift, double coverChange, double densityThen, double densityNow, double cameraOffset)
        {
            if (!haveMap)
                return "first";
            if (sinceRead < MinInterval)
                return null;
            if (inputChanged)
                return "input";
            if (cameraOffset >= CameraLimit)
                return "camera";

            double noise = noiseShift / NoiseLimit, cover = Math.Abs(coverChange) / CoverLimit;
            double used = Used(noiseShift, coverChange, densityThen, densityNow);
            if (used >= 1.0)
            {
                double density = used - noise - cover;
                return noise >= cover && noise >= density ? "wind" : cover >= density ? "cover" : "density";
            }

            if (Ceiling > 0.0 && awakeSinceRead >= Ceiling)
                return "ceiling";
            return null;
        }
    }
}
