// Raymarched volumetric cloud layer for Cities: Skylines (Unity 5.6, built-in pipeline).
//
// Drawn on a box that follows the camera, so every pixel gets a fragment. Each fragment
// intersects its view ray with the cloud slab [_CloudBottom, _CloudTop] and marches it, then
// marches the rain hanging underneath: one pass, one view ray, one depth read, so the two
// media composite correctly against each other and the scene. (With the camera under the
// cloud base the same pass is drawn twice, clouds and air apart: see _DrawPart.)
//
//   _WeatherTex : 2D tiling density field, the same one the shadow cookie is cut from.
//                 Thresholded here with _Threshold/_Softness so coverage is one uniform.
//   _NoiseTex   : 3D tiling noise. R = Perlin-Worley base shape, G = Worley detail.
Shader "VolumetricClouds/CloudRaymarch"
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
            // 3.5, not 4.0: Unity 5.6 builds NO Metal code at target 4.0 (a geometry-shader
            // level, unsupported on Metal: an EMPTY blob, no error). 3.5 is the same SM4.0
            // on D3D11 (blobs byte-identical), OpenGL 3.3, and Metal. tools/bundle-apis.ps1.
            #pragma target 3.5
            // The waterspouts' funnels (1.5.0, FunnelShape.cginc) exist only in this
            // variant, switched on on the air's material while there is a funnel (Sky/CloudVolume.cs):
            // in the pass it raised every AMD chip's registers ~18% (fewer waves in flight on every
            // frame, a funnel or not). Without it the pass is what it was.
            #pragma multi_compile __ VC_FUNNELS
            // The pass itself (shared with CloudRaymarchLite.shader, which defines VC_LITE first).
            #include "CloudRaymarchPass.cginc"
            ENDCG
        }
    }

    Fallback Off
}
