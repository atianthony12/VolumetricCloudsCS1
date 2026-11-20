// The cloud column map for "Simpler clouds" (Sky/CloudShaders.cs): CloudColumnMap.shader's pass compiled with VC_LITE,
// the same SampleDensity the light cloud shader draws (as CloudShadowMapLite.shader for the shadows).
Shader "VolumetricClouds/CloudColumnMapLite"
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
            #include "CloudColumnMapPass.cginc"
            ENDCG
        }
    }

    Fallback Off
}
