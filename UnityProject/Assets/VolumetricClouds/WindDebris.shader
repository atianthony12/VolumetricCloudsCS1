// What a strong wind carries near the camera (2026-10-30, Sky/WindDebris.cs): leaves torn off the
// trees, and dust (blowing snow on a winter map) lifted off dry open ground.
//
// One mesh of seeds, four vertices each --
//   POSITION   the particle's seed, 0..1 on each axis
//   TEXCOORD0  the corner, -1..1 each way
//   TEXCOORD1  three more random numbers
// -- each lives a few seconds, then starts again somewhere else (pcg4d of its id and its life's
// number). It starts at a random place in a square tile _WVClock.w wide, wrapped round the point the
// camera looks at, and flies downwind from there. It shows only where the map says it can: a leaf
// where its life STARTED is a cell with trees (it is torn off one, then blown out of the wood), dust
// where the ground is dry and open. The map (_WVMap) is the game's own: 32 m cells round the focus,
// r = trees (0..1), g = their crowns' height (m), b = the ground or water height over the focus (m),
// a = how dusty the ground is.
//
// _Part 0, LEAVES: small tumbling flakes, lifted and dropped by the gusts as they go.
// _Part 1, DUST: wide soft puffs low over the ground, growing as they drift; faded where they meet
// the ground or a wall (the depth texture), so a puff never shows its edge.
//
// Premultiplied, drawn after the air like the waterspouts' flecks, faded by the fog in front of them.
Shader "VolumetricClouds/WindDebris"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+96" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5   // not 4.0: no Metal code at 4.0 in Unity 5.6 (see CloudRaymarch.shader)
            #include "UnityCG.cginc"

            float _Part;          // 0 = leaves, 1 = dust
            float4 _WVFocus;      // xyz = the point the camera looks at, on the ground
            float4 _WVMapPlace;   // xy = the map's corner (world x, z), z = 1 / its width (m)
            sampler2D _WVMap;
            float4 _WVWind;       // xy = the way it blows (x, z), z = how fast they travel (m/s), w = how many show (0..1)
            float4 _WVClock;      // x = the clock 0..1, y = how many times it has wrapped, z = its period (s), w = the tile (m)
            float4 _WVColor;      // rgb = albedo (linear), a = opacity x what the fog in front leaves
            float4 _WVLook;       // x = size (m), y = length over width, z = flutter
            float3 _WVAmbient;
            float3 _WVSun;
            float _PixelAngle;    // radians one pixel subtends
            sampler2D_float _CameraDepthTexture;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 corner : TEXCOORD0;
                float3 extra : TEXCOORD1;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 corner : TEXCOORD0;
                float4 colour : TEXCOORD1;   // premultiplied
                float4 screen : TEXCOORD2;   // xyw = for the depth texture, z = the particle's eye depth
                float soft : TEXCOORD3;      // dust: how deep (m) its fade at the ground is; 0 = none
                float kind : TEXCOORD4;      // leaves: 0..1, which shape (over 0.6 a lobed edge)
            };

            // pcg4d (Jarzynski & Olano, "Hash Functions for GPU Rendering", JCGT 2020): four numbers 0..1.
            float4 Random4(uint a, uint b)
            {
                uint4 v = uint4(a, b, a ^ 0x9E3779B9u, b + 0x632BE5ABu);
                v = v * 1664525u + 1013904223u;
                v.x += v.y * v.w; v.y += v.z * v.x; v.z += v.x * v.y; v.w += v.y * v.z;
                v = v ^ (v >> 16u);
                v.x += v.y * v.w; v.y += v.z * v.x; v.z += v.x * v.y; v.w += v.y * v.z;
                return float4(v >> 8u) * (1.0 / 16777216.0);
            }

            float4 MapAt(float2 xz)
            {
                float2 uv = (xz - _WVMapPlace.xy) * _WVMapPlace.z;
                return tex2Dlod(_WVMap, float4(uv, 0.0, 0.0));
            }

            float SinTurns(float turns)
            {
                return sin(6.2831853 * frac(turns));
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.corner = v.corner;
                o.soft = 0.0;
                o.kind = v.vertex.z;

                float3 seed = v.vertex.xyz;
                float3 e = v.extra;
                bool dust = _Part > 0.5;

                // Its life: a whole number of lives in the clock's period (leaves 4-8 s, dust 6-13 s at
                // 64 s), counted on across the clock's wraps.
                float lives = dust ? 5.0 + floor(e.x * 6.0) : 8.0 + floor(e.x * 9.0);
                float t = lives * _WVClock.x + seed.x;
                float life = floor(t);
                float u = t - life;
                float age = u * _WVClock.z / lives;
                float4 r = Random4((uint)(seed.y * 16777215.0), (uint)(life + _WVClock.y * lives));

                // From a random place in the tile, downwind, swaying across the wind as it goes.
                float L = _WVClock.w;
                float2 dir = _WVWind.xy;
                float2 side = float2(-dir.y, dir.x);
                float speed = _WVWind.z * (dust ? 0.8 + 0.3 * r.z : 0.55 + 0.35 * r.z);
                float swayAcross = (dust ? 3.0 : 1.5 + 2.5 * r.w) * SinTurns(age * (dust ? 0.12 : 0.35) + r.w);
                float2 travel = dir * (speed * age) + side * swayAcross;
                float2 rel = r.xy * L + travel - _WVFocus.xz;
                rel = (frac(rel / L + 0.5) - 0.5) * L;
                float2 here = _WVFocus.xz + rel;
                float2 start = here - travel;

                float4 from = MapAt(start);
                float4 at = MapAt(here);
                float ground = _WVFocus.y + at.b;
                float strength = _WVWind.w;
                float share = dust ? from.a : from.r;
                float shown = saturate((share * strength - e.y) * 8.0);

                float3 centre;
                float size;
                float lengthwise;
                float3 flight;
                if (dust)
                {
                    // Low, rising a little and spreading as it drifts.
                    float rise = (1.0 + 5.0 * r.z * strength) * sqrt(u);
                    centre = float3(here.x, ground + 0.5 + rise, here.y);
                    size = (2.5 + 7.0 * u) * (0.6 + 0.8 * r.y) * (0.6 + 0.4 * strength);
                    lengthwise = size * _WVLook.y;
                    flight = float3(dir.x, 0.05, dir.y);
                }
                else
                {
                    // Torn off a crown, lifted by the gust that took it, then falling to the ground ahead.
                    float top = _WVFocus.y + from.b + from.g * (0.35 + 0.6 * e.z);
                    float lift = (1.5 + 4.0 * strength) * (0.5 + r.z) * sin(3.1415927 * u) + 1.2 * SinTurns(age * 0.8 + r.x);
                    float y = lerp(top, ground + 0.15, u * u) + lift;
                    centre = float3(here.x, max(y, ground + 0.1), here.y);
                    size = _WVLook.x * (0.7 + 0.6 * r.y);
                    lengthwise = size * _WVLook.y;
                    flight = normalize(float3(dir.x, 0.3 * SinTurns(age * 0.8 + r.x + 0.25), dir.y));
                }

                float3 toCamera = _WorldSpaceCameraPos - centre;
                float dist = length(toCamera);
                toCamera /= max(dist, 1e-3);

                // Never smaller than a pixel: wider and fainter instead; gone once far smaller.
                float pixel = dist * _PixelAngle;
                float wide = max(size, pixel);
                float thin = size / wide;
                lengthwise = max(lengthwise, pixel);

                // Leaves turn over as they fly: their width comes and goes.
                float flutter = lerp(1.0, abs(SinTurns(age * (0.8 + 1.2 * e.y) + seed.z)), _WVLook.z);

                float2 edge = abs(rel) / L;
                float fade = smoothstep(0.0, dust ? 0.2 : 0.08, u) * (1.0 - smoothstep(dust ? 0.6 : 0.88, 1.0, u)) * shown
                           * (1.0 - smoothstep(0.36, 0.5, max(edge.x, edge.y))) * smoothstep(0.5, 3.0, dist)
                           * thin * smoothstep(0.1, 0.35, thin);

                float3 across = cross(flight, toCamera);
                across /= max(length(across), 1e-3);
                float3 along = cross(toCamera, across);
                float3 world = centre + across * (v.corner.x * 0.5 * wide * flutter) + along * (v.corner.y * 0.5 * lengthwise);

                float alpha = fade * _WVColor.a * (dust ? sin(3.1415927 * u) : 1.0);
                float3 lit = _WVAmbient + _WVSun * 0.6;
                o.colour = float4(_WVColor.rgb * (dust ? 0.9 + 0.2 * r.w : 0.6 + 0.8 * e.z) * lit * alpha, alpha);

                float4 clip = mul(UNITY_MATRIX_VP, float4(world, 1.0));
                o.screen = ComputeScreenPos(clip);
                o.screen.z = -mul(UNITY_MATRIX_V, float4(world, 1.0)).z;
                o.soft = dust ? 0.4 * size : 0.0;

                // Hidden ones collapse outside clip space so they cost no fill.
                o.pos = alpha > 0.001 ? clip : float4(2, 2, 2, 1);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 c = i.corner;
                float d2 = dot(c, c);

                // A leaf (c.y along it, -1 the stem's end, 1 the tip): pointed at the tip, widest toward
                // the stem, some with a lobed edge (an oak's, a maple's); one half darker, as a leaf folds
                // along its midrib and its two halves meet the light differently. The edge a pixel
                // soft (its derivative, taken here, outside any branch).
                float y = c.y;
                float width = 0.62 * pow(saturate(1.0 - y * y), 0.75) * (1.0 - 0.25 * y);
                width *= i.kind > 0.6 ? 1.0 - 0.2 * abs(sin(y * 9.0 + 6.2831853 * i.kind)) : 1.0;
                float edge = width - abs(c.x);
                float leaf = saturate(edge / max(fwidth(edge), 1e-4) + 0.5) * (c.x > 0.0 ? 1.0 : 0.72);

                float4 colour = i.colour;
                if (i.soft > 0.0)
                {
                    // A soft puff, faded where the ground or a wall stands close behind it.
                    float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.screen)));
                    colour *= exp(-3.0 * d2) * saturate(1.0 - d2) * saturate((scene - i.screen.z) / i.soft);
                }
                else
                {
                    colour *= leaf;
                }

                return colour;
            }
            ENDCG
        }
    }

    Fallback Off
}
