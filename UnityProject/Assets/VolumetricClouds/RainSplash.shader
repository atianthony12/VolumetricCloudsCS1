// Splashes (2026-11-15, "Finer rain"; the author, after Torrent: "drops burst on the ground ... as fine
// spray"). A cloud of slots round the camera, each a splash that lives a quarter of a second and is born
// again somewhere else (Sky/RainDrops.cs builds the mesh and sets the clock). Stateless:
//   where      a world-anchored spot in a 30 m box round the camera, new each life (hash of the life)
//   on what    the spot at the ground's height under the camera is projected to the screen and the depth
//              there read (in the vertex function): the surface seen along that ray is where the drop
//              lands -- accepted only within 0.8 m of the spot sideways (the ground, a low flat roof),
//              so nothing splashes in mid air, on a wall or behind a building
//   how many   as the streaks: a slot shows where the local rain (SampleRain) is above its rank
//   looks      a small crown of droplets thrown up and out, gone in a quarter second, lit like the rain
//              (the sky's light + the city's lamps, LampMap.cginc)
//
// Mesh layout, four vertices a slot:
//   POSITION   the slot's seed, 0..1 on each axis
//   TEXCOORD0  the corner: x -1 / +1, y 0 / 1
//   TEXCOORD1  x = rank, y = its clock's offset, z = size variation
Shader "VolumetricClouds/RainSplash"
{
    SubShader
    {
        // After the spray (Transparent+105), before the streaks (Transparent+110).
        Tags { "Queue" = "Transparent+108" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

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
            #include "LampMap.cginc"
            #include "RainCover.cginc"

            sampler2D_float _CameraDepthTexture;

            float4 _SplashParams;   // x clock (s, simulation time), y the ground's height under the camera, z box (m), w lives a second
            float4 _DropColor;      // the streaks' colour and opacity (RainDrops.cs StreakLook)
            float _DropDensity;     // the streaks' row
            float _DropFade;        // from the camera's height
            float4 _RainDetail;     // y = how much of the lamps' light (as the streaks)

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
                float4 info : TEXCOORD1;     // x alpha, y age 0..1, z seed, w -
                float3 light : TEXCOORD2;
            };

            float SplashHash(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            v2f vert(appdata v)
            {
                v2f o;
                float box = _SplashParams.z;

                // This slot's life, and a new spot for each one.
                float t = _SplashParams.x * _SplashParams.w + v.random.y;
                float life = floor(t);
                float age = t - life;
                float2 jump = float2(SplashHash(float2(life, v.random.y * 97.0)), SplashHash(float2(life + 17.0, v.random.z * 61.0)));
                float2 rel = (frac(v.vertex.xz + jump - _WorldSpaceCameraPos.xz / box + 0.5) - 0.5) * box;
                float3 guess = float3(_WorldSpaceCameraPos.x + rel.x, _SplashParams.y, _WorldSpaceCameraPos.z + rel.y);

                // What the camera sees along the ray through that spot: there the drop lands.
                float4 clipGuess = mul(UNITY_MATRIX_VP, float4(guess, 1.0));
                // Unity's own screen position (it handles the flip of a target drawn top row first).
                float4 screen = ComputeScreenPos(clipGuess);
                float2 uv = screen.xy / max(screen.w, 1e-4);
                float onScreen = (clipGuess.w > 0.3 && all(uv > 0.0) && all(uv < 1.0)) ? 1.0 : 0.0;
                float raw = tex2Dlod(_CameraDepthTexture, float4(saturate(uv), 0, 0)).r;
                float sceneEye = LinearEyeDepth(raw);
                float guessEye = -mul(UNITY_MATRIX_V, float4(guess, 1.0)).z;
                float3 hit = _WorldSpaceCameraPos + (guess - _WorldSpaceCameraPos) * (sceneEye / max(guessEye, 1e-3));
                float sideways = length(hit.xz - guess.xz);
                float landed = onScreen * (sideways < 0.8 ? 1.0 : 0.0) * (Linear01Depth(raw) < 0.999 ? 1.0 : 0.0);

                // As many as the streaks: where it rains, by rank.
                float local = SampleRain(hit);
                float shown = saturate((local * _DropDensity - v.random.x) * 8.0);
                // No splash where a roof or a bridge keeps the rain off (Sky/RainCover.cs).
                shown *= 1.0 - RainCovered(hit + float3(0.0, 0.05, 0.0), float3(0.0, -1.0, 0.0));
                float dist = length(hit - _WorldSpaceCameraPos);
                float near = 1.0 - smoothstep(box * 0.35, box * 0.5, dist);
                float alpha = landed * shown * near * _DropFade * saturate(_DropColor.a * 1.6) * (1.0 - age) * (1.0 - age);

                // An upright card facing the camera across, the crown rising out of the surface.
                float size = lerp(0.12, 0.3, v.random.z) * lerp(0.6, 1.0, saturate(age * 3.0));
                float3 toCamera = _WorldSpaceCameraPos - hit;
                float3 across = normalize(cross(float3(0, 1, 0), toCamera) + float3(1e-4, 0, 0));
                float3 world = hit + across * (v.corner.x * size) + float3(0, 1, 0) * (v.corner.y * size * 0.9 - 0.02);

                float4 clip = mul(UNITY_MATRIX_VP, float4(world, 1.0));
                o.pos = alpha > 0.002 ? clip : float4(2, 2, 2, 1);
                o.corner = v.corner;
                o.info = float4(alpha, age, v.random.z, 0);
                o.light = CityLightAt(hit + float3(0, 0.3, 0)) * _RainDetail.y;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                // A crown: a few droplets flung up and out along an arc that widens and falls with age.
                float age = i.info.y;
                float2 p = float2(i.corner.x, i.corner.y);   // x -1..1 across, y 0..1 up
                float crown = 0.0;
                [unroll]
                for (int k = 0; k < 5; k++)
                {
                    float side = (k - 2.0) / 2.0;                       // -1..1
                    float reach = side * lerp(0.25, 0.9, age);
                    float rise = (1.0 - side * side) * (0.7 * age - 0.9 * age * age) * 2.2 + 0.08;
                    float2 d = p - float2(reach, rise);
                    crown += exp(-dot(d, d) * 260.0);
                }
                // The splash's foot: a thin bright sheet on the surface at its start.
                crown += exp(-pow(p.y / 0.08, 2.0)) * (1.0 - smoothstep(0.0, 0.35, age)) * (1.0 - p.x * p.x);

                float3 colour = _DropColor.rgb * 1.2 + i.light;
                return float4(colour, saturate(crown) * i.info.x);
            }
            ENDCG
        }
    }

    Fallback Off
}
