// Rain streaks near the camera, replacing the game's camera-locked rain.
//
// What made the game's rain read as a filter stuck on the lens, and what this does instead:
//   - its streaks were attached to the camera     -> these live in WORLD space. The mesh is a
//                                                    cloud of random seeds; each is wrapped
//                                                    into a box around the camera, so moving
//                                                    the camera moves THROUGH the rain
//   - it rained everywhere at one strength        -> each drop asks SampleRain (CloudCommon)
//                                                    about its own spot: showers have edges
//   - it drew over everything                     -> depth tested against the scene
//   - it was there at every zoom level            -> fades out with camera height (_DropFade);
//                                                    from high up the rain CURTAINS in
//                                                    CloudRaymarch carry the look
//   - it ignored the wind                         -> falls along _FallDirection, the same
//                                                    slant the curtains lean at
//
// Mesh layout (built in RainDrops.cs), four vertices per drop:
//   POSITION   the drop's seed, 0..1 on each axis
//   TEXCOORD0  x = side (-1 / +1), y = along the streak (0 head .. 1 tail)
//   TEXCOORD1  x = rank (drops with rank above the local rain strength are hidden, so heavier
//                  rain is MORE drops, not brighter ones), y = layer (0 near, 1 far),
//              z = speed variation
//
// 2026-11-19 (the author: "'Wind on rain' is way too aggressive, like the rain is becoming horizontal ...
// make it more dynamic/random ... around streetlights the game's vanilla rain particles still show, not
// ours"). Every number comes from Sky/RainLook.cs through Sky/RainDrops.cs.
//   - THE WIND: the mean lean is given whole (_RainWind.xz, a tangent with a ceiling); a gust changes it
//     by a share and swings it to the side. A drop always MOVES the way its streak points: the mean lean
//     rides the carry (_RainWindOffset), what a gust adds is a shear about the camera's height (a drop
//     lower down is further downwind, so falling moves it sideways at exactly that lean), and an eddy is
//     a winding path whose streak is the path's own direction (one function, read at the drop and a
//     metre lower). The old sideways push of up to 12-50 m with nothing in the streak to match is gone.
//   - LIFE (with Finer rain, _RainLife2.x > 0): sheets of denser and thinner rain riding downwind, eddies
//     that turn the streaks off parallel, a wider spread of speed, length and brightness.
//   - LIGHT: the lamps' and the lightning's light is ADDED (premultiplied blend), whatever the streaks'
//     opacity: rain in a street light's glow is seen because it is lit. With no light the picture is the
//     old blend's (colour x alpha over the scene).
//   - A SECOND DRAW of the same mesh (_RainLayer, a MaterialPropertyBlock) fills the middle distance.
//
// SNOW (1.2.1, winter maps, _Snow = 1): the same seeds as round, soft flakes -- a snowflake
// falls at a metre or so a second, too slowly to streak -- that drift from side to side, each on
// its own rhythm, by a phase that moves only as the snow falls (so it stops when the game pauses).
Shader "VolumetricClouds/RainDrops"
{
    SubShader
    {
        // After the clouds (Transparent+100): streaks are in front of the curtains.
        Tags { "Queue" = "Transparent+110" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest LEqual
            // Premultiplied: rgb = colour x alpha + the light the drop throws back, over the scene x
            // (1 - alpha). With no light it is SrcAlpha / OneMinusSrcAlpha's picture.
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5   // not 4.0: no Metal code at 4.0 in Unity 5.6 (see CloudRaymarch.shader)
            #include "UnityCG.cginc"
            #include "CloudCommon.cginc"
            #include "RainGust.cginc"
            #include "LampMap.cginc"
            #include "RainCover.cginc"

            // "Wind on the rain". xz = the mean lean, a tangent (metres sideways per metre of fall) for a
            // medium drop, y = the share of it a gust adds and a lull takes, w 1 = on. 0 (unset) = the
            // streaks along _FallDirection, carried by _FallOffset alone.
            float4 _RainWind;
            // xz = how far the air has carried the rain so far (m, wrapped in C# to whole boxes), y the
            // heavier bands' strength in gusts, w = the share of the lean a gust swings it to the side.
            float4 _RainWindOffset;
            // "Finer rain": x 1 = drop sizes (small, medium, large) and a few drops right at the lens; y = how
            // much of the city's lamp light (LampMap.cginc) a drop throws back, z = of the lightning's;
            // w = both, by the Streak brightness row (RainLook.LightShare).
            float4 _RainDetail;
            // The rain's life (2026-11-19, with Finer rain). xy = the sheets' drift (m, wrapped to
            // RAIN_SHEET_WRAP in C#), z = the eddies' clock (turns, 0..1), w = an eddy's reach (m).
            float4 _RainLife;
            // x = how deep the sheets cut (the share of drops a thin stretch loses; 0 = no life at all),
            // zw = metres the sheets lean downwind per metre of height.
            float4 _RainLife2;
            // A second draw of the same mesh: xyz = added to every seed, w 1 = no drops at the lens.
            float4 _RainLayer;

            // The lightning (Sky/CloudLightning.ApplyTo, as the cloud pass gets it).
            #define RAIN_MAX_FLASHES 4
            float _FlashCount;
            float4 _FlashPos[RAIN_MAX_FLASHES];     // xyz world position
            float4 _FlashColor[RAIN_MAX_FLASHES];   // rgb, already scaled by this frame's flicker

            // The eddies ride with the rain the air carries: 17 m cells, 120 of them = the carry's wrap
            // (RainDrops.WindWrap 2040 m). The sheets: 14 m x 160 = 70 m x 32 = RainLook.SheetWrap.
            #define RAIN_EDDY_CELL 17.0
            #define RAIN_EDDY_TILE 120.0
            #define RAIN_EDDY_FALL (6.2832 / 24.0)   // one turn of the path every 24 m of fall
            #define RAIN_EDDY_STEP 1.0

            // Where an eddy has moved a drop, sideways (m). The SAME function gives the streak its
            // direction (read again a step lower), so a drop moves along its own streak.
            float2 RainEddy(float2 riding, float height)
            {
                float n = RainValueNoise(riding * (1.0 / RAIN_EDDY_CELL), RAIN_EDDY_TILE);
                float a = 6.2832 * (n + _RainLife.z);
                float h = height * RAIN_EDDY_FALL;
                // Whole multiples of the clock only: it wraps at 1.
                return _RainLife.w * float2(sin(h + a), cos(h * 0.79 + 2.0 * a));
            }

            // 0 a thin stretch .. 1 a dense one: sheets a few houses wide inside slower, wider squalls.
            float RainSheets(float2 place, float height)
            {
                float2 q = place - _RainLife.xy + _RainLife2.zw * height;
                float fine = RainValueNoise(q * (1.0 / 14.0), 160.0);
                float wide = RainValueNoise(q * (1.0 / 70.0) + 31.7, 32.0);
                return smoothstep(0.25, 0.75, 0.62 * fine + 0.38 * wide);
            }

            // The lightning on the rain: every flash lights the drops for kilometres round it.
            float3 RainFlash(float3 p)
            {
                float3 sum = 0;
                for (int k = 0; k < RAIN_MAX_FLASHES; k++)
                {
                    float3 d = p - _FlashPos[k].xyz;
                    float on = (float)k < _FlashCount ? 1.0 : 0.0;
                    sum += _FlashColor[k].rgb * (on / (1.0 + dot(d, d) * (1.0 / (1500.0 * 1500.0))));
                }
                return sum;
            }

            // Radians one pixel subtends, from the camera drawing now (see LightningRibbon.cginc).
            #ifndef VC_PIXEL_ANGLE_HERE
            #define VC_PIXEL_ANGLE_HERE
            float PixelAngleHere()
            {
                return 2.0 / max(abs(unity_CameraProjection._m11) * _ScreenParams.y, 1e-3);
            }
            #endif

            float3 _BoxNear;          // size of the box the near layer wraps in, metres
            float3 _BoxFar;
            float3 _FallOffset;       // how far the rain has fallen so far (wrapped by the C# side)
            float3 _FallDirection;    // unit vector, downwards and downwind
            float4 _DropColor;        // rgb colour, a = opacity
            float _DropFade;          // 0..1, from camera height
            float _DropDensity;       // 0..1+, scales how many drops show
            float _PixelAngle;        // radians one pixel subtends
            float4 _StreakNear;       // x = length, y = width (metres)
            float4 _StreakFar;
            float _Snow;              // 1 on a winter map: flakes, not streaks
            float _SwayPhase;         // radians: how far the flakes' drift has gone (CloudRain.SwayPhase)
            float _SnowSway;          // metres a flake drifts to each side

            struct appdata
            {
                float4 vertex : POSITION;
                float2 corner : TEXCOORD0;
                float3 random : TEXCOORD1;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 corner : TEXCOORD0;
                float alpha : TEXCOORD1;
                float3 light : TEXCOORD2;   // the city's lamps at the drop (Finer rain; 0 otherwise)
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.corner = v.corner;

                float isFar = v.random.y;
                float3 box = lerp(_BoxNear, _BoxFar, isFar);
                float2 streak = lerp(_StreakNear.xy, _StreakFar.xy, isFar);

                // Finer rain: a drop's size from its speed variation -- small ones thin and slow, large
                // ones wide and fast (they fall faster, and lean less in the wind) -- and one near drop
                // in seven falls in a small box right in front of the lens.
                bool detail = _RainDetail.x > 0.5;
                bool alive = _RainLife2.x > 0.0;
                float size = detail ? (v.random.z < 0.45 ? 0.6 : (v.random.z > 0.88 ? 1.6 : 1.0)) : 1.0;
                bool lens = detail && isFar < 0.5 && _RainLayer.w < 0.5 && frac(v.random.x * 7.31) < 0.14;
                box = lens ? float3(12.0, 9.0, 12.0) : box;
                streak *= float2(detail ? lerp(0.85, 1.25, saturate(size - 0.6)) : 1.0, size);

                // The drop's own numbers for the life: how bright, which sheets hide it, how long.
                float ownBright = frac(v.random.z * 91.7 + v.random.x * 13.3);
                float ownSheet = frac(v.random.x * 57.9 + v.random.z * 31.1);
                float ownLength = frac(v.random.z * 23.7 + v.vertex.x * 17.9);
                streak.x *= alive ? lerp(0.6, 1.5, ownLength) : 1.0;

                // How fast it falls beside a medium drop: the small ones slow, the large ones fast.
                float speed = alive ? lerp(0.7, 1.3, v.random.z) : 0.85 + 0.3 * v.random.z;

                // World-anchored: the seed plus the fall so far is a WORLD position; wrapping
                // it relative to the camera keeps the drop in the box without tying it to it.
                // With wind on the rain the air carries every drop the same way (_RainWindOffset).
                bool windy = _RainWind.w > 0.5;
                float3 fallen = _FallOffset * speed;
                fallen = windy ? float3(_RainWindOffset.x, fallen.y, _RainWindOffset.z) : fallen;
                float3 rel = (frac(v.vertex.xyz + _RainLayer.xyz + (fallen - _WorldSpaceCameraPos) / box + 0.5) - 0.5) * box;

                // Where it is before a gust or an eddy moves it: what the box's fade, the gust and the
                // sheets are read at (so a wrap is always hidden, and nothing chases its own tail).
                float3 home = rel;
                float2 place = _WorldSpaceCameraPos.xz + home.xz;
                float height = _WorldSpaceCameraPos.y + home.y;

                // The lean: metres sideways per metre of fall. Without the wind, the curtains' slant
                // (every drop's carry is its own fall x the slant, so it is the same for all).
                float2 lean = _FallDirection.xz / max(-_FallDirection.y, 1e-3);
                float gust = 0.5;
                if (windy)
                {
                    // The gust where the drop is: more lean in it, less in a lull, and a swing to the
                    // side by a slower pattern riding the same field.
                    gust = RainGustAt(place);
                    float swing = RainValueNoise((place - _RainGust.xy) * _RainGust.z * 1.25 + 53.1, RAIN_GUST_TILE * 1.25);
                    float2 mean = _RainWind.xz;
                    float2 local = mean * (1.0 + _RainWind.y * (2.0 * gust - 1.0))
                                 + float2(-mean.y, mean.x) * (_RainWindOffset.w * (2.0 * swing - 1.0));
                    // The air carries every drop alike, so a slow drop leans more and a fast one less.
                    lean = local / speed;
                    // The mean rides the carry. What the gust adds is a shear about the camera's height:
                    // lower is further downwind, so falling carries the drop sideways at this very lean.
                    rel.xz -= (local - mean) / speed * home.y;
                }

                if (alive)
                {
                    // Eddies, riding with the rain the air carries: the drop winds down a path, and its
                    // streak is that path's direction (the same function a step lower).
                    float2 riding = place - fallen.xz;
                    float2 here = RainEddy(riding, height);
                    float2 below = RainEddy(riding, height - RAIN_EDDY_STEP);
                    rel.xz += here;
                    lean += (below - here) * (1.0 / RAIN_EDDY_STEP);
                    // And a little of its own: no two streaks quite parallel.
                    lean += (float2(ownBright, ownSheet) - 0.5) * 0.05;
                }

                // Never flatter than RainLook.SteepestDrawn (a tangent of 1.75: 60 degrees).
                lean *= min(1.0, 1.75 / max(length(lean), 1e-4));
                float3 fall = (windy || alive) ? normalize(float3(lean.x, -1.0, lean.y)) : _FallDirection;
                // A slanted streak is a longer path in the same moment.
                streak.x *= windy ? 1.0 / max(-fall.y, 0.4) : 1.0;

                // A snowflake drifts from side to side as it falls, each on its own rhythm: an
                // ellipse of its own shape and turn. Only whole multiples of the phase, which wraps
                // every sway -- a fraction of it would jump at every wrap.
                if (_Snow > 0.5)
                {
                    float a = _SwayPhase + v.random.z * 6.2832;
                    rel.xz += float2(sin(a + v.vertex.x * 40.0), sin(a + v.vertex.z * 40.0)) * _SnowSway;
                }

                float3 head = _WorldSpaceCameraPos + rel;

                // EVERYTHING that decides whether and how strongly this drop shows is computed
                // from the head, which all four vertices share. Deciding per corner would let
                // the corners disagree, and a half-collapsed quad is a triangle across the
                // whole screen.
                float dist = length(rel);
                float3 toCamera = -rel / max(dist, 1e-3);

                // Never thinner than a pixel: a 1 cm streak 60 m away would otherwise flicker
                // in and out of existence. Widen it and thin its opacity to match.
                float width = max(streak.y, dist * PixelAngleHere());
                float widened = streak.y / width;

                // A flake is as long as it is wide, a pixel at the least like its width.
                if (_Snow > 0.5)
                    streak.x = width;

                // How hard it is raining where this drop is. Heavier rain shows MORE drops.
                float local = SampleRain(head);
                // In a gust the rain comes in heavier bands (0 = as before).
                float bands = windy ? 1.0 + _RainWindOffset.y * (gust - 0.5) * 1.6 : 1.0;
                float shown = saturate((local * _DropDensity * bands - v.random.x) * 8.0);
                // Finer rain: none under a roof or a bridge (the cover map; off = 0 everywhere).
                shown *= 1.0 - RainCovered(head, fall);

                // The sheets: a thin stretch loses up to _RainLife2.x of its drops, whatever the Rain
                // streaks row (which, turned up, shows every drop everywhere); a gust thickens them.
                float bright = 1.0;
                if (alive)
                {
                    float sheet = saturate(RainSheets(place, height) + (windy ? _RainWindOffset.y * (gust - 0.5) : 0.0));
                    float kept = 1.0 - _RainLife2.x * (1.0 - sheet);
                    shown *= saturate((kept - ownSheet * 0.95) * 6.0);
                    // Mostly faint drops and a few that catch the light: 0.6 to 1.8, 1 on average.
                    bright = 0.6 + 1.2 * ownBright * ownBright;
                }

                // Fade towards the box faces so wrapping never pops, and right at the lens so a
                // drop never smears across the screen.
                float3 edge = abs(home) / (box * 0.5);
                float boxFade = 1.0 - smoothstep(0.75, 1.0, max(edge.x, max(edge.y, edge.z)));
                float lensFade = smoothstep(0.4, 2.0, dist);

                float seen = shown * boxFade * lensFade * widened * _DropFade * bright;
                // Never over 1: the blend would darken what is behind.
                o.alpha = min(1.0, seen * _DropColor.a);
                // Finer rain: the street lamps and headlights it falls through light it, and the lightning
                // (0 by day / off). Added whatever the streaks' opacity: _RainDetail.w.
                o.light = (CityLightAt(head) * _RainDetail.y + RainFlash(head) * _RainDetail.z) * (seen * _RainDetail.w);

                float3 side = cross(fall, toCamera);
                side /= max(length(side), 1e-3);

                float3 world = head
                             - fall * streak.x * v.corner.y     // tail trails behind the head
                             + side * width * 0.5 * v.corner.x;

                // Hidden drops collapse outside clip space so they cost no fill.
                bool drawn = o.alpha > 0.001 || dot(o.light, float3(1.0, 1.0, 1.0)) > 0.0005;
                o.pos = drawn ? mul(UNITY_MATRIX_VP, float4(world, 1.0)) : float4(2, 2, 2, 1);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Soft across the width; bright head, fading tail.
                float across = 1.0 - i.corner.x * i.corner.x;
                float along = 1.0 - smoothstep(0.15, 1.0, i.corner.y);

                // A flake is round: soft from its middle to its edge, whichever way it falls.
                if (_Snow > 0.5)
                {
                    float2 q = float2(i.corner.x, i.corner.y * 2.0 - 1.0);
                    across = saturate(1.0 - dot(q, q));
                    along = 1.0;
                }

                float shape = across * along;
                float alpha = i.alpha * shape;
                return float4(_DropColor.rgb * alpha + i.light * shape, alpha);
            }
            ENDCG
        }
    }

    Fallback Off
}
