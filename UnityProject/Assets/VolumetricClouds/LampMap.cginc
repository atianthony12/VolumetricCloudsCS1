// The city's lights for what falls through the air (2026-11-15, "Finer rain": streaks and splashes lit by
// street lamps, windows' lamps and the cars' headlight beams). The SAME map the fog reads (Sky/FogLampMap.cs,
// FogLamps.shader; CloudRaymarchPass.cginc LampLightAt is its twin there -- change one, change the other):
// centred on the camera, rgb = light, a = luminance x the height it comes from, Gaussian footprints so the
// height comes back here. Globals; _VCLampParams.z is 1 only while the map is drawn this frame.
#ifndef VC_LAMP_MAP_INCLUDED
#define VC_LAMP_MAP_INCLUDED

sampler2D _VCLampMap;      // rgb = light, a = luminance x the altitude it comes from
float4 _VCLampMapRect;     // xy = minimum corner (world xz), z = 1 / size
float4 _VCLampParams;      // x = the fog's strength, y = 3 / R^2, z = 1 while on, w = how far out

// The lamps' light at p (0 while the map is off), before any strength. Usable in a vertex function.
float3 CityLightAt(float3 p)
{
    // One exit (vertex functions here keep to that: CLAUDE.md, the 1.5.0 GL bug).
    float on = _VCLampParams.z > 0.5 ? 1.0 : 0.0;
    float2 uv = (p.xz - _VCLampMapRect.xy) * _VCLampMapRect.z;
    float2 inside = saturate(min(uv, 1.0 - uv) * 20.0);
    float edge = inside.x * inside.y;
    float4 m = tex2Dlod(_VCLampMap, float4(uv, 0, 0));
    float lum = dot(m.rgb, float3(0.2126, 0.7152, 0.0722));
    float dz = p.y - m.a / max(lum, 1e-6);
    return m.rgb * (exp(-dz * dz * _VCLampParams.y) * edge * on);
}

#endif
