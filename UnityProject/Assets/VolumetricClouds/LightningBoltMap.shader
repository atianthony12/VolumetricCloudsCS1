// The lightning bolts' distance from the camera, drawn into a screen-sized map (Sky/BoltMap.cs, 1.5.0)
// that the cloud pass reads to stop a little behind a bolt on its own pixels (LightningBoltMap.cginc).
// Not drawn to the screen: BoltMap draws it with its own matrix into its own texture.
//
// Pass 0: the bolt meshes (Sky/LightningBoltMesh.cs), widened exactly as LightningBolt.shader widens
//         them (LightningRibbon.cginc), and only their core and inner glow: the outer glow is faint,
//         and there a cloud behind still shows as before. The nearest bolt wins (BlendOp Min).
// Pass 1: the orientation self-check: reads the map at points on a bolt the way the cloud pass does,
//         and at the same points mirrored top to bottom (BoltMap.Check).
Shader "VolumetricClouds/LightningBoltMap"
{
    SubShader
    {
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            BlendOp Min
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5   // not 4.0: no Metal code at 4.0 in Unity 5.6 (see CloudRaymarch.shader)
            #include "UnityCG.cginc"
            #include "LightningRibbon.cginc"
            #include "LightningBoltMap.cginc"

            // How much of the ribbon's half-width counts as the bolt: the core is about 0.2 of it, and
            // at 0.35 the glow is down to a twentieth of the centre's brightness.
            #define BOLT_MAP_CORE 0.35

            // (2026-11-18, the author: the bolt's "black part is very visible") Only as much of the core counts
            // as the bolt is bright there: a part drawn dimmer than BOLT_MAP_FLOOR (faded forks, the path the
            // leader has not reached, the flicker's dim frames) stops nothing, from there the core widens to
            // all of BOLT_MAP_CORE at BOLT_MAP_FULL. Before, every part cut the lit rain and cloud behind it
            // whatever its brightness, and a dim one showed as a dark line.
            #define BOLT_MAP_FLOOR 0.05
            #define BOLT_MAP_FULL 0.6

            float4x4 _BoltMapGPUVP;  // the camera's view-projection for drawing into a texture (GL.GetGPUProjectionMatrix)
            float _PixelAngle;       // radians one pixel subtends
            // Per bolt (Sky/BoltMap.cs): x, y, w = its _BoltLeader (leader on, revealed share, the forks' fade),
            // z = how bright it is drawn against a full stroke (its flicker x its colour's strength).
            float4 _BoltMapLook;

            struct appdata
            {
                float4 vertex : POSITION;      // world space: the mesh is drawn with the identity matrix
                float3 tangent : NORMAL;
                float2 shape : TEXCOORD0;
                float3 branch : TEXCOORD1;     // x brightness, y along, z path position (as LightningBolt.shader)
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float across : TEXCOORD1;
                float seen : TEXCOORD2;        // how bright the bolt is drawn here, as LightningBolt.shader works it out
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 world = RibbonCorner(v.vertex.xyz, v.tangent, v.shape, _VCBoltMapEye.xyz, _PixelAngle);
                o.pos = mul(_BoltMapGPUVP, float4(world, 1.0));
                o.world = world;
                o.across = v.shape.x;

                float level = v.branch.x * (v.branch.x < 0.6 ? 1.0 - _BoltMapLook.w : 1.0);
                float shown = RibbonShown(v.branch.z, float4(_BoltMapLook.xy, 0.0, 0.0));
                o.seen = level * shown * _BoltMapLook.z;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float core = BOLT_MAP_CORE * saturate((i.seen - BOLT_MAP_FLOOR) / (BOLT_MAP_FULL - BOLT_MAP_FLOOR));
                clip(core - abs(i.across) - 1e-4);
                return float4(distance(i.world, _VCBoltMapEye.xyz), 0, 0, 0);
            }
            ENDCG
        }

        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5   // not 4.0: no Metal code at 4.0 in Unity 5.6 (see CloudRaymarch.shader)
            #include "UnityCG.cginc"
            #include "LightningBoltMap.cginc"

            #define PROBE_POINTS 8

            float4 _ProbePoints[PROBE_POINTS];  // xyz = points on a bolt's channel

            // Pixel 2k reads point k the way the cloud pass would, pixel 2k+1 the same point mirrored
            // top to bottom: 1 where a bolt is found.
            float4 frag(v2f_img i) : SV_Target
            {
                uint pixel = (uint)clamp(i.uv.x * (2 * PROBE_POINTS), 0.0, 2.0 * PROBE_POINTS - 1.0);
                float2 uv = BoltMapUV(_ProbePoints[pixel >> 1].xyz);
                if ((pixel & 1u) != 0u && uv.x >= 0.0)
                    uv.y = 1.0 - uv.y;

                return float4(BoltMapDistance(uv) < BOLT_MAP_NONE ? 1.0 : 0.0, 0, 0, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}
