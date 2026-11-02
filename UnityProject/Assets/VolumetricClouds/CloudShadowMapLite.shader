// The cloud shadow map for "Simpler clouds" (Sky/CloudShaders.cs): CloudShadowMap.shader's pass compiled
// with VC_LITE -- the same SampleDensity without Curved clouds, the far sky, Soft or Mixed, as
// CloudRaymarchLite.shader draws them. With those off (the C# side makes sure of it) it writes exactly
// what CloudShadowMap does.
Shader "VolumetricClouds/CloudShadowMapLite"
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
            #define VC_LITE 1
            #include "CloudShadowMapPass.cginc"
            ENDCG
        }
    }

    Fallback Off
}
