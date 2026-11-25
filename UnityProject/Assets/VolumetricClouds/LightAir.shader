// The lights' air (1.6.2, Sky/LightAir.cs): per texel, how much of another mod's light reaches the camera through
// our clouds, rain and fog. Run only while a mod hands in lights (Light Maker); nothing otherwise. The pass is
// LightAirPass.cginc.
Shader "VolumetricClouds/LightAir"
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
            // The extras' variant (Sky/CloudShaders.cs, VC_PLUS), as the cloud material has it.
            #pragma multi_compile __ VC_PLUS
            #include "LightAirPass.cginc"
            ENDCG
        }
    }

    Fallback Off
}
