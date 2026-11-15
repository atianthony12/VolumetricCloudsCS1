using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// The rules of "Spray from wheels" (2026-11-15): which vehicles throw spray, from where, how much,
    /// and how each kind of particle moves. Pure: tools\test-spray.ps1 checks them.
    /// </summary>
    public static class WheelSprayRules
    {
        public const int Droplet = 0;   // the rooster tail: drawn as short streaks along their flight
        public const int Spray = 1;     // fine spray: soft, small
        public const int Mist = 2;      // the trail that hangs behind and drifts off on the wind

        /// <summary>Slower than this (m/s) a wheel throws nothing.</summary>
        public const float MinSpeed = 4f;

        /// <summary>
        /// 0..1.5: how much a vehicle throws at a speed (m/s) on a road this wet (0..1). Rises from
        /// <see cref="MinSpeed"/> to 20 m/s (72 km/h), more above; nothing on a road under 15% wet.
        /// </summary>
        public static float Amount(float speed, float wetness)
        {
            if (speed <= MinSpeed || wetness <= 0.15f)
                return 0f;
            float s = Mathf.Clamp((speed - MinSpeed) / 16f, 0f, 1.5f);
            return s * Mathf.Clamp01((wetness - 0.15f) / 0.45f);
        }

        /// <summary>Particles a second, per wheel, of each kind at amount 1.</summary>
        public static float Rate(int kind)
        {
            return kind == Droplet ? 55f : kind == Spray ? 30f : 7f;
        }

        /// <summary>How long a particle of each kind lives, from a random 0..1 (seconds).</summary>
        public static float Life(int kind, float r)
        {
            return kind == Droplet ? Mathf.Lerp(0.5f, 0.9f, r) : kind == Spray ? Mathf.Lerp(0.6f, 1f, r) : Mathf.Lerp(1.5f, 3f, r);
        }

        /// <summary>
        /// The two rear wheels' contact points: half the wheel base behind the middle along the way it
        /// drives (its velocity, so a reversing vehicle comes out right), half the gauge to each side.
        /// </summary>
        public static void RearWheels(Vector3 position, Vector3 velocity, float wheelBase, float gauge,
            out Vector3 left, out Vector3 right)
        {
            Vector3 forward = new Vector3(velocity.x, 0f, velocity.z);
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            Vector3 side = new Vector3(forward.z, 0f, -forward.x);
            Vector3 rear = position - forward * (wheelBase * 0.5f);
            left = rear - side * (gauge * 0.5f);
            right = rear + side * (gauge * 0.5f);
        }

        /// <summary>
        /// A new particle's world velocity off a wheel of a vehicle moving at <paramref name="velocity"/>:
        /// thrown back and up off the tyre (so it trails behind, slower than the vehicle), spread a little.
        /// </summary>
        public static Vector3 Launch(int kind, Vector3 velocity, float r1, float r2, float r3)
        {
            float speed = new Vector2(velocity.x, velocity.z).magnitude;
            Vector3 forward = speed > 1e-3f ? new Vector3(velocity.x, 0f, velocity.z) / speed : Vector3.forward;
            Vector3 side = new Vector3(forward.z, 0f, -forward.x);
            float spread = (r1 - 0.5f) * 2f;
            switch (kind)
            {
                case Droplet:
                    return velocity - forward * (speed * Mathf.Lerp(0.2f, 0.32f, r2)) + Vector3.up * (speed * Mathf.Lerp(0.12f, 0.22f, r3))
                         + side * (spread * speed * 0.06f);
                case Spray:
                    return velocity * 0.55f + Vector3.up * (speed * Mathf.Lerp(0.04f, 0.1f, r3)) + side * (spread * speed * 0.05f);
                default:
                    return velocity * 0.35f + Vector3.up * Mathf.Lerp(0.2f, 0.6f, r3) + side * (spread * 0.6f);
            }
        }

        /// <summary>
        /// One step of a particle's flight: droplets fall and slow in the air; spray slows quickly; mist
        /// is caught by the wind (<paramref name="wind"/>, m/s) and drifts on it, rising a little.
        /// </summary>
        public static Vector3 Step(int kind, Vector3 velocity, Vector3 wind, float dt)
        {
            switch (kind)
            {
                case Droplet:
                    velocity += Vector3.down * (9.81f * dt);
                    return Vector3.Lerp(wind, velocity, Mathf.Exp(-1.2f * dt)) + Vector3.zero;
                case Spray:
                    velocity += Vector3.down * (3f * dt);
                    return Vector3.Lerp(wind, velocity, Mathf.Exp(-3f * dt));
                default:
                    Vector3 drift = wind + Vector3.up * 0.15f;
                    return Vector3.Lerp(drift, velocity, Mathf.Exp(-1.6f * dt));
            }
        }

        /// <summary>A particle's size (m across) over its life (0..1): mist grows from 0.6 to 2.5 m.</summary>
        public static float Size(int kind, float life)
        {
            float t = Mathf.Clamp01(life);
            return kind == Droplet ? 0.035f : kind == Spray ? Mathf.Lerp(0.25f, 0.6f, t) : Mathf.Lerp(0.6f, 2.5f, Mathf.Sqrt(t));
        }

        /// <summary>A particle's opacity over its life: in quickly, out slowly.</summary>
        public static float Opacity(int kind, float life)
        {
            float t = Mathf.Clamp01(life);
            float fade = Mathf.Clamp01(t * 8f) * (1f - t) * (1f - t);
            return fade * (kind == Droplet ? 0.55f : kind == Spray ? 0.22f : 0.12f);
        }
    }
}
