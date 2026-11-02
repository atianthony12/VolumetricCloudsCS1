// "Simpler clouds" (Sky/CloudShaders.cs): the cloud pass (CloudRaymarchPass.cginc) compiled with VC_LITE,
// about 1.4.0's size and its 11 textures. Drawn when this machine's graphics driver cannot run the whole
// of CloudRaymarch.shader (found by drawing it once at a city's load), or when the player ticks "Simpler
// clouds". Same render state and queue as CloudRaymarch: CloudVolume sets both materials the same way.
Shader "VolumetricClouds/CloudRaymarchLite"
{
    Properties
    {
        _WeatherTex ("Weather (R)", 2D) = "black" {}
        _NoiseTex ("Noise (RG)", 3D) = "" {}
    }

    SubShader
    {
        Tags { "Queue" = "Transparent+100" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha   // premultiplied

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // 3.5, never 4.0 (an EMPTY Metal blob): see CloudRaymarch.shader.
            #pragma target 3.5
            // No funnels variant: the waterspouts do not form while this shader is drawn.
            #define VC_LITE 1
            #include "CloudRaymarchPass.cginc"
            ENDCG
        }
    }

    Fallback Off
}
