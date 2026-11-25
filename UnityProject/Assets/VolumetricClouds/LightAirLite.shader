// The lights' air for "Simpler clouds" (Sky/CloudShaders.cs): LightAir.shader's pass compiled with VC_LITE, the
// density the light cloud shader draws (as CloudColumnMapLite.shader for the column map).
Shader "VolumetricClouds/LightAirLite"
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
            #include "LightAirPass.cginc"
            ENDCG
        }
    }

    Fallback Off
}
