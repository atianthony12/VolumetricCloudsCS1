// The city's gusts for the rain (2026-11-15, "Wind on the rain"): the SAME field the trees bend in
// (Sky/TreeWind.cs GustAt, Trees.cginc VWGust): cells of 160 m riding downwind, two octaves of value
// noise. A third twin: change one, change all three. Its own names and uniform, so Trees.cginc and the
// tree shaders stay byte-identical.
#ifndef VC_RAIN_GUST_INCLUDED
#define VC_RAIN_GUST_INCLUDED

// xy the gust field's drift (m, wrapped to its tile in C#), z 1 / its cell (m), w the gusts' strength (0..1).
float4 _RainGust;

#define RAIN_GUST_TILE 128.0

uint2 RainPcg2d(uint2 v)
{
    v = v * 1664525u + 1013904223u;
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v = v ^ (v >> 16u);
    v.x += v.y * 1664525u;
    v.y += v.x * 1664525u;
    v = v ^ (v >> 16u);
    return v;
}

float RainHash(float2 cell)
{
    return (float)(RainPcg2d((uint2)cell).x >> 8u) * (1.0 / 16777216.0);
}

float RainValueNoise(float2 p, float tile)
{
    float2 i = floor(p);
    float2 f = p - i;
    float2 u = f * f * (3.0 - 2.0 * f);
    float2 i0 = i - tile * floor(i / tile);
    float2 i1 = i0 + 1.0;
    i1 = i1 >= tile ? i1 - tile : i1;
    float a = RainHash(i0);
    float b = RainHash(float2(i1.x, i0.y));
    float c = RainHash(float2(i0.x, i1.y));
    float d = RainHash(i1);
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// 0 a lull .. 1 a gust, at a place.
float RainGustAt(float2 xz)
{
    float2 p = (xz - _RainGust.xy) * _RainGust.z;
    float g = 0.65 * RainValueNoise(p, RAIN_GUST_TILE) + 0.35 * RainValueNoise(p * 2.5, RAIN_GUST_TILE * 2.5);
    return smoothstep(0.3, 0.75, g);
}

#endif
