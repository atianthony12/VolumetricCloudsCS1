using HarmonyLib;
using UnityEngine;
using VolumetricClouds.Sky;

namespace VolumetricClouds.Lighting
{
    /// <summary>
    /// Records dynamic lights for the fog's light map and tags lamp-type ones for the ported
    /// halo shader; the light itself is left as the game submits it. Parameter names must
    /// match the game's, which is how Harmony binds them. The prefix returns true: returning
    /// false would skip the original and the light with it.
    /// </summary>
    [HarmonyPatch(typeof(LightSystem), nameof(LightSystem.DrawLight), new[]
    {
        typeof(LightType), typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(Color),
        typeof(float), typeof(float), typeof(float), typeof(float), typeof(bool),
    })]
    public static class DrawLightParametersPatch
    {
        // Late (2026-11-17): another mod rewrites vehicles' lights (colour, range, kind) in its own
        // DrawLight prefix, run first; the fog's light map and the tags here then see its result.
        [HarmonyPriority(Priority.Low)]
        public static bool Prefix(LightType type, Vector3 pos, Vector3 dir, ref Vector3 vel, Color color, float intensity,
            float range, float spotAngle, bool volume)
        {
            if (FogLampMap.Collecting)
                FogLampMap.Record(type, pos, dir, color, intensity, range, spotAngle);

            return HaloAdjuster.Adjust(ref vel, intensity, range, volume);
        }
    }

    /// <summary>
    /// Notes when a LAMP is about to be drawn dynamically, so the DrawLight prefix can tag it.
    /// </summary>
    /// <remarks>
    /// LightEffect.RenderEffect opens with "if (m_batchedLight &amp;&amp; !id.IsEmpty) return": a
    /// lamp-type light only continues to DrawLight when it has NO instance id. Intersection
    /// Marking Tool renders its props with a default InstanceID (and only batches them into
    /// their own layer, never the light layer), so its lamps arrive here -- through the game's
    /// instanced halo shader, which knew none of the halo settings. The argument types are
    /// spelled out so an overload added by a game update cannot make PatchAll fail for every
    /// patch in the mod. A finalizer rather than a postfix: the flag must not stay set if the
    /// original throws.
    /// </remarks>
    [HarmonyPatch(typeof(LightEffect), nameof(LightEffect.RenderEffect), new[]
    {
        typeof(InstanceID), typeof(EffectInfo.SpawnArea), typeof(Vector3), typeof(float),
        typeof(float), typeof(float), typeof(float), typeof(RenderManager.CameraInfo),
    })]
    public static class LampRenderEffectPatch
    {
        public static void Prefix(LightEffect __instance, InstanceID id)
        {
            HaloAdjuster.InLampEffect = __instance.m_batchedLight && id.IsEmpty;
        }

        public static void Finalizer()
        {
            HaloAdjuster.InLampEffect = false;
        }
    }

    /// <summary>
    /// Brackets the drawing of one vehicle (2026-11-17, the flare on vehicles' lights): every light
    /// reaching DrawLight meanwhile is the vehicle's, and HaloAdjuster tags it. The static 16-argument
    /// overload is the one every vehicle goes through, its lights and its LOD (checked by reflection
    /// 2026-11-17; the instance one, by vehicle id, calls it). A depth counter and a finalizer: nested
    /// calls cannot clear it early, and a throw cannot leave it set.
    /// </summary>
    [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.RenderInstance), new[]
    {
        typeof(RenderManager.CameraInfo), typeof(VehicleInfo), typeof(Vector3), typeof(Quaternion), typeof(Vector3),
        typeof(Vector4), typeof(Vector4), typeof(Vector3), typeof(float), typeof(Color), typeof(Vehicle.Flags),
        typeof(Vehicle.Flags2), typeof(int), typeof(InstanceID), typeof(bool), typeof(bool),
    })]
    public static class VehicleRenderPatch
    {
        public static void Prefix()
        {
            HaloAdjuster.InVehicle++;
        }

        public static void Finalizer()
        {
            if (HaloAdjuster.InVehicle > 0)
                HaloAdjuster.InVehicle--;
        }
    }

    /// <summary>
    /// Counts calls only. Confirmed unused in practice, but kept as a tripwire in case
    /// another code path starts routing through it.
    /// </summary>
    [HarmonyPatch(typeof(LightSystem), nameof(LightSystem.DrawLight), new[] { typeof(LightData) })]
    public static class DrawLightDataPatch
    {
        public static void Prefix()
        {
            HaloAdjuster.CountLightDataCall();
        }
    }
}
