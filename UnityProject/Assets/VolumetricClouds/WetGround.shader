// Wet ground and puddles (2026-11-15, the author: "move wet roads with puddles here into VW, and improve
// the way wet roads look"; Sky/WetGround.cs). One full-screen pass on the game's camera after its final
// pass (CameraEvent.AfterFinalPass): the lit picture is copied (_VWWetScene), then every pixel is drawn
// over again with Blend One SrcAlpha, ColorMask RGB -- the picture times "what the wet surface keeps" plus
// what the water reflects. The game's own fog (an opaque image effect) comes after and fogs it.
//
// Per pixel:
//   how wet   the ground's wetness (the game's m_groundWetness, x the row) x how level it lies, x a tenth
//             on bare terrain (2026-11-18: the game's surface, unclipped by roads and unpaved; WetTerrainMap)
//   what      hard (grey asphalt, concrete), porous (saturated: soil, brick) or leaves (green, or a tree's
//             thin pixel: the normals' alpha under 0.5), from its colour over the light's hue
//   darker    hard ground keeps 60%, porous ground by Lekner and Dorf (1988) per channel, leaves half that
//   puddles   on level hard ground, from a world pattern filling from its lowest as the wetness rises, the
//             fill leaning toward the low side of the slope (kerbs, a cambered road's edge); stable in the
//             world, they drain as the game dries the ground
//   rings     where it rains NOW (SampleRain), procedural rings in the puddles, on simulation time
//   mirror    the city traced in the depth buffer along the mirror ray; where nothing is met, the sky's
//             light from C# (ambient sky and horizon); Fresnel (water's F0 0.02); a rough film reads its
//             hit blurred by its cone, a puddle sharp
//   glint     the sun's highlight on the water, fading under cloud
//
// Nothing here is the game's code. Nothing writes the alpha (ColorMask RGB): other passes read it.
Shader "VolumetricClouds/WetGround"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One SrcAlpha
            ColorMask RGB

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5   // not 4.0: no Metal code at 4.0 in Unity 5.6 (see CloudRaymarch.shader)
            #include "UnityCG.cginc"
            #include "CloudCommon.cginc"

            sampler2D_float _CameraDepthTexture;
            sampler2D _CameraNormalsTexture;
            sampler2D _VWWetScene;

            float4 _VWWet;        // x wetness 0..1 (x the row), y puddles' share of level hard ground, z ripple clock (s), w 1 = trace
            float4 _VWWetSun;     // rgb the sun's light (linear, x its intensity), w the glint's share (0 under cloud / at night)
            float4 _VWWetSunDir;  // xyz toward the sun (world, unit)
            float4 _VWWetSky;     // rgb the sky's light near the zenith (linear), w the rain rings' strength
            float4 _VWWetHorizon; // rgb the sky's light near the horizon (linear), w the trace's reach (m)
            float4 _VWWetHue;     // rgb the light's hue to judge a colour against (sun by day, the city's lamps by night)
            float _VCProbe;       // 1 = the support check: return a fixed colour (Sky/WetGround.Supported)
            // The ground's height with its water (Sky/TerrainHeightMap: 256^2 over the 17.28 km map, metres):
            // what lies under the water's surface stays dry -- the water is drawn after this pass, over it.
            sampler2D _VWWetWater;
            sampler2D _VWWetPlaces;   // TerrainHeightMap.Places: g = how much of the ground nearby is water
            float _VWWetWaterOn;
            #define WET_MAP_SIZE 17280.0
            // Bare terrain (Sky/WetTerrainMap: the game's surface, unclipped and unpaved, round where the camera
            // looks): rg near (4 m cells over 2048 m: height above _VWWetTerrain.z, bare share), ba far (16 m
            // over 8192 m). _VWWetTerrain: xy the maps' centre (world xz), z their base height, w bare
            // terrain's share of the wetness (1 = no map yet: everything as wet as before).
            sampler2D _VWWetTerrainMap;
            float4 _VWWetTerrain;
            #define WET_NEAR_SPAN 2048.0
            #define WET_FAR_SPAN 8192.0

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = float4(v.vertex.xy, 0.5, 1.0);
                return o;
            }

            // The camera's own space at this screen point and depth.
            float3 ViewPos(float2 uv, float depth)
            {
                return float3((uv * 2.0 - 1.0) / float2(unity_CameraProjection._m00, unity_CameraProjection._m11) * depth, depth);
            }

            float2 UvOf(float3 q)
            {
                float2 ndc = float2(q.x * unity_CameraProjection._m00, q.y * unity_CameraProjection._m11) / max(q.z, 1e-3);
                return ndc * 0.5 + 0.5;
            }

            // The world normal without the normals buffer's 8-bit offset.
            float3 WorldNormal(float2 uv)
            {
                float3 n = tex2Dlod(_CameraNormalsTexture, float4(uv, 0.0, 0.0)).rgb * 2.0 - 1.0;
                n = (abs(n) > 1.5 / 255.0) * n;
                return normalize(n + float3(0.0, 1e-6, 0.0));
            }

            float WetHash(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            float WetNoise(float2 p)
            {
                float2 c = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(WetHash(c), WetHash(c + float2(1, 0)), f.x), lerp(WetHash(c + float2(0, 1)), WetHash(c + float2(1, 1)), f.x), f.y);
            }

            // 1 under a puddle: the world pattern (about 7 m across, 2-3 m detail) filled from its lowest
            // as the wetness rises toward the share puddles may cover. `low` (0..1) is how much this spot
            // lies at the low side of its slope: puddles gather there first.
            float Puddle(float3 wpos, float wet, float low)
            {
                float field = 0.65 * WetNoise(wpos.xz / 7.0) + 0.35 * WetNoise(wpos.xz / 2.3 + 17.0);
                // The pattern's high values are its "hollows" (they fill first); the flattest spots lean in.
                field = saturate(field + 0.18 * (low - 0.5));
                float from = 1.0 - _VWWet.y * smoothstep(0.3, 1.0, wet);
                return smoothstep(from, from + 0.05, field);
            }

            // What the ground is: x porous, y leaves (x >= y).
            float2 WetKind(float3 albedo, float thin)
            {
                float top = max(albedo.r, max(albedo.g, albedo.b));
                float bottom = min(albedo.r, min(albedo.g, albedo.b));
                float sat = (top - bottom) / max(top, 1e-6);
                float green = albedo.g / max(max(albedo.r, albedo.b), 1e-6);
                float leaves = max(smoothstep(1.03, 1.25, green) * smoothstep(0.12, 0.3, sat), thin);
                float porous = max(leaves, smoothstep(0.22, 0.45, sat));
                return float2(porous, leaves);
            }

            // How much of this spot is bare terrain lying on the ground (0 = a road, a paved or gravel lot,
            // or anything standing: a roof, a car, a bridge). Near map inside its span, far map beyond,
            // faded between; past the far map all of it counts as terrain (roads are a few pixels there).
            float BareTerrain(float3 wpos, float eye)
            {
                float2 rel = wpos.xz - _VWWetTerrain.xy;
                float2 nearUv = rel / WET_NEAR_SPAN + 0.5;
                float2 farUv = rel / WET_FAR_SPAN + 0.5;
                float4 m = tex2Dlod(_VWWetTerrainMap, float4(nearUv, 0.0, 0.0));
                float4 f = tex2Dlod(_VWWetTerrainMap, float4(farUv, 0.0, 0.0));
                float2 nearEdge = 0.5 - abs(nearUv - 0.5);
                float2 farEdge = 0.5 - abs(farUv - 0.5);
                float nearIn = smoothstep(0.0, 0.05, min(nearEdge.x, nearEdge.y));
                float farIn = smoothstep(0.0, 0.03, min(farEdge.x, farEdge.y));
                float above = wpos.y - _VWWetTerrain.z;
                // On the ground: within ~1.5 m of the near map's 4 m heights, more for the far map's 16 m ones
                // and with distance (the terrain's mesh is triangles, the map bilinear).
                float nearOn = 1.0 - smoothstep(1.0, 2.0 + 0.002 * eye, above - m.r);
                float farOn = 1.0 - smoothstep(2.0, 4.0 + 0.004 * eye, above - f.b);
                float farBare = lerp(1.0, f.a * farOn, farIn);
                return lerp(farBare, m.g * nearOn, nearIn);
            }

            // Lekner and Dorf 1988, per channel: the soaked / dry ratio of a diffuse surface of albedo a.
            float3 LeknerDorf(float3 albedo)
            {
                return 0.49315 / (1.0 - 0.472 * saturate(albedo));
            }

            // Schlick's Fresnel for water (F0 0.02), with roughness damping the grazing rise (Lagarde).
            float Fresnel(float nv, float rough)
            {
                float f0 = 0.02;
                float grazing = max(1.0 - rough, f0);
                return f0 + (grazing - f0) * pow(1.0 - nv, 5.0);
            }

            // The sky the water mirrors where the trace meets nothing: horizon to zenith.
            float3 SkyLight(float3 r)
            {
                float up = saturate(r.y);
                return lerp(_VWWetHorizon.rgb, _VWWetSky.rgb, sqrt(up));
            }

            // Rain rings in a puddle: two layers of cells 0.6 m wide, each with a drop landing at its own
            // moment; returns the normal's tilt (xz) at the spot.
            float2 Rings(float2 xz, float t)
            {
                float2 tilt = 0;
                [unroll]
                for (int layer = 0; layer < 2; layer++)
                {
                    float2 p = xz / 0.6 + layer * float2(0.37, 0.71);
                    // The four cells nearest the spot: a ring reaches at most half a cell out.
                    float2 base = floor(p - 0.5);
                    [unroll]
                    for (int k = 0; k < 4; k++)
                    {
                        float2 cid = base + float2(k & 1, k >> 1);
                        float h = WetHash(cid + layer * 31.0);
                        float2 centre = cid + float2(WetHash(cid + 7.0), WetHash(cid + 13.0));
                        float age = frac(t * (0.9 + 0.5 * h) + h);   // 0..1 over ~0.8 s
                        float2 d = p - centre;
                        float dist = length(d) * 0.6;                // metres
                        float radius = age * 0.3;
                        float ring = exp(-pow((dist - radius) / 0.025, 2.0)) * (1.0 - age) * (1.0 - age);
                        // The ring's slope: outward on its outer side, inward on its inner side.
                        float slope = (dist - radius) / 0.025;
                        tilt += d / max(length(d), 1e-4) * (-slope * ring);
                    }
                }
                return tilt * 0.12;
            }

            // The city met by the mirror ray, by the depth buffer: rgb and how sure (0 = nothing met).
            float4 Trace(float3 p, float3 d, float start, float cone)
            {
                float steps = _VWWet.w > 1.5 ? 32.0 : 20.0;
                float reach = _VWWetHorizon.w;
                float len = max(0.25, 0.01 * p.z) * (0.5 + start);
                float travelled = 0.0;
                float3 last = p;
                float4 found = float4(0, 0, 0, 0);
                [loop]
                for (int k = 0; k < 32; k++)
                {
                    if (k >= steps)
                        break;
                    float3 q = last + d * len;
                    travelled += len;
                    if (q.z < 0.3 || travelled > reach)
                        break;
                    float2 uv = UvOf(q);
                    if (any(uv < 0.0) || any(uv > 1.0))
                        break;
                    float scene = LinearEyeDepth(tex2Dlod(_CameraDepthTexture, float4(uv, 0.0, 0.0)).r);
                    float behind = q.z - scene;
                    if (behind > 0.0 && behind < max(0.5, 0.03 * scene) + 2.0 * len)
                    {
                        float3 a = last, b = q;
                        [unroll]
                        for (int r = 0; r < 4; r++)
                        {
                            float3 m = 0.5 * (a + b);
                            float sm = LinearEyeDepth(tex2Dlod(_CameraDepthTexture, float4(UvOf(m), 0.0, 0.0)).r);
                            if (m.z > sm) b = m; else a = m;
                        }
                        float2 hit = UvOf(b);
                        float2 edge = min(hit, 1.0 - hit);
                        float sure = smoothstep(0.0, 0.06, min(edge.x, edge.y)) * (1.0 - smoothstep(0.7, 1.0, travelled / reach));
                        float rawHit = tex2Dlod(_CameraDepthTexture, float4(hit, 0.0, 0.0)).r;
                        sure *= Linear01Depth(rawHit) < 0.9999 ? 1.0 : 0.0;
                        // A rough film reads its hit blurred by its cone: four taps, the cone's width apart.
                        float across = min(24.0, length(b - p) * cone * 0.5 * _ScreenParams.y * unity_CameraProjection._m11 / max(b.z, 0.3));
                        float2 px = across / _ScreenParams.xy;
                        float3 c = tex2Dlod(_VWWetScene, float4(hit, 0.0, 0.0)).rgb;
                        UNITY_BRANCH
                        if (across > 1.0)
                        {
                            c = 0.4 * c
                              + 0.15 * tex2Dlod(_VWWetScene, float4(hit + px * float2(0.7, 0.2), 0.0, 0.0)).rgb
                              + 0.15 * tex2Dlod(_VWWetScene, float4(hit + px * float2(-0.2, 0.7), 0.0, 0.0)).rgb
                              + 0.15 * tex2Dlod(_VWWetScene, float4(hit + px * float2(-0.7, -0.2), 0.0, 0.0)).rgb
                              + 0.15 * tex2Dlod(_VWWetScene, float4(hit + px * float2(0.2, -0.7), 0.0, 0.0)).rgb;
                        }
                        found = float4(c, sure);
                        break;
                    }
                    last = q;
                    len *= 1.12;
                }
                return found;
            }

            float4 frag(v2f i) : SV_Target
            {
                // The support check (Sky/WetGround.Supported): a fixed colour, whole.
                if (_VCProbe > 0.5)
                    return float4(0.25, 0.5, 0.75, 1.0);

                float2 uv = i.pos.xy / _ScreenParams.xy;
                float raw = tex2Dlod(_CameraDepthTexture, float4(uv, 0.0, 0.0)).r;
                float eye = LinearEyeDepth(raw);
                float3 pv = ViewPos(uv, eye);
                float3 wpos = mul(unity_CameraToWorld, float4(pv, 1.0)).xyz;

                // Which side of its slope this spot lies on, from the world height's change across the
                // pixel (screen derivatives: before any branch).
                float rise = length(float2(ddx(wpos.y), ddy(wpos.y)));
                float run = length(float2(length(ddx(wpos.xz)), length(ddy(wpos.xz))));

                // The sky (nothing drawn yet there at this event) and anything at the far plane: untouched.
                if (Linear01Depth(raw) > 0.9999)
                    return float4(0, 0, 0, 1);

                float3 n = WorldNormal(uv);
                float3 v = normalize(_WorldSpaceCameraPos - wpos);

                // Level and open surfaces most, walls a little.
                float wet = saturate(_VWWet.x) * lerp(0.12, 1.0, smoothstep(0.3, 0.8, n.y));
                // Not under the water: the seabed, the river bed (dry again over 0.5 m above its surface).
                // Only where there is water near (the map's g): on land the 67 m map's height is the ground
                // smoothed, which a road cut or a slope lies under, and that must stay wet.
                if (_VWWetWaterOn > 0.5)
                {
                    float2 muv = wpos.xz / WET_MAP_SIZE + 0.5;
                    float waterNear = tex2Dlod(_VWWetPlaces, float4(muv, 0.0, 0.0)).g;
                    float surface = tex2Dlod(_VWWetWater, float4(muv, 0.0, 0.0)).r;
                    wet *= lerp(1.0, smoothstep(-0.3, 0.5, wpos.y - surface), smoothstep(0.02, 0.15, waterNear));
                }
                // Bare terrain keeps a tenth of the wetness (it washed the grass and soil out), no puddles.
                float bare = 0.0;
                UNITY_BRANCH
                if (_VWWetTerrain.w < 1.0)
                {
                    bare = BareTerrain(wpos, eye);
                    wet *= lerp(1.0, _VWWetTerrain.w, bare);
                }
                if (wet <= 0.002)
                    return float4(0, 0, 0, 1);

                float3 dry = tex2Dlod(_VWWetScene, float4(uv, 0.0, 0.0)).rgb;
                float thin = step(tex2Dlod(_CameraNormalsTexture, float4(uv, 0.0, 0.0)).a, 0.5);

                // Its colour over the light's hue, its brightest channel taken as a mid albedo.
                float3 hue = _VWWetHue.rgb + 0.2 * dot(_VWWetHue.rgb, float3(0.2126, 0.7152, 0.0722)) + 1e-4;
                float3 c = dry / hue;
                float3 albedo = 0.3 * c / max(max(c.r, max(c.g, c.b)), 1e-6);
                float2 kind = WetKind(albedo, thin);
                float porous = kind.x, leaves = kind.y;

                // Puddles gather where the ground is flattest (the low, level part of a cambered road, the
                // kerb's gutter) first: the slope across the pixel against a road's typical 1-2% camber.
                float slope = rise / max(run, 1e-3);
                float low = saturate(1.0 - slope * 25.0);

                float puddle = n.y > 0.97 ? Puddle(wpos, saturate(_VWWet.x), low) * (1.0 - porous) * (1.0 - bare) : 0.0;

                // Raindrops' rings in the puddles, where it rains now.
                float3 nw = normalize(lerp(n, float3(0, 1, 0), puddle));
                UNITY_BRANCH
                if (puddle > 0.05 && _VWWetSky.w > 0.0)
                {
                    float rain = SampleRain(wpos);
                    UNITY_BRANCH
                    if (rain > 0.02)
                    {
                        float2 tilt = Rings(wpos.xz, _VWWet.z) * saturate(rain * 2.0) * _VWWetSky.w * puddle;
                        nw = normalize(nw + float3(tilt.x, 0.0, tilt.y));
                    }
                }

                float nv = max(dot(nw, v), 1e-4);
                // Rough: a hard surface's film 0.2, porous ground 0.45, a puddle 0.03.
                float rough = lerp(lerp(0.2, 0.45, porous), 0.03, puddle);
                float alphaW = rough * rough;
                // The share of it a film of water covers: all on hard ground and in a puddle.
                float film = lerp(1.0, lerp(0.55, 0.3, leaves), porous * (1.0 - puddle));

                float3 r = reflect(-v, nw);
                float3 reflected = SkyLight(r);
                float traceWeight = (1.0 - porous * (1.0 - puddle)) * (r.y > -0.05 ? 1.0 : 0.0);
                UNITY_BRANCH
                if (_VWWet.w > 0.5 && traceWeight > 0.05)
                {
                    float noise = frac(52.9829189 * frac(dot(i.pos.xy, float2(0.06711056, 0.00583715))));
                    float cone = 2.0 * alphaW / max(1.0 - alphaW * alphaW, 1e-3);
                    float3 rv = mul((float3x3)unity_WorldToCamera, r);
                    float4 traced = Trace(pv + mul((float3x3)unity_WorldToCamera, nw) * (0.002 * pv.z), rv, frac(noise * 3.7), cone);
                    reflected = lerp(reflected, traced.rgb, traced.a * traceWeight);
                }

                float share = Fresnel(nv, rough) * film;

                // The sun's glint on the water (GGX), when the sun is out.
                float3 l = normalize(_VWWetSunDir.xyz);
                float3 h = normalize(l + v);
                float nh = saturate(dot(nw, h));
                float nl = saturate(dot(nw, l));
                float a2 = max(alphaW * alphaW, 1e-5);
                float dd = nh * nh * (a2 - 1.0) + 1.0;
                float ggx = a2 / max(3.14159 * dd * dd, 1e-5);
                float3 glint = _VWWetSun.rgb * (ggx * nl * 0.02 * film * _VWWetSun.w / max(4.0 * nv, 0.1));

                // What the surface keeps of its own light: hard ground 60%, porous by Lekner and Dorf, leaves
                // half of that darkening, a puddle 45%.
                float3 ground = lerp(LeknerDorf(albedo), 1.0, 0.5 * leaves);
                float3 keep = lerp(lerp((0.6).xxx, ground, porous), (0.45).xxx, puddle);
                float least = min(keep.r, min(keep.g, keep.b));

                // The blend scales what is there by one number (alpha); the rest of each channel's share is
                // added from the copy.
                float4 wetResult = float4(reflected * share + glint + dry * (keep - least) * (1.0 - share), least * (1.0 - share));
                return lerp(float4(0, 0, 0, 1), wetResult, wet);
            }
            ENDCG
        }
    }

    Fallback Off
}
