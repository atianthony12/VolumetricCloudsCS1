// THE SHADOW MAP'S PASS: one sun ray a texel through the cloud slab, with the very same SampleDensity as
// the visible clouds (invariant 1). Included by CloudShadowMap.shader and, with VC_LITE defined, by
// CloudShadowMapLite.shader -- the shadows of "Simpler clouds" (Sky/CloudShaders.cs; see
// CloudRaymarchPass.cginc). _VCProbe: 1 writes one known colour, for CloudShaders' driver check.

#include "UnityCG.cginc"
#include "CloudCommon.cginc"

float3 _LightPos;
float3 _LightRight;
float3 _LightUp;
float3 _LightForward;   // direction the light travels (from sun towards ground)
float _CookieSize;
float _ShadowDepth;     // 0 = no shadow, 1 = thick cloud blocks all direct light
float _ShadowFullness;  // >1 makes thin cloud cast a fuller shadow, <1 a fainter one
float _ShadowSteps;
float _DetailShadowLod;  // cloud detail's mip level for a texel of this map (Sky/CloudDetail.cs)
float _VCProbe;          // 1: write the check's colour and nothing else (Sky/CloudShaders.cs)

// The march's most steps (CloudShadowMap.MostSteps, the same number).
#ifndef SHADOW_MOST_STEPS
#define SHADOW_MOST_STEPS 48
#endif

float4 frag(v2f_img i) : SV_Target
{
    // The driver check: a uniform, so the whole program is still built and run.
    if (_VCProbe > 0.5)
        return float4(0.25, 0.5, 0.75, 1.0);

    // The line of constant cookie-uv: every point on it is lit by this texel.
    float2 local = (i.uv - 0.5) * _CookieSize;
    float3 origin = _LightPos + _LightRight * local.x + _LightUp * local.y;
    float3 dir = _LightForward;

    // Sun at or below the horizon: nothing sensible to cast. The shadows fade out over
    // the last degrees (2.9 -> 1.15) rather than all vanishing in one frame.
    float sunUp = saturate((-dir.y - 0.02) / 0.03);
    if (sunUp <= 0.0)
        return float4(1, 1, 1, 1);

    float sTop = (_CloudTop - origin.y) / dir.y;
    float sBottom = (_CloudBottom - origin.y) / dir.y;
    float sEnter = min(sTop, sBottom);
    float sExit = max(sTop, sBottom);

    // A low sun makes the path through the slab very long; cap it so the fixed
    // step count doesn't skate over whole clouds.
    float thickness = _CloudTop - _CloudBottom;
    sExit = min(sExit, sEnter + thickness * 8.0);

    // The steps follow the slab's height since 1.6.1 (CloudShadowMap.StepsFor: about 40 m of height a step, never
    // under 20; it was 20 whatever the slab, and a 2.9 km slab of heaps cast its shadows in blocks). The loop's bound
    // is the most C# ever asks for (CloudShadowMap.MostSteps): keep the two equal.
    float steps = min(max(_ShadowSteps, 4.0), SHADOW_MOST_STEPS);
    float stepLen = (sExit - sEnter) / steps;
    float s = sEnter + stepLen * 0.5;

    float opticalDepth = 0.0;

    [loop]
    for (int n = 0; n < SHADOW_MOST_STEPS; n++)
    {
        if ((float)n >= steps)
            break;

        // (1.6.1: skipping the steps in cells of the weather map that hold no cloud -- the cloud march's empty-sky skip --
        // was tried here: the same map to the bit, but 10-15% SLOWER in the bench, Auto at 60 and 92%: the empty columns
        // were already cheap, and the look-up costs every step. Not kept.)
        opticalDepth += SampleDensityLod(origin + dir * s, true, _DetailShadowLod);
        s += stepLen;
    }

    // The visible clouds are tuned to look soft, which leaves their edges and
    // thinner parts letting most sunlight through. Shadows read better fuller than
    // that, so they get their own multiplier on the same optical depth.
    float transmittance = exp(-opticalDepth * stepLen * _Absorption * max(_ShadowFullness, 0.01));
    float lit = 1.0 - (1.0 - transmittance) * _ShadowDepth * sunUp;

    return float4(lit, lit, lit, lit);
}
