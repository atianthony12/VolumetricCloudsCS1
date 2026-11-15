// Spray from wheels (2026-11-15, Sky/WheelSpray.cs): droplets, fine spray and mist thrown off wet roads
// by the nearest moving cars. One dynamic mesh, four vertices a particle, all in WORLD space:
//   POSITION   the particle's centre (all four corners share it)
//   NORMAL     droplets: their flight over a short moment (m), the streak they draw; else zero
//   TEXCOORD0  the corner (-1 / +1, -1 / +1)
//   TEXCOORD1  x size (m across), y = opacity + 2 x kind (0 droplet, 1 spray, 2 mist)
// Billboards facing the camera, faded where they meet the scene (soft particles) and toward the lens.
// Drawn after the air (the rain and fog over the city, Transparent+95) and before the rain streaks
// (Transparent+110): a car's spray is near the camera, the streaks nearer still.
Shader "VolumetricClouds/WheelSpray"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+105" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

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

            sampler2D_float _CameraDepthTexture;
            float4 _SprayLight;   // rgb the light it is seen in (linear)

            struct appdata
            {
                float4 vertex : POSITION;
                float3 stretch : NORMAL;
                float2 corner : TEXCOORD0;
                float2 look : TEXCOORD1;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 corner : TEXCOORD0;
                float3 info : TEXCOORD1;    // x opacity, y kind, z eye depth
                float4 screen : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float kind = floor(v.look.y * 0.5);
                float opacity = v.look.y - kind * 2.0;
                float3 centre = v.vertex.xyz;
                float3 toCamera = _WorldSpaceCameraPos - centre;
                float dist = length(toCamera);
                float3 forward = toCamera / max(dist, 1e-3);

                // A droplet is a short streak along its flight (never under a pixel's width); the rest round.
                float pixel = 2.0 / max(abs(unity_CameraProjection._m11) * _ScreenParams.y, 1e-3);
                float size = max(v.look.x, dist * pixel * 1.2);
                float3 along = v.stretch;
                bool streak = kind < 0.5 && dot(along, along) > 1e-6;
                float3 axis = streak ? normalize(along) : normalize(cross(forward, float3(0, 1, 0.001)));
                float3 side = normalize(cross(axis, forward));
                float halfLength = streak ? max(length(along) * 0.5, size) : size * 0.5;

                float3 world = centre + axis * (v.corner.y * halfLength) + side * (v.corner.x * size * 0.5);
                // Faded right at the lens.
                opacity *= smoothstep(0.5, 2.5, dist);

                // Dead particles collapse outside clip space (no fill); one write of the output.
                float4 clip = mul(UNITY_MATRIX_VP, float4(world, 1.0));
                o.pos = opacity > 0.001 ? clip : float4(2, 2, 2, 1);
                o.corner = v.corner;
                o.info = float3(opacity, kind, -mul(UNITY_MATRIX_V, float4(world, 1.0)).z);
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 q = i.corner;
                float r2 = dot(q, q);
                // Droplets: a thin bright line; spray and mist: soft round puffs.
                float shape = i.info.y < 0.5 ? (1.0 - q.x * q.x) * (1.0 - smoothstep(0.3, 1.0, abs(q.y)))
                                             : saturate(1.0 - r2) * saturate(1.0 - r2);

                // Soft where it meets the scene: a puff of mist does not cut a hard line on the road.
                float scene = LinearEyeDepth(tex2Dproj(_CameraDepthTexture, UNITY_PROJ_COORD(i.screen)).r);
                float soft = saturate((scene - i.info.z) / (i.info.y > 1.5 ? 1.5 : 0.4));

                float alpha = i.info.x * shape * soft;
                // Water catches more light along a droplet's length: a little brighter.
                float3 colour = _SprayLight.rgb * (i.info.y < 0.5 ? 1.3 : 1.0);
                return float4(colour, alpha);
            }
            ENDCG
        }
    }

    Fallback Off
}
