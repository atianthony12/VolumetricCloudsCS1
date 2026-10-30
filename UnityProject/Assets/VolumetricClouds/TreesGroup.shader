// Custom/Trees/Group rebuilt (TreesGroup.cginc): the mid and far trees' billboards, moved by VW's wind
// while it is on. Put on the game's materials by Sky/TreeShaders.cs and given back; only the game's
// normal view (no INFOMODE_* variants: the game's own shader is given back while an info view is open).
Shader "VolumetricClouds/TreesGroup"
{
	Properties
	{
		_MainTex ("Base", 2D) = "white" {}
		_XYCAMap ("XYCA", 2D) = "grey" {}
		_ShadowAMap ("Shadow alpha", 2D) = "white" {}
	}

	SubShader
	{
		Tags { "Queue" = "AlphaTest" "RenderType" = "TransparentCutout" }
		Cull Off

		CGPROGRAM
		#pragma target 3.5
		#pragma surface surf TreeLod vertex:vert exclude_path:deferred exclude_path:forward nometa nolightmap nodynlightmap nodirlightmap noforwardadd
		#pragma multi_compile SINGLE_INSTANCE MULTI_INSTANCE
		#include "TreesGroup.cginc"
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
			#pragma multi_compile SINGLE_INSTANCE MULTI_INSTANCE
			#include "UnityCG.cginc"
			#include "Lighting.cginc"
			#include "TreesGroup.cginc"
			ENDCG
		}
	}
	Fallback Off
}
