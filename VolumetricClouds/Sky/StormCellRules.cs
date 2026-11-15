using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// The rules of a travelling storm (2026-11-15, the author: "lightning almost always occurs in the
    /// center of the map ... here in Beirut, strikes start at the sea, then move towards us very
    /// gradually, then move away"). Pure: tools\test-stormcells.ps1 checks them.
    /// </summary>
    /// <remarks>
    /// A storm cell is a point that rides the cloud field (it drifts with the clouds, so it stays over
    /// the cloud it was born in) plus a slow steer of its own. It is born UPWIND of where the camera
    /// looks, far enough to arrive in 3-6 minutes, aimed so its track passes over that point; its
    /// lightning starts far off (thin, dim, late rumbles), comes over (bright, cracks), and leaves
    /// downwind. Distances are measured ALONG the wind from the look-at point: negative = still
    /// upwind, coming.
    ///
    /// The lesson of 2026-09-20 (LightningPlacement's remarks) stands: lightning placed away from the
    /// view reads as none at all. So the track is aimed at the look-at point, the in-view share is
    /// kept while the cell is in view (CloudLightning), and a share of strikes stays all-round.
    /// </remarks>
    public static class StormCellRules
    {
        /// <summary>A cell's whole life is capped: a storm that stalls (no wind) still ends.</summary>
        public const float MaxAge = 1800f;

        /// <summary>It has passed this far downwind of the look-at point: it ends.</summary>
        public const float EndPast = 9000f;

        /// <summary>The look-at point moved this far from it (the player went elsewhere): it ends.</summary>
        public const float LostAt = 16000f;

        /// <summary>A storm moves at least this fast over the ground, even on a still day (m/s).</summary>
        public const float MinGroundSpeed = 6f;

        /// <summary>Its own steer across the wind, at most (m/s).</summary>
        public const float MaxSteer = 4f;

        /// <summary>How far upwind it is born: about four minutes out, 2.2-9 km.</summary>
        public static float BirthDistance(float groundSpeed)
        {
            return Mathf.Clamp(Mathf.Max(groundSpeed, MinGroundSpeed) * 240f, 2200f, 9000f);
        }

        /// <summary>Cells at once for an activity (0..4): one up to 100%, two to 250%, three above.</summary>
        public static int CellsWanted(float activity)
        {
            if (activity <= 0f)
                return 0;
            return activity <= 1f ? 1 : activity <= 2.5f ? 2 : 3;
        }

        /// <summary>
        /// 0..1, how active the cell is: it grows over its first 40 s, is strongest as it passes over
        /// the look-at point, still flashes far off (0.25), and dies away 6-9 km downwind.
        /// </summary>
        /// <param name="along">Signed distance along the wind from the look-at point, metres.</param>
        /// <param name="age">Seconds since it was born (simulation time).</param>
        public static float Envelope(float along, float age)
        {
            if (age < 0f || age >= MaxAge || along >= EndPast)
                return 0f;

            float grow = Mathf.Clamp01(age / 40f);
            float fadeOld = 1f - Mathf.Clamp01((age - (MaxAge - 120f)) / 120f);
            float passing = 0.25f + 0.75f * Mathf.Exp(-(along / 3500f) * (along / 3500f));
            float gone = 1f - Smooth(6000f, EndPast, along);
            return grow * fadeOld * passing * gone;
        }

        /// <summary>The strike rate's share for the most active cell's envelope (never 0: a far storm still flashes).</summary>
        public static float RateScale(float envelope)
        {
            return 0.35f + 0.65f * Mathf.Clamp01(envelope);
        }

        public static bool Dead(float along, float age, float distanceFromLookAt)
        {
            return age >= MaxAge || along >= EndPast || distanceFromLookAt >= LostAt;
        }

        /// <summary>
        /// The cell's own steer: what the clouds' drift lacks of <see cref="MinGroundSpeed"/> along the
        /// wind, plus a sideways part that brings it over the look-at point by the time it arrives.
        /// </summary>
        /// <param name="windX">Wind direction (unit, horizontal).</param>
        /// <param name="drift">The clouds' own speed, m/s.</param>
        /// <param name="side">Its signed offset to the side of the track at birth, metres (+ = to the wind's right).</param>
        /// <param name="distance">How far upwind it was born, metres.</param>
        public static Vector2 Steer(float windX, float windZ, float drift, float side, float distance)
        {
            float along = Mathf.Max(0f, MinGroundSpeed - Mathf.Max(0f, drift));
            float speed = Mathf.Max(drift, MinGroundSpeed);
            float travel = Mathf.Max(60f, distance / speed);
            float across = Mathf.Clamp(-side / travel, -MaxSteer, MaxSteer);

            // Right of the wind = (z, -x) in the map's plane.
            float rightX = windZ, rightZ = -windX;
            return new Vector2(windX * along + rightX * across, windZ * along + rightZ * across);
        }

        /// <summary>A spot in a cell of <paramref name="radius"/> metres, area weighted, from two randoms.</summary>
        public static Vector2 SpotIn(float centreX, float centreZ, float radius, float r1, float r2)
        {
            float r = radius * Mathf.Sqrt(Mathf.Clamp01(r1));
            float a = Mathf.Clamp01(r2) * Mathf.PI * 2f;
            return new Vector2(centreX + Mathf.Cos(a) * r, centreZ + Mathf.Sin(a) * r);
        }

        /// <summary>
        /// True where <paramref name="x"/>, <paramref name="z"/> lies in the camera's horizontal view
        /// wedge (<paramref name="halfAngle"/> radians either side of its compass heading).
        /// </summary>
        public static bool InWedge(float cameraX, float cameraZ, float headingX, float headingZ, float halfAngle,
            float x, float z)
        {
            float dx = x - cameraX, dz = z - cameraZ;
            float length = Mathf.Sqrt(dx * dx + dz * dz);
            float heading = Mathf.Sqrt(headingX * headingX + headingZ * headingZ);
            if (length < 1f || heading < 1e-4f)
                return false;
            float cos = (dx * headingX + dz * headingZ) / (length * heading);
            return cos >= Mathf.Cos(halfAngle);
        }

        /// <summary>The view wedge's half-angle across, radians, for a vertical fov (degrees) and aspect.</summary>
        public static float HalfWedge(float fov, float aspect)
        {
            float tanHalf = Mathf.Tan(Mathf.Clamp(fov, 1f, 179f) * 0.5f * Mathf.Deg2Rad);
            return Mathf.Atan(tanHalf * Mathf.Max(0.1f, aspect));
        }

        /// <summary>A strike never lands closer to the camera than this (overhead reads as a bug).</summary>
        public const float Nearest = 450f;

        /// <summary>Pushes a spot out to <see cref="Nearest"/> from the camera, along the same bearing.</summary>
        public static Vector2 KeepAway(float cameraX, float cameraZ, Vector2 spot)
        {
            float dx = spot.x - cameraX, dz = spot.y - cameraZ;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            if (d >= Nearest)
                return spot;
            if (d < 1f)
            {
                dx = 1f;
                dz = 0f;
                d = 1f;
            }
            return new Vector2(cameraX + dx / d * Nearest, cameraZ + dz / d * Nearest);
        }

        private static float Smooth(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }
    }
}
