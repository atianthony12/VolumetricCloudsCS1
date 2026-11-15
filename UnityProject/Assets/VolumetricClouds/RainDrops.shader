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
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5   // not 4.0: no Metal code at 4.0 in Unity 5.6 (see CloudRaymarch.shader)
            #include "UnityCG.cginc"
            #include "CloudCommon.cginc"
            #include "RainGust.cginc"
            #include "LampMap.cginc"
            #include "RainCover.cginc"

            // "Wind on the rain" (2026-11-15). x, z = the wind's push per metre of fall for a medium drop
            // (wind speed / fall speed, x the row), y the gust push's length (m), w 1 = on. 0 (unset) =
            // the streaks exactly as before: _FallDirection and _FallOffset alone.
            float4 _RainWind;
            // xz = how far the air has carried the rain so far (m, wrapped in C# to whole boxes), y the
            // heavier bands' strength in gusts.
            float4 _RainWindOffset;
            // "Finer rain": x 1 = drop sizes (small, medium, large) and a few drops right at the lens; y = how much
            // of the city's lamp light (LampMap.cginc) a drop catches (0 = none: by day, or Finer rain off).
            float4 _RainDetail;

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
                float size = detail ? (v.random.z < 0.45 ? 0.6 : (v.random.z > 0.88 ? 1.6 : 1.0)) : 1.0;
                bool lens = detail && isFar < 0.5 && frac(v.random.x * 7.31) < 0.14;
                box = lens ? float3(12.0, 9.0, 12.0) : box;
                streak *= float2(detail ? lerp(0.85, 1.25, saturate(size - 0.6)) : 1.0, size);

                // World-anchored: the seed plus the fall so far is a WORLD position; wrapping
                // it relative to the camera keeps the drop in the box without tying it to it.
                // With wind on the rain the air carries every drop the same way (_RainWindOffset).
                bool windy = _RainWind.w > 0.5;
                float3 fallen = _FallOffset * (0.85 + 0.3 * v.random.z);
                fallen = windy ? float3(_RainWindOffset.x, fallen.y, _RainWindOffset.z) : fallen;
                float3 rel = (frac(v.vertex.xyz + (fallen - _WorldSpaceCameraPos) / box + 0.5) - 0.5) * box;

                // The gust where the drop is: it pushes the drop downwind a bounded way (so the wrap
                // never drifts) and leans it further; the front rolls through with the field.
                float3 fall = _FallDirection;
                float gust = 0.5;
                if (windy)
                {
                    float2 place = _WorldSpaceCameraPos.xz + rel.xz;
                    gust = RainGustAt(place);
                    float2 windUnit = _RainWind.xz / max(length(_RainWind.xz), 1e-4);
                    // At most 15% of the box each way, so a pushed drop stays inside its own box.
                    rel.xz += windUnit * (min(_RainWind.y, box.x * 0.3) * (gust - 0.5));
                    float push = 1.0 + _RainGust.w * (2.0 * gust - 1.0);
                    // A large drop falls faster and leans less; a small one more.
                    float lean = push / lerp(0.8, 1.25, saturate((size - 0.6) / 1.0));
                    fall = normalize(float3(_RainWind.x * lean, -1.0, _RainWind.z * lean));
                    // A slanted streak is a longer path in the same moment.
                    streak.x *= min(3.0, 1.0 / max(-fall.y, 0.2));
                }

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

                // Fade towards the box faces so wrapping never pops, and right at the lens so a
                // drop never smears across the screen.
                float3 edge = abs(rel) / (box * 0.5);
                float boxFade = 1.0 - smoothstep(0.75, 1.0, max(edge.x, max(edge.y, edge.z)));
                float lensFade = smoothstep(0.4, 2.0, dist);

                o.alpha = shown * boxFade * lensFade * widened * _DropFade * _DropColor.a;
                // Finer rain: the street lamps and headlights it falls through light it (0 by day / off).
                o.light = CityLightAt(head) * _RainDetail.y;

                float3 side = cross(fall, toCamera);
                side /= max(length(side), 1e-3);

                float3 world = head
                             - fall * streak.x * v.corner.y     // tail trails behind the head
                             + side * width * 0.5 * v.corner.x;

                // Hidden drops collapse outside clip space so they cost no fill.
                o.pos = o.alpha > 0.001 ? mul(UNITY_MATRIX_VP, float4(world, 1.0)) : float4(2, 2, 2, 1);
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

                return float4(_DropColor.rgb + i.light, i.alpha * across * along);
            }
            ENDCG
        }
    }

    Fallback Off
}
