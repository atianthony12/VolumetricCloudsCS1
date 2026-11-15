// What keeps the rain off (2026-11-15, "Finer rain"; Sky/RainCover.cs): the highest cover over each
// metre round the camera -- buildings' tops, bridges' decks -- and the test of a drop against it.
#ifndef VC_RAIN_COVER_INCLUDED
#define VC_RAIN_COVER_INCLUDED

sampler2D _RainCover;      // r = the cover's height (m); -100000 = none (point filtered)
float4 _RainCoverRect;     // xy = the map's corner (world x, z), z = 1 / its size, w = 1 while on (0: no cover anywhere)

float RainCoverAt(float2 xz)
{
    float2 uv = (xz - _RainCoverRect.xy) * _RainCoverRect.z;
    float inside = (_RainCoverRect.w > 0.5 && all(uv > 0.0) && all(uv < 1.0)) ? 1.0 : 0.0;
    float h = tex2Dlod(_RainCover, float4(saturate(uv), 0, 0)).r;
    return inside > 0.5 ? h : -100000.0;
}

// 1 where a drop at p, falling along `fall` (unit, downward), came down through cover: the cover over p
// gives the height to look back up to, and the cover over the place its slanted path crossed that height
// decides -- so wind blows rain in under an overhang's edge, and a drop beside a building is never cut.
float RainCovered(float3 p, float3 fall)
{
    float roof = RainCoverAt(p.xz);
    float up = max(roof - p.y, 0.0);
    float3 back = p - fall * (up / max(-fall.y, 0.05));
    float roofThere = RainCoverAt(back.xz);
    return (up > 0.0 && roofThere > p.y + 0.1) ? 1.0 : 0.0;
}

#endif
