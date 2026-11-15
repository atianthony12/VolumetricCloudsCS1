// The bolt's ribbon (Sky/LightningBoltMesh.cs): each mesh vertex sits ON the channel and is moved
// sideways, square to the view, out to the ribbon's edge. LightningBolt.shader draws the bolt with it
// and LightningBoltMap.shader the bolt's distance (1.5.0), so the two cover the same pixels.
#ifndef VC_LIGHTNING_RIBBON_INCLUDED
#define VC_LIGHTNING_RIBBON_INCLUDED

// The ribbon is this many core-widths wide: a thin white-hot core inside a soft glow.
#define GLOW_SCALE 6.0

// Radians one pixel subtends, from the camera drawing NOW (2026-11-15): its projection and the size of
// its target. The C# value was the screen's, wrong while another mod renders the camera into a target of
// another size (Dynamic Resolution); without such a mod the two are the same number.
#ifndef VC_PIXEL_ANGLE_HERE
#define VC_PIXEL_ANGLE_HERE
float PixelAngleHere()
{
    return 2.0 / max(abs(unity_CameraProjection._m11) * _ScreenParams.y, 1e-3);
}
#endif

// world: the vertex's point on the channel; tangent: the channel's direction there (unit);
// shape: x = side (-1 / +1), y = core width in metres; eye: the camera; pixelAngle: radians a pixel subtends.
float3 RibbonCorner(float3 world, float3 tangent, float2 shape, float3 eye, float pixelAngle)
{
    float3 toCamera = eye - world;
    float dist = length(toCamera);

    // Lightning is seen from kilometres away: never let the core go sub-pixel.
    float width = max(shape.y, dist * pixelAngle * 1.5);

    float3 side = cross(tangent, toCamera / max(dist, 1e-3));
    side /= max(length(side), 1e-3);

    return world + side * (width * 0.5 * GLOW_SCALE) * shape.x;
}

// (Lightning variety, 2026-11-15) The leader crawling down before the channel flashes:
// leader.x 1 while it crawls (0 = the whole channel shows: every 1.5.3 bolt, every game strike, and an
// unset uniform), leader.y the share of the path revealed. path = the vertex's path position
// (LightningBoltShape: 0 where the channel leaves the cloud, 1 at its furthest tip; 0 on 1.5.3 meshes).
// Returns 0 where the leader has not reached yet, else the brightness factor: a brighter tip.
float RibbonShown(float path, float4 leader)
{
    if (leader.x < 0.5)
        return 1.0;
    float shown = step(path, leader.y);
    float tip = smoothstep(leader.y - 0.08, leader.y, path);
    return shown * lerp(0.35, 1.8, tip);
}

#endif
