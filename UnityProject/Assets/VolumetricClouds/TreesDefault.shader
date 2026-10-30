// Custom/Trees/Default rebuilt (TreesDefault.cginc): the near trees, moved by VW's wind while it is on.
// Put on the game's tree materials by Sky/TreeShaders.cs and given back; only the game's normal view
// (no INFOMODE_* variants: the game's own shader is given back while an info view is open).
Shader "VolumetricClouds/TreesDefault"
{
	Properties
	{
		_MainTex ("Base", 2D) = "white" {}
		_XYCAMap ("XYCA", 2D) = "grey" {}
		_Color ("Colour", Color) = (1, 1, 1, 1)
	}

	SubShader
	{
		Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" }
		Cull Off

		CGPROGRAM
		#pragma target 3.5
		#pragma surface surf Lambert vertex:vert exclude_path:deferred exclude_path:forward nometa nolightmap nodynlightmap nodirlightmap noforwardadd
		#pragma multi_compile SHADING SNOW_ON
		#include "TreesDefault.cginc"
		ENDCG

		Pass
		{
			Name "ShadowCaster"
			Tags { "LightMode" = "ShadowCaster" }
			ZTest Less
			Cull Off

			CGPROGRAM
			#pragma target 3.5
			#pragma vertex vertCaster
			#pragma fragment fragCaster
			#include "UnityCG.cginc"
			#include "Lighting.cginc"
			#include "TreesDefault.cginc"
			ENDCG
		}
	}
	Fallback Off
}
