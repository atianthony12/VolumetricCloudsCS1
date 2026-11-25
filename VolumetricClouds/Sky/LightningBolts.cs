using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// THIS FRAME'S LIGHTNING BOLTS, for other mods' reflections (2026-11-18, the author: "Lightning strikes
    /// should reflect in Real Glass's water"). Other mods read this by reflection (assembly
    /// "VolumetricClouds"); nothing here references them.
    /// </summary>
    /// <remarks>
    /// AN API, like <see cref="WeatherWind"/> and <c>Lighting.LampFlare</c>: these names, signatures and
    /// meanings never change. A new meaning is a new member and <see cref="ApiVersion"/> 2 (readers check
    /// &gt;= 1).
    ///
    /// The bolts are world-space meshes (CloudLightning draws them with Graphics.DrawMesh for the screen,
    /// in a transparent queue just before the clouds, additive, no depth written), so a mod that renders
    /// its own picture of the sky (a reflection cube) sees none of them. <see cref="DrawInto"/> adds them,
    /// each with its own colour and flicker, to that mod's command buffer: drawn there BEFORE the clouds,
    /// as on screen, so the cloud pass lays its cloud, rain and fog over them. Their width follows the
    /// camera drawing them (LightningRibbon.cginc PixelAngleHere): a small cube face gets wider ribbons,
    /// never sub-pixel ones. The cloud pass's own flashes are on the cloud material itself and need nothing.
    /// </remarks>
    public static class LightningBolts
    {
        public const int ApiVersion = 1;

        private sealed class Bolt
        {
            public Mesh Mesh;
            public readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
        }

        private static readonly List<Bolt> s_bolts = new List<Bolt>();
        private static int s_count;
        private static int s_frame = -10;
        private static Material s_material;

        /// <summary>How many bolts show now (0 when none, or lightning is not running; 0 too while off after a game update, GameUpdate).</summary>
        public static int Count
        {
            get { return !GameUpdate.Off && Time.frameCount - s_frame <= 1 ? s_count : 0; }
        }

        /// <summary>
        /// Adds this frame's bolts to <paramref name="buffer"/> (DrawMesh in world space, the bolt's own
        /// material and per-bolt values) and returns how many. Draw them after the sky and before the
        /// clouds. Nothing is added when none show.
        /// </summary>
        public static int DrawInto(CommandBuffer buffer)
        {
            int count = Count;
            if (buffer == null || s_material == null)
                return 0;
            int drawn = 0;
            for (int i = 0; i < count; i++)
            {
                // A strike that ended since its last draw has had its mesh destroyed.
                if (s_bolts[i].Mesh == null)
                    continue;
                buffer.DrawMesh(s_bolts[i].Mesh, Matrix4x4.identity, s_material, 0, 0, s_bolts[i].Block);
                drawn++;
            }
            return drawn;
        }

        /// <summary>CloudLightning, once a frame before it draws: forgets the last frame's bolts.</summary>
        internal static void Begin(Material material)
        {
            s_material = material;
            s_count = 0;
            s_frame = Time.frameCount;
        }

        /// <summary>CloudLightning, for each bolt it draws on screen, with the same values.</summary>
        internal static void Add(Mesh mesh, Vector4 colour, float intensity, Vector4 leader, int idColour, int idIntensity, int idLeader)
        {
            if (s_count == s_bolts.Count)
                s_bolts.Add(new Bolt());
            Bolt bolt = s_bolts[s_count++];
            bolt.Mesh = mesh;
            bolt.Block.Clear();
            bolt.Block.SetVector(idColour, colour);
            bolt.Block.SetFloat(idIntensity, intensity);
            bolt.Block.SetVector(idLeader, leader);
        }

        /// <summary>The city closes: nothing is left to draw.</summary>
        internal static void Clear()
        {
            s_count = 0;
            s_material = null;
            for (int i = 0; i < s_bolts.Count; i++)
                s_bolts[i].Mesh = null;
        }
    }
}
