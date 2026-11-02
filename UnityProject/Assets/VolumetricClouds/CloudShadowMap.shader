// Renders the cloud shadow map that gets assigned as the sun's light cookie.
//
// Unity projects a directional cookie in the light's local XY plane, centred on the light:
//     uv = localXY / cookieSize + 0.5
// so every texel corresponds to one ray parallel to the sun. This pass walks that ray
// through the cloud slab -- using the very same SampleDensity as the visible clouds -- and
// writes how much sunlight survives. Assigned as the cookie, it lands each cloud's shadow
// exactly where the sun would put it.
//
// The built-in lighting passes read the cookie's ALPHA channel.
Shader "VolumetricClouds/CloudShadowMap"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.0
            // The pass itself (shared with CloudShadowMapLite.shader, which defines VC_LITE first).
            #include "CloudShadowMapPass.cginc"
            ENDCG
        }
    }

    Fallback Off
}
