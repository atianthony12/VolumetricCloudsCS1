// THE LIGHTS' AIR PASS (1.6.2, Sky/LightAir.cs): for each point another mod hands in (a lamp, a warning light),
// how much of its light reaches the camera through our clouds, rain curtains and fog -- the share the air in
// between lets through, marched from the eye TO THAT POINT (not to the scene behind it, as the cloud pass does:
// a light in front of a cloud is not hidden by it, a light inside the fog is dimmed by the fog in front of it).
// The same density functions as the cloud pass (SampleDensityLod, FogAt, RainSigma: invariant 1) and the same
// values (Sky/LightAir.cs copies the cloud material's), with fewer steps: one number a light, not a picture.
// Included by LightAir.shader and, with VC_LITE defined, by LightAirLite.shader.
//
// In:  _LightAirPoints, one texel a point (xyz world, w unused), row by row; _LightAirInfo (x the number of
//      points, y the side in texels, z 1 / the side), _LightAirEye (xyz the camera).
// Out: r = the transmittance, 1 = nothing in between (ARGB32, linear; every texel past the points is 1).

#include "UnityCG.cginc"
#include "CloudCommon.cginc"
#include "CloudMedia.cginc"

sampler2D_float _LightAirPoints;
float4 _LightAirInfo;
float4 _LightAirEye;
float _VCProbe;         // 1: write the check's colour and nothing else (as the other cloud passes)

#define LIGHT_AIR_CLOUD_STEPS 24
#define LIGHT_AIR_FOG_STEPS 20
#define LIGHT_AIR_RAIN_STEPS 12

// The ray's stretch below height y, [t0, t1] within [0, tEnd]: t0 > t1 when none.
float2 BelowHeight(float3 origin, float3 dir, float y, float tEnd)
{
    if (abs(dir.y) < 1e-5)
        return origin.y < y ? float2(0.0, tEnd) : float2(1.0, 0.0);

    float tCross = (y - origin.y) / dir.y;
    if (dir.y > 0.0)
        return float2(0.0, min(tEnd, tCross));       // climbing: below until it crosses
    return float2(max(0.0, tCross), tEnd);           // descending: below from where it crosses
}

float4 frag(v2f_img i) : SV_Target
{
    if (_VCProbe > 0.5)
        return float4(0.25, 0.5, 0.75, 1.0);

    float side = _LightAirInfo.y;
    float2 cell = floor(i.uv * side);
    float index = cell.y * side + cell.x;
    if (index >= _LightAirInfo.x)
        return float4(1, 1, 1, 1);

    float3 target = tex2Dlod(_LightAirPoints, float4((cell + 0.5) * _LightAirInfo.z, 0, 0)).xyz;
    float3 origin = _LightAirEye.xyz;
    float3 toTarget = target - origin;
    float dist = length(toTarget);
    if (dist < 0.5)
        return float4(1, 1, 1, 1);
    float3 dir = toTarget / dist;

    // ---- the clouds: through the slab, as far as the light ----
    float cloudCover = 0.0;
    {
        float dy = abs(dir.y) < 1e-4 ? (dir.y < 0 ? -1e-4 : 1e-4) : dir.y;
        float tA = (_CloudBottom - origin.y) / dy;
        float tB = (_CloudTop - origin.y) / dy;
        float tEnter = max(min(tA, tB), 0.0);
        float tExit = min(min(max(tA, tB), _MaxDistance), dist);
        if (tExit > tEnter)
        {
            float stepLen = (tExit - tEnter) / LIGHT_AIR_CLOUD_STEPS;
            float tau = 0.0;
            [loop]
            for (int k = 0; k < LIGHT_AIR_CLOUD_STEPS; k++)
            {
                float t = tEnter + stepLen * ((float)k + 0.5);
                tau += SampleDensityLod(origin + dir * t, true, CloudLod(t)) * stepLen * _Absorption;
            }
            // As the cloud pass lays them: their cover x how much of them there is at that distance.
            cloudCover = CloudPresence(tEnter) * (1.0 - exp(-tau));
        }
    }

    float tauAir = 0.0;

    // ---- the fog: the stretch below its top, as far as the light (MarchFog's sigma) ----
    if (_FogAmount > 0.001 && _FogDensity > 0.0)
    {
        float top = _FogFollowGround > 0.5 ? _FogCeiling : _FogLevel + _FogBase + _FogHeight + _FogValleyDepth;
        float2 span = BelowHeight(origin, dir, top, min(dist, _FogMaxDistance));
        if (span.y > span.x)
        {
            float strength = FogStrength();
            float stepLen = (span.y - span.x) / LIGHT_AIR_FOG_STEPS;
            [loop]
            for (int f = 0; f < LIGHT_AIR_FOG_STEPS; f++)
            {
                float t = span.x + stepLen * ((float)f + 0.5);
                float3 p = origin + dir * t;
                float layerHeight;
                float d = FogAt(p, p.y - FogGround(p), true, layerHeight);
                tauAir += strength * d * FogFade(t) * stepLen;
            }
        }
    }

    // ---- the rain curtains: under the cloud base, as far as the light (MarchRain's sigma) ----
    if (_RainAmount > 0.001 && _RainShaftDensity > 0.0)
    {
        float2 span = BelowHeight(origin, dir, _CloudBottom, min(dist, _RainMaxDistance));
        if (span.y > span.x)
        {
            float stepLen = (span.y - span.x) / LIGHT_AIR_RAIN_STEPS;
            [loop]
            for (int r = 0; r < LIGHT_AIR_RAIN_STEPS; r++)
            {
                float t = span.x + stepLen * ((float)r + 0.5);
                tauAir += RainSigma(origin + dir * t, t) * stepLen;
            }
        }
    }

    float through = (1.0 - cloudCover) * exp(-tauAir);
    return float4(through, through, through, 1.0);
}
