// The cloud column map (1.6.1, Sky/CloudColumnMap.cs): per texel, a column of sky marched straight up through the
// clouds' own density, read back about once a second while lightning can happen, so the bolts start in the drawn
// clouds and the sideways ones run along their underside. The pass is CloudColumnMapPass.cginc.
Shader "VolumetricClouds/CloudColumnMap"
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
            // The extras' variant (Sky/CloudShaders.cs, VC_PLUS), on exactly while the shadow map's is: the density the
            // clouds are drawn with.
            #pragma multi_compile __ VC_PLUS
            #include "CloudColumnMapPass.cginc"
            ENDCG
        }
    }

    Fallback Off
}
